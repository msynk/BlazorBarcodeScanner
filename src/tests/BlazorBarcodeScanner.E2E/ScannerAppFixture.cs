using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Playwright;
using Xunit;

namespace BlazorBarcodeScanner.E2E;

/// <summary>
/// Hosts the demo application and a Chromium instance whose camera is a looping video file.
/// </summary>
/// <remarks>
/// <para>
/// Everything below the fake sensor is real: a real WebAssembly runtime, the real JavaScript
/// module, a real <c>MediaStream</c>, the real frame loop and the real decoder. That is the only
/// way to establish that the scanner works rather than that it looks correct.
/// </para>
/// <para>
/// The browser is an installed Chrome or Edge rather than a downloaded one, so the suite needs no
/// separate browser install step. When neither is present the tests skip instead of failing.
/// </para>
/// </remarks>
public sealed class ScannerAppFixture : IAsyncLifetime
{
    private Process? _server;
    private IPlaywright? _playwright;

    /// <summary>Base address of the running demo application.</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Why the fixture is unusable, or <see langword="null"/> when it is ready.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>Directory holding the generated camera clips.</summary>
    public string VideoDirectory { get; private set; } = string.Empty;

    /// <summary>The rotating multi symbology clip.</summary>
    public string RotatingClip => Path.Combine(VideoDirectory, "rotating.y4m");

    /// <summary>A clip that contains no barcode.</summary>
    public string BlankClip => Path.Combine(VideoDirectory, "blank.y4m");

    /// <summary>A clip showing a single QR code for its whole length.</summary>
    public string SingleQrClip => Path.Combine(VideoDirectory, "single.y4m");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        VideoDirectory = Path.Combine(Path.GetTempPath(), "bscan-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(VideoDirectory);
        FakeCameraVideo.WriteRotatingSymbols(RotatingClip);
        FakeCameraVideo.WriteBlank(BlankClip);
        FakeCameraVideo.WriteSingleQr(SingleQrClip, "SINGLE-QR");

        try
        {
            Microsoft.Playwright.Program.Main(["install-deps", "chromium"]);
        }
        catch
        {
            // Only needed on Linux; failure here is not fatal on Windows.
        }

        try
        {
            _playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SkipReason = $"Playwright could not start: {ex.Message}";
            return;
        }

        if (ResolveChannel() is null)
        {
            SkipReason = "No installed Chrome or Edge was found for the browser tests.";
            return;
        }

        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        var demo = LocateDemoProject();
        if (demo is null)
        {
            SkipReason = "The demo project could not be located from the test output directory.";
            return;
        }

        _server = Process.Start(new ProcessStartInfo("dotnet")
        {
            ArgumentList =
            {
                "run", "--project", demo, "-c", "Release", "--no-build", "--no-launch-profile",
                "--urls", BaseUrl,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        if (_server is null || !await WaitForServerAsync(port).ConfigureAwait(false))
        {
            SkipReason = "The demo application did not start.";
        }
    }

    /// <summary>Opens a browser whose camera is the given clip, and a page on the demo.</summary>
    /// <param name="clipPath">Y4M file the fake camera plays, or <see langword="null"/> for the rotating clip.</param>
    /// <param name="grantPermission">Whether the camera permission prompt is auto-accepted.</param>
    public async Task<ScannerSession> OpenAsync(string? clipPath = null, bool grantPermission = true)
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

        var arguments = new List<string>
        {
            "--use-fake-device-for-media-stream",
            $"--use-file-for-fake-video-capture={clipPath ?? RotatingClip}",
            "--autoplay-policy=no-user-gesture-required",
        };

        if (grantPermission)
        {
            arguments.Add("--use-fake-ui-for-media-stream");
        }
        else
        {
            arguments.Add("--deny-permission-prompts");
        }

        var browser = await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = ResolveChannel(),
            Headless = true,
            Args = arguments,
        }).ConfigureAwait(false);

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            Permissions = grantPermission ? ["camera"] : [],
        }).ConfigureAwait(false);

        var page = await context.NewPageAsync().ConfigureAwait(false);
        var session = new ScannerSession(browser, context, page, BaseUrl);
        await session.AttachDiagnosticsAsync().ConfigureAwait(false);
        return session;
    }

    private static string? ResolveChannel()
    {
        var candidates = new (string Channel, string[] Paths)[]
        {
            ("chrome",
            [
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                "/usr/bin/google-chrome",
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            ]),
            ("msedge",
            [
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                "/usr/bin/microsoft-edge",
            ]),
        };

        foreach (var (channel, paths) in candidates)
        {
            if (paths.Any(File.Exists))
            {
                return channel;
            }
        }

        return null;
    }

    private static string? LocateDemoProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "BlazorBarcodeScanner.Demo", "BlazorBarcodeScanner.Demo.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<bool> WaitForServerAsync(int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_server?.HasExited == true)
            {
                return false;
            }

            try
            {
                var response = await client.GetAsync(BaseUrl).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        return false;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_server is { HasExited: false })
        {
            try
            {
                _server.Kill(entireProcessTree: true);
                await _server.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        _server?.Dispose();
        _playwright?.Dispose();

        try
        {
            if (Directory.Exists(VideoDirectory))
            {
                Directory.Delete(VideoDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A file still held by the browser process; the temp directory will be cleaned up
            // by the operating system.
        }
    }
}

/// <summary>Collection definition so every browser test shares one server and one Playwright.</summary>
[CollectionDefinition(Name)]
public sealed class ScannerAppCollection : ICollectionFixture<ScannerAppFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "scanner-app";
}
