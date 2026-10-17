using Microsoft.Playwright;

namespace BlazorBarcodeScanner.E2E;

/// <summary>
/// One browser, one page, and the bookkeeping a scanner test needs: console errors, page
/// exceptions, and a count of the camera tracks the page has opened and released.
/// </summary>
public sealed class ScannerSession : IAsyncDisposable
{
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly string _baseUrl;
    private readonly List<string> _consoleErrors = [];
    private readonly List<string> _pageErrors = [];

    internal ScannerSession(IBrowser browser, IBrowserContext context, IPage page, string baseUrl)
    {
        _browser = browser;
        _context = context;
        Page = page;
        _baseUrl = baseUrl;
    }

    /// <summary>The page under test.</summary>
    public IPage Page { get; }

    /// <summary>Console messages logged at error level.</summary>
    public IReadOnlyList<string> ConsoleErrors => _consoleErrors;

    /// <summary>Unhandled exceptions that reached the page.</summary>
    public IReadOnlyList<string> PageErrors => _pageErrors;

    internal async Task AttachDiagnosticsAsync()
    {
        Page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                lock (_consoleErrors)
                {
                    _consoleErrors.Add(message.Text);
                }
            }
        };

        Page.PageError += (_, error) =>
        {
            lock (_pageErrors)
            {
                _pageErrors.Add(error);
            }
        };

        // Count every camera track the page opens and every one it stops, so a leaked stream is
        // observable from the test rather than only from the operating system's camera light.
        await Page.AddInitScriptAsync(@"
window.__bscanTracks = { opened: 0, stopped: 0, live: new Set() };
const originalGetUserMedia = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
navigator.mediaDevices.getUserMedia = async (constraints) => {
    const stream = await originalGetUserMedia(constraints);
    for (const track of stream.getTracks()) {
        window.__bscanTracks.opened++;
        window.__bscanTracks.live.add(track);
        const originalStop = track.stop.bind(track);
        track.stop = () => {
            if (window.__bscanTracks.live.delete(track)) {
                window.__bscanTracks.stopped++;
            }
            originalStop();
        };
        track.addEventListener('ended', () => {
            if (window.__bscanTracks.live.delete(track)) {
                window.__bscanTracks.stopped++;
            }
        });
    }
    return stream;
};
").ConfigureAwait(false);
    }

    /// <summary>Navigates to a demo route and waits for Blazor to finish booting.</summary>
    /// <param name="route">Route relative to the application root.</param>
    public async Task GoToAsync(string route)
    {
        await Page.GotoAsync($"{_baseUrl}/{route.TrimStart('/')}", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 60_000,
        }).ConfigureAwait(false);

        await Page.WaitForSelectorAsync("h1", new PageWaitForSelectorOptions { Timeout = 60_000 }).ConfigureAwait(false);
    }

    /// <summary>Navigates within the application without a full page load.</summary>
    /// <param name="linkText">Visible text of the navigation link to click.</param>
    public async Task NavigateByLinkAsync(string linkText)
    {
        await Page.ClickAsync($"nav a:has-text('{linkText}')").ConfigureAwait(false);
        await Page.WaitForSelectorAsync("h1", new PageWaitForSelectorOptions { Timeout = 30_000 }).ConfigureAwait(false);
    }

    /// <summary>Number of camera tracks the page has opened since it loaded.</summary>
    public Task<int> TracksOpenedAsync() => Page.EvaluateAsync<int>("window.__bscanTracks.opened");

    /// <summary>Number of camera tracks still running.</summary>
    public Task<int> TracksLiveAsync() => Page.EvaluateAsync<int>("window.__bscanTracks.live.size");

    /// <summary>Waits until the scanner element reports one of the given statuses.</summary>
    /// <param name="statuses">Accepted values of the scanner's <c>data-status</c> attribute.</param>
    /// <param name="timeoutMs">How long to wait.</param>
    public async Task<string> WaitForStatusAsync(string[] statuses, int timeoutMs = 30_000)
    {
        var selector = string.Join(", ", statuses.Select(s => $".bscan[data-status='{s}']"));
        await Page.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions { Timeout = timeoutMs }).ConfigureAwait(false);
        return await Page.GetAttributeAsync(".bscan", "data-status").ConfigureAwait(false) ?? string.Empty;
    }

    /// <summary>The scanner's current lifecycle status, as rendered on the root element.</summary>
    public async Task<string> StatusAsync() =>
        await Page.GetAttributeAsync(".bscan", "data-status").ConfigureAwait(false) ?? string.Empty;

    /// <summary>Waits until the demo's result log contains at least <paramref name="count"/> entries.</summary>
    /// <param name="count">Number of rows to wait for.</param>
    /// <param name="timeoutMs">How long to wait.</param>
    public async Task WaitForResultsAsync(int count, int timeoutMs = 60_000)
    {
        await Page.WaitForFunctionAsync(
            "n => document.querySelectorAll('[data-testid=\"result-row\"]').length >= n",
            count,
            new PageWaitForFunctionOptions { Timeout = timeoutMs }).ConfigureAwait(false);
    }

    /// <summary>The decoded values currently shown in the demo's result log, newest first.</summary>
    public async Task<IReadOnlyList<string>> ResultValuesAsync()
    {
        var values = await Page.EvalOnSelectorAllAsync<string[]>(
            "[data-testid='result-value']",
            "nodes => nodes.map(n => n.textContent.trim())").ConfigureAwait(false);
        return values;
    }

    /// <summary>The symbologies currently shown in the demo's result log, newest first.</summary>
    public async Task<IReadOnlyList<string>> ResultFormatsAsync()
    {
        var values = await Page.EvalOnSelectorAllAsync<string[]>(
            "[data-testid='result-format']",
            "nodes => nodes.map(n => n.textContent.trim())").ConfigureAwait(false);
        return values;
    }

    /// <summary>Reads the JavaScript heap size, for leak checks across long runs.</summary>
    public async Task<long> HeapBytesAsync()
    {
        var value = await Page.EvaluateAsync<long?>(
            "() => performance.memory ? performance.memory.usedJSHeapSize : null").ConfigureAwait(false);
        return value ?? 0;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _context.CloseAsync().ConfigureAwait(false);
        await _browser.CloseAsync().ConfigureAwait(false);
        await _browser.DisposeAsync().ConfigureAwait(false);
    }
}
