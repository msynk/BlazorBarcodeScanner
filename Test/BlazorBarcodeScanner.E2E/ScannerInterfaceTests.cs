using Microsoft.Playwright;
using Xunit;

namespace BlazorBarcodeScanner.E2E;

/// <summary>
/// Checks the parts of the component a user meets rather than calls: every page of the demo,
/// keyboard operation, the accessible names, right-to-left layout, narrow screens, and what
/// happens when the camera is taken away mid-scan.
/// </summary>
[Collection(ScannerAppCollection.Name)]
public class ScannerInterfaceTests(ScannerAppFixture fixture)
{
    private static readonly string[] Routes =
    [
        "", "samples", "scan", "lifecycle", "camera", "region",
        "image", "performance", "customize", "errors", "api",
    ];

    [Fact]
    public async Task EveryPageLoadsWithoutErrors()
    {
        await using var session = await fixture.OpenAsync(fixture.BlankClip);

        foreach (var route in Routes)
        {
            await session.GoToAsync(route);

            var heading = await session.Page.TextContentAsync("h1");
            Assert.False(string.IsNullOrWhiteSpace(heading), $"Route '{route}' rendered no heading.");
        }

        Assert.Empty(session.PageErrors);
    }

    [Fact]
    public async Task AnUnknownRouteShowsTheNotFoundPage()
    {
        await using var session = await fixture.OpenAsync(fixture.BlankClip);
        await session.GoToAsync("no-such-page");

        var body = await session.Page.TextContentAsync("body");
        Assert.NotNull(body);
        Assert.Contains("not found", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(session.PageErrors);
    }

    [Fact]
    public async Task TheScannerIsALabelledRegionWithNamedControls()
    {
        await using var session = await fixture.OpenAsync();
        await session.GoToAsync("scan");
        await session.WaitForStatusAsync(["scanning"]);

        var role = await session.Page.GetAttributeAsync(".bscan", "role");
        var label = await session.Page.GetAttributeAsync(".bscan", "aria-label");
        Assert.Equal("region", role);
        Assert.False(string.IsNullOrWhiteSpace(label));

        // The video itself carries no information a screen reader can use.
        Assert.Equal("true", await session.Page.GetAttributeAsync(".bscan__video", "aria-hidden"));

        // Results are announced politely rather than interrupting.
        var live = await session.Page.GetAttributeAsync(".bscan__sr", "aria-live");
        Assert.Equal("polite", live);
        Assert.Equal("status", await session.Page.GetAttributeAsync(".bscan__sr", "role"));

        // Every built-in control is a real button or input with an accessible name.
        var unnamed = await session.Page.EvalOnSelectorAllAsync<string[]>(
            ".bscan__controls button, .bscan__controls select, .bscan__controls input",
            @"nodes => nodes
                .filter(n => !(n.getAttribute('aria-label') || n.textContent.trim()))
                .map(n => n.outerHTML)");

        Assert.Empty(unnamed);
    }

    [Fact]
    public async Task TheDecodedValueIsAnnouncedInTheLiveRegion()
    {
        await using var session = await fixture.OpenAsync();
        await session.GoToAsync("scan");
        await session.WaitForStatusAsync(["scanning"]);
        await session.WaitForResultsAsync(1);

        var announcement = await session.Page.TextContentAsync(".bscan__sr");
        Assert.False(string.IsNullOrWhiteSpace(announcement));
    }

    [Fact]
    public async Task TheBuiltInControlsAreReachableAndOperableFromTheKeyboard()
    {
        await using var session = await fixture.OpenAsync();
        await session.GoToAsync("scan");
        await session.WaitForStatusAsync(["scanning"]);

        // Tab until the pause button has focus, then operate it with the keyboard alone.
        var reached = false;
        for (var i = 0; i < 60 && !reached; i++)
        {
            await session.Page.Keyboard.PressAsync("Tab");
            reached = await session.Page.EvaluateAsync<bool>(
                "() => { const a = document.activeElement; return !!a && a.classList.contains('bscan__button') && a.textContent.trim() === 'Pause'; }");
        }

        Assert.True(reached, "The pause button was never reached by tabbing.");

        // A focused control must be visibly focused.
        var hasFocusRing = await session.Page.EvaluateAsync<bool>(
            @"() => {
                const style = getComputedStyle(document.activeElement);
                return style.boxShadow !== 'none' || style.outlineStyle !== 'none';
            }");
        Assert.True(hasFocusRing, "The focused control had no visible focus indicator.");

        await session.Page.Keyboard.PressAsync("Enter");
        await session.WaitForStatusAsync(["paused"]);

        var pressed = await session.Page.GetAttributeAsync(".bscan__button[aria-pressed]", "aria-pressed");
        Assert.Equal("true", pressed);
    }

    [Fact]
    public async Task TheLayoutMirrorsInARightToLeftDocument()
    {
        await using var session = await fixture.OpenAsync();
        await session.GoToAsync("scan");
        await session.WaitForStatusAsync(["scanning"]);

        var beforeStatus = await ControlBoxAsync(session, ".bscan__status");
        await session.Page.EvaluateAsync("() => document.documentElement.setAttribute('dir', 'rtl')");
        await session.Page.WaitForTimeoutAsync(200);

        // Logical properties mean the chrome keeps its block placement and the inline direction
        // flips, rather than the layout breaking.
        var afterStatus = await ControlBoxAsync(session, ".bscan__status");
        Assert.Equal(beforeStatus.Y, afterStatus.Y, 0);

        var corner = await session.Page.QuerySelectorAsync(".bscan__corner--tl");
        Assert.NotNull(corner);

        // The top-left corner of the reticle is drawn at the inline start, which in a
        // right-to-left document is the right hand side.
        var reticle = await ControlBoxAsync(session, ".bscan__reticle");
        var cornerBox = await corner.BoundingBoxAsync();
        Assert.NotNull(cornerBox);
        Assert.True(
            cornerBox.X + cornerBox.Width > reticle.X + (reticle.Width / 2),
            "The reticle did not mirror in a right-to-left document.");

        Assert.Empty(session.PageErrors);
    }

    [Fact]
    public async Task TheScannerFitsANarrowPhoneScreenWithoutSidewaysScrolling()
    {
        await using var session = await fixture.OpenAsync();
        await session.Page.SetViewportSizeAsync(360, 740);
        await session.GoToAsync("scan");
        await session.WaitForStatusAsync(["scanning"]);

        var overflows = await session.Page.EvaluateAsync<bool>(
            "() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");
        Assert.False(overflows, "The page scrolled sideways on a 360 pixel viewport.");

        var scanner = await ControlBoxAsync(session, ".bscan");
        Assert.True(scanner.Width <= 361, $"The scanner was {scanner.Width} pixels wide in a 360 pixel viewport.");

        // The control bar wraps rather than spilling out of the viewfinder.
        var controlsFit = await session.Page.EvaluateAsync<bool>(
            @"() => {
                const scanner = document.querySelector('.bscan').getBoundingClientRect();
                const controls = document.querySelector('.bscan__controls').getBoundingClientRect();
                return controls.right <= scanner.right + 1 && controls.left >= scanner.left - 1;
            }");
        Assert.True(controlsFit, "The control bar overflowed the scanner.");
    }

    [Fact]
    public async Task TheScannerKeepsTheCameraAspectRatioSoOverlaysLineUp()
    {
        await using var session = await fixture.OpenAsync();
        await session.GoToAsync("scan");
        await session.WaitForStatusAsync(["scanning"]);

        // The fake camera delivers 640x480, so the box must be 4:3 rather than a fixed guess.
        var ratio = await session.Page.EvaluateAsync<double>(
            @"() => { const r = document.querySelector('.bscan').getBoundingClientRect(); return r.width / r.height; }");

        Assert.InRange(ratio, 1.28, 1.39);
    }

    [Fact]
    public async Task LosingTheCameraMidScanIsReportedRatherThanHanging()
    {
        await using var session = await fixture.OpenAsync();
        await session.GoToAsync("scan");
        await session.WaitForStatusAsync(["scanning"]);
        await session.WaitForResultsAsync(1);

        // Stop the track from outside, exactly as unplugging a webcam or another application
        // claiming it would.
        await session.Page.EvaluateAsync("() => { for (const t of window.__bscanTracks.live) { t.stop(); t.dispatchEvent(new Event('ended')); } }");

        var status = await session.WaitForStatusAsync(["faulted"], timeoutMs: 20_000);
        Assert.Equal("faulted", status);

        var message = await session.Page.TextContentAsync(".bscan__message");
        Assert.NotNull(message);
        Assert.Contains("camera", message, StringComparison.OrdinalIgnoreCase);

        // And the scanner recovers when told to start again.
        await session.Page.ClickAsync(".bscan__button:has-text('Start')");
        await session.WaitForStatusAsync(["scanning"], timeoutMs: 20_000);
        Assert.Empty(session.PageErrors);
    }

    [Fact]
    public async Task ChoosingTheSameImageTwiceScansItBothTimes()
    {
        await using var session = await fixture.OpenAsync(fixture.BlankClip);
        await session.GoToAsync("image");

        var file = Path.Combine(fixture.VideoDirectory, "qr.png");
        if (!File.Exists(file))
        {
            await WriteQrPngAsync(session, file);
        }

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await session.Page.SetInputFilesAsync("input[type=file]", file);
            await session.WaitForResultsAsync(attempt, timeoutMs: 30_000);
        }

        var values = await session.ResultValuesAsync();
        Assert.Equal(2, values.Count);
        Assert.All(values, v => Assert.Equal(FakeCameraVideo.QrPayload, v));
    }

    [Fact]
    public async Task ScanningAGeneratedBufferUsesTheDecodeImageApi()
    {
        await using var session = await fixture.OpenAsync(fixture.BlankClip);
        await session.GoToAsync("image");

        await session.Page.ClickAsync("button:has-text('Decode a generated image')");
        await session.WaitForResultsAsync(1, timeoutMs: 30_000);

        Assert.Empty(session.PageErrors);
    }

    private static async Task WriteQrPngAsync(ScannerSession session, string path)
    {
        // Draw the symbol in the page and save the canvas, so the test needs no image library.
        var matrix = BlazorBarcodeScanner.TestKit.QrEncoder.Encode(FakeCameraVideo.QrPayload);
        var rows = new List<string>(matrix.Height);
        for (var y = 0; y < matrix.Height; y++)
        {
            var row = new char[matrix.Width];
            for (var x = 0; x < matrix.Width; x++)
            {
                row[x] = matrix[x, y] ? '1' : '0';
            }

            rows.Add(new string(row));
        }

        matrix.Dispose();

        var base64 = await session.Page.EvaluateAsync<string>(
            @"rows => {
                const scale = 8, quiet = 4;
                const size = (rows.length + quiet * 2) * scale;
                const canvas = document.createElement('canvas');
                canvas.width = size; canvas.height = size;
                const ctx = canvas.getContext('2d');
                ctx.fillStyle = '#fff'; ctx.fillRect(0, 0, size, size);
                ctx.fillStyle = '#000';
                for (let y = 0; y < rows.length; y++) {
                    for (let x = 0; x < rows[y].length; x++) {
                        if (rows[y][x] === '1') {
                            ctx.fillRect((x + quiet) * scale, (y + quiet) * scale, scale, scale);
                        }
                    }
                }
                return canvas.toDataURL('image/png').split(',')[1];
            }",
            rows);

        await File.WriteAllBytesAsync(path, Convert.FromBase64String(base64));
    }

    private static async Task<BoundingBox> ControlBoxAsync(ScannerSession session, string selector)
    {
        var element = await session.Page.QuerySelectorAsync(selector);
        Assert.NotNull(element);
        var box = await element.BoundingBoxAsync();
        Assert.NotNull(box);
        return new BoundingBox(box.X, box.Y, box.Width, box.Height);
    }

    private readonly record struct BoundingBox(float X, float Y, float Width, float Height);
}
