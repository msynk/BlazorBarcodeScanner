using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using BlazorBarcodeScanner.Imaging;
using BlazorBarcodeScanner.Interop;
using BlazorBarcodeScanner.Pipeline;
using Microsoft.AspNetCore.Components;

namespace BlazorBarcodeScanner;

/// <summary>
/// A camera barcode scanner.
/// </summary>
/// <remarks>
/// <para>
/// The component owns the camera, the frame loop and one <see cref="BarcodeDecoder"/>. Frames
/// never enter the Blazor render tree: the video element paints itself, the decoder reads pixels
/// out of a buffer JavaScript writes into directly, and the component only re-renders when
/// something a user can see actually changes. A scanner running at twenty frames a second
/// typically renders a handful of times a minute.
/// </para>
/// <para>
/// Continuous camera scanning requires Blazor WebAssembly, because it depends on the browser
/// interop that lets JavaScript write into managed memory. On other render modes the component
/// reports <see cref="ScannerErrorKind.NotSupported"/> rather than silently degrading.
/// </para>
/// </remarks>
public sealed partial class BarcodeScanner : ComponentBase, IAsyncDisposable
{
    private readonly string _sessionId = $"bs-{Guid.NewGuid():N}";
    private readonly string _videoElementId = $"bscan-video-{Guid.NewGuid():N}";
    private readonly string _fileInputId = $"bscan-file-{Guid.NewGuid():N}";

    private BarcodeDecoder? _decoder;
    private CancellationTokenSource? _loopCancellation;
    private PeriodicTimer? _timer;
    private byte[] _frameBuffer = [];
    private List<CameraDevice> _cameras = [];
    private CameraInfo? _camera;
    private ScanRegion _region = ScanRegion.Full;

    private ScannerStatus _status = ScannerStatus.Idle;
    private string? _message;
    private string? _announcement;
    private string? _highlight;
    private string? _activeDeviceId;
    private bool _mirrored;
    private bool _torchOn;
    private double _zoom;
    private int _processingWidth;
    private int _processingHeight;
    private bool _renderPending = true;
    private long _lastDiagnosticsAt;
    private bool _disposed;

    /// <summary>The symbologies to look for.</summary>
    [Parameter]
    public BarcodeFormat Formats { get; set; } = BarcodeFormat.All;

    /// <summary>
    /// The part of the frame to search, in normalized coordinates. The default searches the whole
    /// frame; a centred square is the usual choice for an aiming user interface.
    /// </summary>
    [Parameter]
    public ScanRegion Region { get; set; } = ScanRegion.Full;

    /// <summary>
    /// Advanced tuning. When supplied, <see cref="Formats"/> and <see cref="Region"/> are still
    /// applied on top of it, so the common cases do not need this at all.
    /// </summary>
    [Parameter]
    public ScannerOptions? Options { get; set; }

    /// <summary>How many frames a second to decode. Higher costs battery for little benefit above about twenty.</summary>
    [Parameter]
    public int TargetFrameRate { get; set; } = 15;

    /// <summary>Camera width to ask the browser for. The browser is free to negotiate something else.</summary>
    [Parameter]
    public int RequestedWidth { get; set; } = 1280;

    /// <summary>Camera height to ask the browser for.</summary>
    [Parameter]
    public int RequestedHeight { get; set; } = 720;

    /// <summary>
    /// Longest edge, in pixels, of the image the decoder actually sees. Frames are scaled down to
    /// this by the browser before they cross into managed code, which is the single most
    /// effective performance control the component exposes.
    /// </summary>
    [Parameter]
    public int MaxProcessingSize { get; set; } = 640;

    /// <summary>Which camera to prefer when <see cref="DeviceId"/> is not set.</summary>
    [Parameter]
    public CameraFacing Facing { get; set; } = CameraFacing.Rear;

    /// <summary>A specific camera to open, from <see cref="GetCamerasAsync"/>.</summary>
    [Parameter]
    public string? DeviceId { get; set; }

    /// <summary>Start the camera as soon as the component is ready.</summary>
    [Parameter]
    public bool AutoStart { get; set; } = true;

    /// <summary>How long the same value is suppressed after being reported.</summary>
    [Parameter]
    public TimeSpan DuplicateSuppressionWindow { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Show the built-in control bar.</summary>
    [Parameter]
    public bool ShowControls { get; set; } = true;

    /// <summary>Show the built-in status bar.</summary>
    [Parameter]
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>Draw the corner reticle. Ignored when <see cref="Overlay"/> is supplied.</summary>
    [Parameter]
    public bool ShowReticle { get; set; } = true;

    /// <summary>Dim the area outside <see cref="Region"/>.</summary>
    [Parameter]
    public bool ShowMask { get; set; } = true;

    /// <summary>Outline a decoded symbol where it was found in the frame.</summary>
    [Parameter]
    public bool HighlightResults { get; set; } = true;

    /// <summary>Offer the built-in still image scanning control.</summary>
    [Parameter]
    public bool EnableFileScanning { get; set; } = true;

    /// <summary>Stretch to the height of the parent instead of keeping a 4:3 box.</summary>
    [Parameter]
    public bool Fill { get; set; }

    /// <summary>Accessible name for the scanner region.</summary>
    [Parameter]
    public string AriaLabel { get; set; } = "Barcode scanner";

    /// <summary>Extra CSS classes for the root element.</summary>
    [Parameter]
    public string? CssClass { get; set; }

    /// <summary>Replaces the built-in reticle entirely.</summary>
    [Parameter]
    public RenderFragment<BarcodeScanner>? Overlay { get; set; }

    /// <summary>Replaces the rendering of status and error messages.</summary>
    [Parameter]
    public RenderFragment<string>? MessageTemplate { get; set; }

    /// <summary>Extra controls appended to the built-in control bar.</summary>
    [Parameter]
    public RenderFragment? ControlsContent { get; set; }

    /// <summary>Arbitrary content rendered on top of the scanner.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Raised for every accepted scan, after duplicate suppression.</summary>
    [Parameter]
    public EventCallback<BarcodeResult> OnScan { get; set; }

    /// <summary>Raised when the scanner fails to start or the camera is lost.</summary>
    [Parameter]
    public EventCallback<ScannerException> OnError { get; set; }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    [Parameter]
    public EventCallback<ScannerStatus> OnStatusChanged { get; set; }

    /// <summary>Raised when a camera is opened, with what the browser negotiated.</summary>
    [Parameter]
    public EventCallback<CameraInfo> OnCameraChanged { get; set; }

    /// <summary>
    /// Raised at most twice a second with the timings of the most recent frame. Subscribing has
    /// no cost when nothing is listening.
    /// </summary>
    [Parameter]
    public EventCallback<ScanDiagnostics> OnDiagnostics { get; set; }

    /// <summary>Any other attributes are applied to the root element.</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Current lifecycle state.</summary>
    public ScannerStatus Status => _status;

    /// <summary>What the browser negotiated for the open camera, or <see langword="null"/>.</summary>
    public CameraInfo? Camera => _camera;

    /// <summary>Cameras discovered by the last call to <see cref="GetCamerasAsync"/>.</summary>
    public IReadOnlyList<CameraDevice> Cameras => _cameras;

    /// <summary>Timings from the most recently decoded frame.</summary>
    public ScanDiagnostics Diagnostics => _decoder?.LastFrame ?? default;

    /// <summary>Whether the torch is currently on.</summary>
    public bool IsTorchOn => _torchOn;

    /// <summary>Current zoom value, or zero when zoom is not supported.</summary>
    public double Zoom => _zoom;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        _region = Region.Normalized();

        if (_decoder is not null)
        {
            if (_decoder.Options.Formats != Formats)
            {
                // The decoder builds its symbology list up front, so a format change needs a new one.
                var previous = _decoder;
                _decoder = CreateDecoder();
                previous.Dispose();
            }
            else
            {
                // Everything else is live-editable, so a running scanner picks up parameter
                // changes without dropping the camera.
                _decoder.Options.Region = _region;
                _decoder.Options.TryHarder = Options?.TryHarder ?? _decoder.Options.TryHarder;
                _decoder.DuplicateSuppressionWindow = Options?.DuplicateSuppressionWindow ?? DuplicateSuppressionWindow;
            }
        }

        if (_timer is not null)
        {
            _timer.Period = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(TargetFrameRate, 1, 60));
        }

        if (_camera is not null && _status is ScannerStatus.Scanning or ScannerStatus.Paused)
        {
            var expectedLongest = Math.Max(_processingWidth, _processingHeight);
            var wanted = Math.Min(Math.Max(120, MaxProcessingSize), Math.Max(_camera.Width, _camera.Height));
            if (Math.Abs(expectedLongest - wanted) > 1)
            {
                ConfigureProcessingSize(_camera.Width, _camera.Height);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        if (!OperatingSystem.IsBrowser())
        {
            // Prerendering on the server is expected and harmless; the component activates once
            // the WebAssembly runtime takes over.
            return;
        }

        if (!await EnsureModuleAsync().ConfigureAwait(true))
        {
            return;
        }

        if (!ScannerJsInterop.IsCameraSupported())
        {
            Fail(new ScannerException(
                ScannerErrorKind.NotSupported,
                ScannerJsInterop.IsSecureContext()
                    ? "This browser does not expose a camera API to the page."
                    : "The camera API needs a secure context; serve the application over HTTPS or from localhost."));
            return;
        }

        await RefreshCamerasAsync().ConfigureAwait(true);

        if (AutoStart)
        {
            await StartAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Enumerates the available cameras.</summary>
    /// <remarks>
    /// Browsers withhold camera labels until permission has been granted once, so calling this
    /// before <see cref="StartAsync"/> tends to return generic names.
    /// </remarks>
    public async Task<IReadOnlyList<CameraDevice>> GetCamerasAsync()
    {
        if (await EnsureModuleAsync().ConfigureAwait(true))
        {
            await RefreshCamerasAsync().ConfigureAwait(true);
        }

        return _cameras;
    }

    /// <summary>Opens the camera and starts decoding.</summary>
    /// <param name="deviceId">A specific camera, or <see langword="null"/> to use <see cref="DeviceId"/> and <see cref="Facing"/>.</param>
    public async Task StartAsync(string? deviceId = null)
    {
        if (!OperatingSystem.IsBrowser() || _disposed)
        {
            return;
        }

        if (!await EnsureModuleAsync().ConfigureAwait(true))
        {
            return;
        }

        if (_status is ScannerStatus.Starting or ScannerStatus.Scanning)
        {
            await StopAsync().ConfigureAwait(true);
        }

        SetStatus(ScannerStatus.Starting, "Requesting camera access...");

        var facing = Facing switch
        {
            CameraFacing.Rear => "environment",
            CameraFacing.Front => "user",
            _ => null,
        };

        string json;
        try
        {
            json = await ScannerJsInterop.StartAsync(
                _sessionId,
                _videoElementId,
                deviceId ?? DeviceId,
                RequestedWidth,
                RequestedHeight,
                facing).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail(ScannerException.FromBrowserError(ex));
            return;
        }

        _camera = JsonSerializer.Deserialize(json, ScannerJsonContext.Default.CameraInfo);
        if (_camera is null || _camera.Width <= 0 || _camera.Height <= 0)
        {
            Fail(new ScannerException(ScannerErrorKind.Unknown, "The camera opened but reported no video size."));
            return;
        }

        _activeDeviceId = _camera.DeviceId;
        _mirrored = _camera.FacingMode == "user";
        _torchOn = _camera.TorchOn;
        _zoom = _camera.Zoom;

        ConfigureProcessingSize(_camera.Width, _camera.Height);

        _decoder ??= CreateDecoder();
        _decoder.ResetDuplicateFilter();

        // Labels only become readable once permission has been granted, so refresh them now.
        await RefreshCamerasAsync().ConfigureAwait(true);

        SetStatus(ScannerStatus.Scanning, null);
        await OnCameraChanged.InvokeAsync(_camera).ConfigureAwait(true);

        _loopCancellation = new CancellationTokenSource();
        _ = RunFrameLoopAsync(_loopCancellation.Token);
    }

    /// <summary>Stops decoding and releases the camera.</summary>
    public async Task StopAsync()
    {
        if (_loopCancellation is not null)
        {
            await _loopCancellation.CancelAsync().ConfigureAwait(true);
            _loopCancellation.Dispose();
            _loopCancellation = null;
        }

        if (OperatingSystem.IsBrowser() && ScannerJsInterop.IsImported)
        {
            ScannerJsInterop.Stop(_sessionId);
        }

        _highlight = null;
        _camera = null;
        SetStatus(ScannerStatus.Stopped, null);
    }

    /// <summary>Stops decoding but keeps the camera open, so resuming is instant.</summary>
    public void Pause()
    {
        if (_status == ScannerStatus.Scanning)
        {
            SetStatus(ScannerStatus.Paused, null);
        }
    }

    /// <summary>Resumes decoding after <see cref="Pause"/>.</summary>
    public void Resume()
    {
        if (_status == ScannerStatus.Paused)
        {
            _decoder?.ResetDuplicateFilter();
            SetStatus(ScannerStatus.Scanning, null);
        }
    }

    /// <summary>Switches to another camera without tearing the component down.</summary>
    /// <param name="deviceId">The camera to open.</param>
    public Task SwitchCameraAsync(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        return StartAsync(deviceId);
    }

    /// <summary>Turns the torch on or off where the camera supports it.</summary>
    /// <param name="on">Whether the torch should be lit.</param>
    /// <returns><see langword="true"/> when the browser accepted the request.</returns>
    public async Task<bool> SetTorchAsync(bool on)
    {
        if (!OperatingSystem.IsBrowser() || _camera?.SupportsTorch != true)
        {
            return false;
        }

        var accepted = await ScannerJsInterop.SetTorchAsync(_sessionId, on).ConfigureAwait(true);
        if (accepted)
        {
            _torchOn = on;
            RequestRender();
        }

        return accepted;
    }

    /// <summary>Sets the zoom where the camera supports it.</summary>
    /// <param name="zoom">A value between <see cref="CameraInfo.ZoomMin"/> and <see cref="CameraInfo.ZoomMax"/>.</param>
    /// <returns><see langword="true"/> when the browser accepted the request.</returns>
    public async Task<bool> SetZoomAsync(double zoom)
    {
        if (!OperatingSystem.IsBrowser() || _camera?.SupportsZoom != true)
        {
            return false;
        }

        zoom = Math.Clamp(zoom, _camera.ZoomMin, _camera.ZoomMax);
        var accepted = await ScannerJsInterop.SetZoomAsync(_sessionId, zoom).ConfigureAwait(true);
        if (accepted)
        {
            _zoom = zoom;
            RequestRender();
        }

        return accepted;
    }

    /// <summary>Opens the file picker so the user can scan a still image.</summary>
    public async Task OpenFilePickerAsync()
    {
        if (!OperatingSystem.IsBrowser() || !EnableFileScanning)
        {
            return;
        }

        if (await EnsureModuleAsync().ConfigureAwait(true))
        {
            ScannerJsInterop.Click(_fileInputId);
        }
    }

    /// <summary>
    /// Decodes an image already loaded into a pixel buffer, for applications that source images
    /// themselves rather than through the built-in picker.
    /// </summary>
    /// <param name="pixels">Packed pixels.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="format">Pixel layout.</param>
    public BarcodeResult? DecodeImage(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format)
    {
        using var decoder = new BarcodeDecoder(BuildOptions(ScannerOptions.ForStillImages()));
        return decoder.Decode(pixels, width, height, format, suppressDuplicates: false);
    }

    private async Task OnFileSelectedAsync(ChangeEventArgs args)
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        SetMessage("Reading image...");

        int packed;
        try
        {
            // Large photographs are scaled down in the browser: a 12 megapixel image carries no
            // more barcode information than a 2 megapixel one and costs six times as much to scan.
            packed = await ScannerJsInterop.PrepareImageFromInputAsync(_sessionId, _fileInputId, 1600)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Fail(new ScannerException(ScannerErrorKind.Unknown, "The selected file could not be read as an image.", ex));
            return;
        }

        if (packed <= 0)
        {
            SetMessage(null);
            return;
        }

        var width = packed >> 16;
        var height = packed & 0xFFFF;
        var required = width * height * 4;
        var buffer = new byte[required];

        var written = ScannerJsInterop.ReadPrepared(_sessionId, buffer);
        if (written <= 0)
        {
            SetMessage("The image could not be read.");
            return;
        }

        using var stillDecoder = new BarcodeDecoder(BuildOptions(ScannerOptions.ForStillImages()));
        var result = stillDecoder.Decode(buffer.AsSpan(0, written), width, height, PixelFormat.Rgba32, suppressDuplicates: false);

        SetMessage(result is null ? "No barcode found in that image." : null);

        if (result is not null)
        {
            _announcement = $"{result.Format}: {result.Text}";
            RequestRender();
            await OnScan.InvokeAsync(result).ConfigureAwait(true);
        }
    }

    private async Task RunFrameLoopAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(TargetFrameRate, 1, 60));
        using var timer = new PeriodicTimer(interval);
        _timer = timer;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true))
            {
                if (_status != ScannerStatus.Scanning || _decoder is null)
                {
                    continue;
                }

                // The browser reports zero when the video has not produced a new frame since the
                // last grab, so an idle camera costs one interop call and nothing else. This is
                // also the backpressure mechanism: a slow decode simply misses ticks and the
                // next grab returns the newest frame rather than a queued stale one.
                var written = ScannerJsInterop.GrabFrame(_sessionId, _frameBuffer);
                if (written <= 0)
                {
                    continue;
                }

                BarcodeResult? result;
                try
                {
                    result = _decoder.Decode(
                        _frameBuffer.AsSpan(0, written),
                        _processingWidth,
                        _processingHeight,
                        PixelFormat.Rgba32);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Fail(new ScannerException(ScannerErrorKind.Unknown, "The decoder failed on a frame.", ex));
                    return;
                }

                await ReportDiagnosticsAsync().ConfigureAwait(true);

                if (result is null)
                {
                    continue;
                }

                UpdateHighlight(result);
                _announcement = $"{result.Format}: {result.Text}";
                RequestRender();
                StateHasChanged();

                await OnScan.InvokeAsync(result).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on stop and on disposal.
        }
        finally
        {
            _timer = null;
        }
    }

    private async Task ReportDiagnosticsAsync()
    {
        if (!OnDiagnostics.HasDelegate || _decoder is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastDiagnosticsAt < 500)
        {
            return;
        }

        _lastDiagnosticsAt = now;
        await OnDiagnostics.InvokeAsync(_decoder.LastFrame).ConfigureAwait(true);
    }

    private void ConfigureProcessingSize(int cameraWidth, int cameraHeight)
    {
        var longest = Math.Max(cameraWidth, cameraHeight);
        var limit = Math.Max(120, MaxProcessingSize);
        var scale = longest > limit ? (double)limit / longest : 1.0;

        _processingWidth = Math.Max(2, (int)Math.Round(cameraWidth * scale) & ~1);
        _processingHeight = Math.Max(2, (int)Math.Round(cameraHeight * scale) & ~1);

        var required = _processingWidth * _processingHeight * 4;
        if (_frameBuffer.Length < required)
        {
            // The buffer lives for the whole session; frames are copied into it in place.
            _frameBuffer = new byte[required];
        }

        if (OperatingSystem.IsBrowser() && ScannerJsInterop.IsImported)
        {
            ScannerJsInterop.Configure(_sessionId, _processingWidth, _processingHeight);
        }
    }

    /// <summary>
    /// Loads the JavaScript module, reporting a load failure through the normal error path.
    /// </summary>
    /// <remarks>
    /// Every entry point that reaches JavaScript goes through here or through
    /// <see cref="ScannerJsInterop.IsImported"/> first. Calling a binding on a module that has not
    /// been imported aborts the WebAssembly runtime outright - it is an assertion, not an
    /// exception - which leaves the whole application dead rather than just the scanner.
    /// </remarks>
    private async Task<bool> EnsureModuleAsync()
    {
        if (!OperatingSystem.IsBrowser())
        {
            return false;
        }

        if (ScannerJsInterop.IsImported)
        {
            return true;
        }

        try
        {
            await ScannerJsInterop.EnsureImportedAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            Fail(new ScannerException(
                ScannerErrorKind.NotSupported,
                "The BlazorBarcodeScanner JavaScript module could not be loaded. Check that static web assets are served.",
                ex));
            return false;
        }
    }

    private BarcodeDecoder CreateDecoder() => new(BuildOptions(Options?.Clone() ?? new ScannerOptions()));

    private ScannerOptions BuildOptions(ScannerOptions baseOptions)
    {
        baseOptions.Formats = Formats;
        baseOptions.Region = _region;
        if (Options is null)
        {
            baseOptions.DuplicateSuppressionWindow = DuplicateSuppressionWindow;
        }

        return baseOptions;
    }

    private async Task RefreshCamerasAsync()
    {
        if (!OperatingSystem.IsBrowser() || !ScannerJsInterop.IsImported)
        {
            return;
        }

        try
        {
            var json = await ScannerJsInterop.ListCamerasAsync().ConfigureAwait(true);
            var devices = JsonSerializer.Deserialize(json, ScannerJsonContext.Default.CameraDeviceArray);
            _cameras = devices is null ? [] : [.. devices];
            RequestRender();
        }
        catch
        {
            // Enumeration is best effort: a browser that refuses it still supports scanning with
            // the default camera.
            _cameras = [];
        }
    }

    private async Task OnCameraSelectedAsync(ChangeEventArgs args)
    {
        var deviceId = args.Value?.ToString();
        if (!string.IsNullOrEmpty(deviceId) && deviceId != _activeDeviceId)
        {
            await SwitchCameraAsync(deviceId).ConfigureAwait(true);
        }
    }

    private async Task OnZoomChangedAsync(ChangeEventArgs args)
    {
        if (double.TryParse(args.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom))
        {
            await SetZoomAsync(zoom).ConfigureAwait(true);
        }
    }

    private Task ToggleTorchAsync() => SetTorchAsync(!_torchOn);

    private Task TogglePauseAsync()
    {
        if (_status == ScannerStatus.Paused)
        {
            Resume();
        }
        else
        {
            Pause();
        }

        return Task.CompletedTask;
    }

    private void UpdateHighlight(BarcodeResult result)
    {
        if (!HighlightResults || _processingWidth == 0 || _processingHeight == 0)
        {
            return;
        }

        var box = result.BoundingBox;
        var left = box.X / _processingWidth * 100;
        var top = box.Y / _processingHeight * 100;
        var width = Math.Max(box.Width, 4) / _processingWidth * 100;
        var height = Math.Max(box.Height, 4) / _processingHeight * 100;

        _highlight = string.Create(
            CultureInfo.InvariantCulture,
            $"--bscan-hit-left:{left:0.##}%;--bscan-hit-top:{top:0.##}%;--bscan-hit-width:{width:0.##}%;--bscan-hit-height:{height:0.##}%");
    }

    private string MaskClipPath()
    {
        var left = _region.X * 100;
        var top = _region.Y * 100;
        var right = (_region.X + _region.Width) * 100;
        var bottom = (_region.Y + _region.Height) * 100;

        // An even-odd polygon: the outer ring is the frame, the inner ring is the hole.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"polygon(evenodd, 0% 0%, 100% 0%, 100% 100%, 0% 100%, 0% 0%, {left:0.##}% {top:0.##}%, {right:0.##}% {top:0.##}%, {right:0.##}% {bottom:0.##}%, {left:0.##}% {bottom:0.##}%, {left:0.##}% {top:0.##}%)");
    }

    private string ReticleStyle() => string.Create(
        CultureInfo.InvariantCulture,
        $"inset-inline-start:{_region.X * 100:0.##}%;inset-block-start:{_region.Y * 100:0.##}%;inline-size:{_region.Width * 100:0.##}%;block-size:{_region.Height * 100:0.##}%");

    private string StatusText() => _status switch
    {
        ScannerStatus.Idle => "Ready",
        ScannerStatus.Starting => "Starting camera",
        ScannerStatus.Scanning => _camera is null ? "Scanning" : $"Scanning · {_camera.Width}×{_camera.Height}",
        ScannerStatus.Paused => "Paused",
        ScannerStatus.Stopped => "Stopped",
        _ => "Error",
    };

    private void SetStatus(ScannerStatus status, string? message)
    {
        if (_status == status && _message == message)
        {
            return;
        }

        _status = status;
        _message = message;
        RequestRender();
        StateHasChanged();
        _ = OnStatusChanged.InvokeAsync(status);
    }

    private void SetMessage(string? message)
    {
        if (_message == message)
        {
            return;
        }

        _message = message;
        RequestRender();
        StateHasChanged();
    }

    private void Fail(ScannerException exception)
    {
        _status = ScannerStatus.Faulted;
        _message = exception.Message;
        RequestRender();
        StateHasChanged();
        _ = OnStatusChanged.InvokeAsync(ScannerStatus.Faulted);
        _ = OnError.InvokeAsync(exception);
    }

    private void RequestRender() => _renderPending = true;

    /// <inheritdoc />
    protected override bool ShouldRender()
    {
        // Frames arrive many times a second; only state a user can see is allowed to re-render.
        if (!_renderPending)
        {
            return false;
        }

        _renderPending = false;
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_loopCancellation is not null)
        {
            await _loopCancellation.CancelAsync().ConfigureAwait(false);
            _loopCancellation.Dispose();
            _loopCancellation = null;
        }

        if (OperatingSystem.IsBrowser() && ScannerJsInterop.IsImported)
        {
            try
            {
                ScannerJsInterop.DisposeSession(_sessionId);
            }
            catch (Exception ex) when (ex is JSException or InvalidOperationException)
            {
                // The page may already be tearing down; there is nothing left to release.
            }
        }

        _decoder?.Dispose();
        _decoder = null;
        _frameBuffer = [];
    }
}
