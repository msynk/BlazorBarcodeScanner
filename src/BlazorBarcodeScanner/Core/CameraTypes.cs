using System.Text.Json.Serialization;

namespace BlazorBarcodeScanner;

/// <summary>A camera the browser is willing to open.</summary>
/// <param name="DeviceId">Opaque identifier to pass to <see cref="BarcodeScanner.StartAsync"/>.</param>
/// <param name="Label">
/// Human readable name. Browsers hide it until the user has granted camera permission at least
/// once, so it may be a generic placeholder on the first run.
/// </param>
public sealed record CameraDevice(string DeviceId, string Label);

/// <summary>Which camera to prefer when no specific device has been chosen.</summary>
public enum CameraFacing
{
    /// <summary>Let the browser decide.</summary>
    Any = 0,

    /// <summary>Prefer a rear facing camera, which is what a scanner normally wants.</summary>
    Rear = 1,

    /// <summary>Prefer a front facing camera.</summary>
    Front = 2,
}

/// <summary>
/// What the browser actually negotiated, and what the running track can do.
/// </summary>
public sealed class CameraInfo
{
    /// <summary>Identifier of the open camera.</summary>
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Human readable name of the open camera.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>Negotiated frame width in pixels.</summary>
    [JsonPropertyName("width")]
    public int Width { get; set; }

    /// <summary>Negotiated frame height in pixels.</summary>
    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>Negotiated frame rate, when the browser reports one.</summary>
    [JsonPropertyName("frameRate")]
    public double FrameRate { get; set; }

    /// <summary>Reported facing mode, typically <c>environment</c> or <c>user</c>.</summary>
    [JsonPropertyName("facingMode")]
    public string FacingMode { get; set; } = string.Empty;

    /// <summary><see langword="true"/> when the track exposes a torch capability.</summary>
    [JsonPropertyName("supportsTorch")]
    public bool SupportsTorch { get; set; }

    /// <summary>Whether the torch is currently reported as on.</summary>
    [JsonPropertyName("torchOn")]
    public bool TorchOn { get; set; }

    /// <summary><see langword="true"/> when the track exposes a zoom capability.</summary>
    [JsonPropertyName("supportsZoom")]
    public bool SupportsZoom { get; set; }

    /// <summary>Smallest accepted zoom value.</summary>
    [JsonPropertyName("zoomMin")]
    public double ZoomMin { get; set; }

    /// <summary>Largest accepted zoom value.</summary>
    [JsonPropertyName("zoomMax")]
    public double ZoomMax { get; set; }

    /// <summary>Granularity of the zoom control, when reported.</summary>
    [JsonPropertyName("zoomStep")]
    public double ZoomStep { get; set; }

    /// <summary>Current zoom value.</summary>
    [JsonPropertyName("zoom")]
    public double Zoom { get; set; }
}

/// <summary>Lifecycle state of a scanner.</summary>
public enum ScannerStatus
{
    /// <summary>Created but not started.</summary>
    Idle = 0,

    /// <summary>Waiting for camera permission and for the track to open.</summary>
    Starting = 1,

    /// <summary>Running and decoding frames.</summary>
    Scanning = 2,

    /// <summary>Camera still open, but frames are not being decoded.</summary>
    Paused = 3,

    /// <summary>Stopped and the camera released.</summary>
    Stopped = 4,

    /// <summary>Stopped because of an error; see the reported exception.</summary>
    Faulted = 5,
}

/// <summary>Why a scanner could not start.</summary>
public enum ScannerErrorKind
{
    /// <summary>The cause could not be classified.</summary>
    Unknown = 0,

    /// <summary>The user denied camera permission, or a policy denies it.</summary>
    PermissionDenied = 1,

    /// <summary>No camera matched the request.</summary>
    NoCamera = 2,

    /// <summary>A camera exists but could not be opened, usually because another application holds it.</summary>
    CameraUnavailable = 3,

    /// <summary>The requested constraints cannot be satisfied by any camera.</summary>
    ConstraintsUnsatisfied = 4,

    /// <summary>The browser exposes no camera API, or the page is not in a secure context.</summary>
    NotSupported = 5,
}

/// <summary>An error raised by the scanner.</summary>
public sealed class ScannerException : Exception
{
    /// <summary>Creates an exception.</summary>
    /// <param name="kind">Classification of the failure.</param>
    /// <param name="message">Human readable description.</param>
    /// <param name="innerException">The underlying error, when there is one.</param>
    public ScannerException(ScannerErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>Classification of the failure, so an application can react without parsing text.</summary>
    public ScannerErrorKind Kind { get; }

    /// <summary>
    /// Classifies a browser <c>getUserMedia</c> rejection, whose only reliable signal is the
    /// DOMException name embedded in the message.
    /// </summary>
    /// <param name="exception">The exception thrown by the interop call.</param>
    public static ScannerException FromBrowserError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = exception.Message;

        var kind = message switch
        {
            var m when m.Contains("NotAllowedError", StringComparison.Ordinal) ||
                       m.Contains("SecurityError", StringComparison.Ordinal) => ScannerErrorKind.PermissionDenied,
            var m when m.Contains("NotFoundError", StringComparison.Ordinal) ||
                       m.Contains("DevicesNotFoundError", StringComparison.Ordinal) => ScannerErrorKind.NoCamera,
            var m when m.Contains("NotReadableError", StringComparison.Ordinal) ||
                       m.Contains("TrackStartError", StringComparison.Ordinal) => ScannerErrorKind.CameraUnavailable,
            var m when m.Contains("OverconstrainedError", StringComparison.Ordinal) ||
                       m.Contains("ConstraintNotSatisfiedError", StringComparison.Ordinal) =>
                ScannerErrorKind.ConstraintsUnsatisfied,
            var m when m.Contains("NotSupportedError", StringComparison.Ordinal) => ScannerErrorKind.NotSupported,
            _ => ScannerErrorKind.Unknown,
        };

        var description = kind switch
        {
            ScannerErrorKind.PermissionDenied => "Camera permission was denied.",
            ScannerErrorKind.NoCamera => "No camera matching the request was found.",
            ScannerErrorKind.CameraUnavailable => "The camera could not be opened; another application may be using it.",
            ScannerErrorKind.ConstraintsUnsatisfied => "No camera can satisfy the requested constraints.",
            ScannerErrorKind.NotSupported => "This browser does not expose a camera API to the page.",
            _ => "The camera could not be started.",
        };

        return new ScannerException(kind, description, exception);
    }
}

/// <summary>Source generated JSON contracts, so the library works with trimming and AOT.</summary>
[JsonSerializable(typeof(CameraInfo))]
[JsonSerializable(typeof(CameraDevice[]))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal sealed partial class ScannerJsonContext : JsonSerializerContext;
