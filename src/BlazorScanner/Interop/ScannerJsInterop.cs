using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace BlazorScanner.Interop;

/// <summary>
/// The complete JavaScript surface of the library.
/// </summary>
/// <remarks>
/// <para>
/// Every method here is a source generated <see cref="JSImportAttribute"/> binding, not a
/// <c>IJSRuntime.InvokeAsync</c> call. That matters for the frame path: a JSImport call is a
/// direct invocation with no JSON serialisation, and a <see cref="JSType.MemoryView"/> parameter
/// hands JavaScript a view straight onto the managed buffer, so a camera frame is copied exactly
/// once, by the browser, into memory the decoder already owns.
/// </para>
/// <para>
/// The control methods could have used ordinary Blazor interop, but keeping them here means the
/// whole bridge is one module with one lifetime, and the component never needs an
/// <see cref="Microsoft.JSInterop.IJSObjectReference"/> at all.
/// </para>
/// </remarks>
[SupportedOSPlatform("browser")]
internal static partial class ScannerJsInterop
{
    private const string ModuleName = "blazor-scanner";

    /// <summary>Path the JavaScript module is served from by the static web assets pipeline.</summary>
    internal const string ModulePath = "./_content/BlazorScanner/blazor-scanner.js";

    private static bool _imported;

    /// <summary>Loads the JavaScript module once per application.</summary>
    internal static async Task EnsureImportedAsync()
    {
        if (_imported)
        {
            return;
        }

        await JSHost.ImportAsync(ModuleName, ModulePath).ConfigureAwait(false);
        _imported = true;
    }

    [JSImport("listCameras", ModuleName)]
    internal static partial Task<string> ListCamerasAsync();

    [JSImport("start", ModuleName)]
    internal static partial Task<string> StartAsync(
        string sessionId,
        string videoElementId,
        string? deviceId,
        int requestedWidth,
        int requestedHeight,
        string? facingMode);

    [JSImport("configure", ModuleName)]
    internal static partial void Configure(string sessionId, int width, int height);

    [JSImport("grabFrame", ModuleName)]
    internal static partial int GrabFrame(
        string sessionId,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);

    [JSImport("setTorch", ModuleName)]
    internal static partial Task<bool> SetTorchAsync(string sessionId, bool on);

    [JSImport("setZoom", ModuleName)]
    internal static partial Task<bool> SetZoomAsync(string sessionId, double value);

    [JSImport("describe", ModuleName)]
    internal static partial string Describe(string sessionId);

    [JSImport("stop", ModuleName)]
    internal static partial void Stop(string sessionId);

    [JSImport("dispose", ModuleName)]
    internal static partial void DisposeSession(string sessionId);

    [JSImport("prepareImageFromInput", ModuleName)]
    internal static partial Task<int> PrepareImageFromInputAsync(
        string sessionId,
        string inputElementId,
        int maxDimension);

    [JSImport("readPrepared", ModuleName)]
    internal static partial int ReadPrepared(
        string sessionId,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> buffer);

    [JSImport("click", ModuleName)]
    internal static partial void Click(string elementId);

    [JSImport("isCameraSupported", ModuleName)]
    internal static partial bool IsCameraSupported();

    [JSImport("isSecureContext", ModuleName)]
    internal static partial bool IsSecureContext();
}
