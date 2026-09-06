// BlazorScanner browser bridge.
//
// This file is deliberately small. Everything that can be done in C# is done in C#: the only
// jobs left here are the ones the browser will not let managed code do at all, which are opening
// a camera, keeping a <video> element fed, and copying the pixels of the latest frame.
//
// The pixel copy writes straight into a .NET owned buffer through a MemoryView, so a frame
// crosses the boundary once, with no intermediate array, no JSON and no base64.

const state = {
    sessions: new Map(),
};

function getSession(id) {
    let session = state.sessions.get(id);
    if (!session) {
        session = {
            id,
            stream: null,
            track: null,
            video: null,
            canvas: null,
            ctx: null,
            canvasWidth: 0,
            canvasHeight: 0,
            hasNewFrame: false,
            frameCallbackHandle: 0,
            prepared: null,
        };
        state.sessions.set(id, session);
    }

    return session;
}

function releaseStream(session) {
    if (session.frameCallbackHandle && session.video && session.video.cancelVideoFrameCallback) {
        session.video.cancelVideoFrameCallback(session.frameCallbackHandle);
    }

    session.frameCallbackHandle = 0;

    if (session.stream) {
        for (const track of session.stream.getTracks()) {
            track.stop();
        }
    }

    if (session.video) {
        session.video.srcObject = null;
    }

    session.stream = null;
    session.track = null;
    session.hasNewFrame = false;
}

function ensureCanvas(session, width, height) {
    if (session.canvas && session.canvasWidth === width && session.canvasHeight === height) {
        return;
    }

    // An OffscreenCanvas keeps the pixels off the compositor entirely. Where it is missing the
    // detached <canvas> behaves the same, it just costs a little more memory.
    session.canvas = typeof OffscreenCanvas === 'function'
        ? new OffscreenCanvas(width, height)
        : Object.assign(document.createElement('canvas'), { width, height });

    if (session.canvas.width !== width) {
        session.canvas.width = width;
    }

    if (session.canvas.height !== height) {
        session.canvas.height = height;
    }

    // willReadFrequently keeps the backing store in CPU memory, which is what makes
    // getImageData cheap instead of forcing a GPU readback on every frame.
    session.ctx = session.canvas.getContext('2d', { willReadFrequently: true, alpha: false });
    session.canvasWidth = width;
    session.canvasHeight = height;
}

function scheduleFrameCallback(session) {
    const video = session.video;
    if (!video) {
        return;
    }

    if (typeof video.requestVideoFrameCallback !== 'function') {
        // Without the callback there is no way to tell new frames from repeats, so every grab
        // is treated as new. The managed side still paces itself with its own timer.
        session.hasNewFrame = true;
        return;
    }

    const step = () => {
        session.hasNewFrame = true;
        session.frameCallbackHandle = video.requestVideoFrameCallback(step);
    };

    session.frameCallbackHandle = video.requestVideoFrameCallback(step);
}

function describeTrack(track, video) {
    const settings = typeof track.getSettings === 'function' ? track.getSettings() : {};
    const capabilities = typeof track.getCapabilities === 'function' ? track.getCapabilities() : {};

    return {
        deviceId: settings.deviceId ?? '',
        label: track.label ?? '',
        width: settings.width ?? video.videoWidth ?? 0,
        height: settings.height ?? video.videoHeight ?? 0,
        frameRate: settings.frameRate ?? 0,
        facingMode: settings.facingMode ?? '',
        supportsTorch: Object.prototype.hasOwnProperty.call(capabilities, 'torch'),
        torchOn: settings.torch === true,
        supportsZoom: Object.prototype.hasOwnProperty.call(capabilities, 'zoom'),
        zoomMin: capabilities.zoom ? capabilities.zoom.min : 0,
        zoomMax: capabilities.zoom ? capabilities.zoom.max : 0,
        zoomStep: capabilities.zoom && capabilities.zoom.step ? capabilities.zoom.step : 0,
        zoom: settings.zoom ?? 0,
    };
}

/**
 * Returns the available video inputs as JSON. Labels are only populated once the user has
 * granted camera permission at least once, which is a browser privacy rule, not a bug.
 */
export async function listCameras() {
    if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) {
        return '[]';
    }

    const devices = await navigator.mediaDevices.enumerateDevices();
    const cameras = devices
        .filter(d => d.kind === 'videoinput')
        .map((d, index) => ({
            deviceId: d.deviceId,
            label: d.label || `Camera ${index + 1}`,
        }));

    return JSON.stringify(cameras);
}

/**
 * Opens a camera and binds it to the <video> element with the given id.
 * Returns a JSON description of the negotiated track.
 */
export async function start(sessionId, videoElementId, deviceId, requestedWidth, requestedHeight, facingMode) {
    const session = getSession(sessionId);
    releaseStream(session);

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        throw new Error('NotSupportedError: this browser exposes no camera API.');
    }

    const video = document.getElementById(videoElementId);
    if (!video) {
        throw new Error('NotFoundError: the scanner video element is not in the document.');
    }

    const video_constraints = {};
    if (deviceId) {
        video_constraints.deviceId = { exact: deviceId };
    } else if (facingMode) {
        video_constraints.facingMode = { ideal: facingMode };
    }

    if (requestedWidth > 0) {
        video_constraints.width = { ideal: requestedWidth };
    }

    if (requestedHeight > 0) {
        video_constraints.height = { ideal: requestedHeight };
    }

    const stream = await navigator.mediaDevices.getUserMedia({ audio: false, video: video_constraints });

    session.stream = stream;
    session.video = video;
    video.setAttribute('playsinline', '');
    video.setAttribute('muted', '');
    video.muted = true;
    video.srcObject = stream;

    await video.play();

    // Safari reports a zero sized video until metadata has actually arrived.
    if (!video.videoWidth) {
        await new Promise(resolve => {
            const done = () => {
                video.removeEventListener('loadedmetadata', done);
                resolve();
            };

            video.addEventListener('loadedmetadata', done);
            setTimeout(done, 2000);
        });
    }

    session.track = stream.getVideoTracks()[0];
    scheduleFrameCallback(session);

    return JSON.stringify(describeTrack(session.track, video));
}

/** Sets the resolution frames are sampled at. Lower is faster and is chosen by the managed side. */
export function configure(sessionId, width, height) {
    const session = getSession(sessionId);
    ensureCanvas(session, width, height);
}

/**
 * Copies the most recent camera frame into a .NET buffer as RGBA.
 * Returns the number of bytes written, 0 when there is no new frame, or -1 when the buffer is
 * too small.
 */
export function grabFrame(sessionId, buffer) {
    const session = state.sessions.get(sessionId);
    if (!session || !session.video || !session.ctx || !session.hasNewFrame) {
        return 0;
    }

    const width = session.canvasWidth;
    const height = session.canvasHeight;
    if (!width || !height || !session.video.videoWidth) {
        return 0;
    }

    session.ctx.drawImage(session.video, 0, 0, width, height);
    const data = session.ctx.getImageData(0, 0, width, height).data;
    if (buffer.length < data.byteLength) {
        return -1;
    }

    buffer.set(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
    session.hasNewFrame = false;
    return data.byteLength;
}

/** Turns the torch on or off. Returns whether the request was accepted. */
export async function setTorch(sessionId, on) {
    const session = state.sessions.get(sessionId);
    if (!session || !session.track || typeof session.track.applyConstraints !== 'function') {
        return false;
    }

    try {
        await session.track.applyConstraints({ advanced: [{ torch: !!on }] });
        return true;
    } catch {
        return false;
    }
}

/** Sets the optical or digital zoom. Returns whether the request was accepted. */
export async function setZoom(sessionId, value) {
    const session = state.sessions.get(sessionId);
    if (!session || !session.track || typeof session.track.applyConstraints !== 'function') {
        return false;
    }

    try {
        await session.track.applyConstraints({ advanced: [{ zoom: value }] });
        return true;
    } catch {
        return false;
    }
}

/** Returns the current track description as JSON, or an empty string when nothing is running. */
export function describe(sessionId) {
    const session = state.sessions.get(sessionId);
    if (!session || !session.track || !session.video) {
        return '';
    }

    return JSON.stringify(describeTrack(session.track, session.video));
}

/** Stops the camera and releases the stream. The session and its canvas survive for a restart. */
export function stop(sessionId) {
    const session = state.sessions.get(sessionId);
    if (session) {
        releaseStream(session);
    }
}

/** Stops the camera and forgets the session entirely. */
export function dispose(sessionId) {
    const session = state.sessions.get(sessionId);
    if (!session) {
        return;
    }

    releaseStream(session);
    session.prepared = null;
    session.canvas = null;
    session.ctx = null;
    state.sessions.delete(sessionId);
}

/**
 * Decodes the first file selected in the given <input type="file"> into a pixel buffer held on
 * the JS side, scaled so that neither edge exceeds maxDimension.
 * Returns the dimensions packed as (width << 16) | height, or 0 when there is nothing to read.
 */
export async function prepareImageFromInput(sessionId, inputElementId, maxDimension) {
    const input = document.getElementById(inputElementId);
    if (!input || !input.files || input.files.length === 0) {
        return 0;
    }

    return await prepareImageFromBlob(sessionId, input.files[0], maxDimension);
}

/** Decodes an image blob the same way as prepareImageFromInput. */
export async function prepareImageFromBlob(sessionId, blob, maxDimension) {
    const session = getSession(sessionId);
    const bitmap = await createImageBitmap(blob);

    try {
        let width = bitmap.width;
        let height = bitmap.height;
        const longest = Math.max(width, height);
        if (maxDimension > 0 && longest > maxDimension) {
            const scale = maxDimension / longest;
            width = Math.max(1, Math.round(width * scale));
            height = Math.max(1, Math.round(height * scale));
        }

        const canvas = typeof OffscreenCanvas === 'function'
            ? new OffscreenCanvas(width, height)
            : Object.assign(document.createElement('canvas'), { width, height });

        const ctx = canvas.getContext('2d', { willReadFrequently: true, alpha: false });
        ctx.drawImage(bitmap, 0, 0, width, height);
        session.prepared = ctx.getImageData(0, 0, width, height).data;

        return (width << 16) | height;
    } finally {
        bitmap.close();
    }
}

/** Copies the image prepared by prepareImage* into a .NET buffer. Returns bytes written. */
export function readPrepared(sessionId, buffer) {
    const session = state.sessions.get(sessionId);
    if (!session || !session.prepared) {
        return 0;
    }

    const data = session.prepared;
    if (buffer.length < data.byteLength) {
        return -1;
    }

    buffer.set(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
    session.prepared = null;
    return data.byteLength;
}

/** Reports whether the page is in a context where the camera API is reachable at all. */
export function isCameraSupported() {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
}

/** Reports whether the page is served from a secure context, which the camera API requires. */
export function isSecureContext() {
    return !!window.isSecureContext;
}

/** Clicks an element by id, used to open the hidden file picker from managed code. */
export function click(elementId) {
    const element = document.getElementById(elementId);
    if (element) {
        element.click();
    }
}
