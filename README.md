# BlazorBarcodeScanner

A barcode scanner for Blazor with the decoding written from scratch in C#.

No barcode SDK, no JavaScript scanner library, no component framework, no runtime NuGet
dependency beyond `Microsoft.AspNetCore.Components.Web`. The browser opens the camera and copies
pixels; everything after that — luminance conversion, binarisation, detection, Reed–Solomon
correction and bit stream parsing — is managed code you can step into.

```razor
<BarcodeScanner Formats="BarcodeFormat.QrCode | BarcodeFormat.Ean13"
                Region="ScanRegion.CenteredSquare(0.7)"
                OnScan="HandleScan" />

@code {
    private void HandleScan(BarcodeResult result)
        => Console.WriteLine($"{result.Format}: {result.Text}");
}
```

## Contents

- [Symbologies](#symbologies)
- [Installing](#installing)
- [Using the component](#using-the-component)
- [Using the decoder without Blazor](#using-the-decoder-without-blazor)
- [What it reads, and what it does not](#what-it-reads-and-what-it-does-not)
- [Performance](#performance)
- [Architecture](#architecture)
- [PDF417 status](#pdf417-status)
- [Repository layout](#repository-layout)
- [Building and testing](#building-and-testing)

## Symbologies

| Symbology | Status | Notes |
| --- | --- | --- |
| QR Code | Supported | Versions 1–40, all four error correction levels, all eight masks, ECI, structured append, mirrored symbols |
| Data Matrix | Supported | ECC 200, all 24 square and 6 rectangular shapes, ASCII / C40 / Text / X12 / EDIFACT / Base 256, ECI, structured append, GS1 |
| Code 128 | Supported | Code sets A, B and C, shifts, FNC1 (GS1-128) |
| Code 39 | Supported | Optional modulo 43 check character, optional full ASCII extension |
| EAN-13 | Supported | |
| EAN-8 | Supported | |
| UPC-A | Supported | Reported as UPC-A rather than as a zero-prefixed EAN-13 |
| UPC-E | Supported | Expanded and check-digit verified against its UPC-A form |
| ITF | Supported | Configurable accepted lengths |
| PDF417 | **Partial** | Fully implemented except the symbol character table; see [PDF417 status](#pdf417-status) |

## Installing

```bash
dotnet add package BlazorBarcodeScanner
```

Reference the stylesheet from `index.html`:

```html
<link rel="stylesheet" href="_content/BlazorBarcodeScanner/blazor-barcode-scanner.css" />
```

That is the whole setup. There is no service registration, no script tag and no initialisation
call: the component imports its own JavaScript module the first time it renders.

Continuous camera scanning requires **Blazor WebAssembly**. The component prerenders safely on
the server and activates once WebAssembly takes over; on hosting models where the browser interop
is unavailable it reports `ScannerErrorKind.NotSupported` rather than silently degrading.

The camera API also requires a secure context, so serve the application over HTTPS or from
`localhost`.

## Using the component

### Parameters

| Parameter | Type | Default | Purpose |
| --- | --- | --- | --- |
| `Formats` | `BarcodeFormat` | `All` | Symbologies to look for. A flags enum. |
| `Region` | `ScanRegion` | `Full` | Normalized sub-rectangle to search. |
| `Options` | `ScannerOptions?` | `null` | Advanced tuning, applied under `Formats` and `Region`. |
| `TargetFrameRate` | `int` | `15` | Frames a second to decode. |
| `RequestedWidth` / `RequestedHeight` | `int` | `1280` / `720` | Camera constraints, requested as *ideal*, so the browser negotiates the nearest mode rather than failing. |
| `MaxProcessingSize` | `int` | `640` | Longest edge of the image the decoder sees. |
| `Facing` | `CameraFacing` | `Rear` | Preferred camera when no device id is given. |
| `DeviceId` | `string?` | `null` | A specific camera to open. Changing it while running switches cameras. |
| `AutoStart` | `bool` | `true` | Open the camera as soon as the component is ready. |
| `DuplicateSuppressionWindow` | `TimeSpan` | 2 s | How long an identical value stays suppressed. |
| `ShowControls`, `ShowStatusBar`, `ShowReticle`, `ShowMask`, `HighlightResults` | `bool` | `true` | Built-in chrome. |
| `EnableFileScanning` | `bool` | `true` | Offer still image scanning. |
| `Fill` | `bool` | `false` | Fill the parent instead of keeping the camera's aspect ratio. |
| `AriaLabel` | `string` | `"Barcode scanner"` | Accessible name for the scanner region. |
| `CssClass` | `string?` | `null` | Extra classes for the root element. |
| `Overlay` | `RenderFragment<BarcodeScanner>?` | `null` | Replaces the reticle; receives the scanner. |
| `MessageTemplate` | `RenderFragment<string>?` | `null` | Replaces status and error message rendering. |
| `ControlsContent`, `ChildContent` | `RenderFragment?` | `null` | Extra content. |

Any other attribute — `id`, `style`, `data-*`, a `class` of your own — is applied to the root
element. A `class` or `style` supplied that way is merged with the component's own rather than
replacing it.

### Events

| Event | Payload | Raised when |
| --- | --- | --- |
| `OnScan` | `BarcodeResult` | A symbol is decoded and passes duplicate suppression. |
| `OnError` | `ScannerException` | The camera cannot start, or is lost while running. |
| `OnStatusChanged` | `ScannerStatus` | The lifecycle state changes. |
| `OnCameraChanged` | `CameraInfo` | A camera opens, with what was negotiated. |
| `OnDiagnostics` | `ScanDiagnostics` | At most twice a second, with frame timings. |

An exception thrown by one of these handlers is routed to the renderer, exactly as Blazor treats
an exception from an `@onclick` handler, rather than being swallowed or killing the frame loop.

### Methods and properties

```csharp
Task            StartAsync(string? deviceId = null)
Task            StopAsync()
void            Pause()
void            Resume()
Task            SwitchCameraAsync(string deviceId)
Task<bool>      SetTorchAsync(bool on)
Task<bool>      SetZoomAsync(double zoom)
Task            OpenFilePickerAsync()
Task<IReadOnlyList<CameraDevice>> GetCamerasAsync()
BarcodeResult?  DecodeImage(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format)

ScannerStatus              Status
CameraInfo?                Camera
IReadOnlyList<CameraDevice> Cameras
ScanDiagnostics            Diagnostics
long                       FramesDecoded
long                       FramesWithSymbol
bool                       IsTorchOn
double                     Zoom
(int Width, int Height)    ProcessingSize
```

`StartAsync` is safe to call at any time and from anywhere: a call that arrives while an earlier
one is still waiting for the permission prompt supersedes it, and the superseded request releases
its camera instead of leaving it running. The same holds for `StopAsync` and for disposal, so a
user who navigates away mid-prompt never leaves the camera indicator lit.

### Results

```csharp
public sealed class BarcodeResult
{
    public string Text { get; }                          // decoded value
    public ReadOnlyMemory<byte> RawBytes { get; }        // payload before character set interpretation
    public BarcodeFormat Format { get; }
    public DateTimeOffset Timestamp { get; }
    public IReadOnlyList<ScanPoint> Corners { get; }     // in frame pixel coordinates
    public ScanRect BoundingBox { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public BarcodeResultMetadata? Metadata { get; }      // version, EC level, corrections, ECI, GS1, structured append
}
```

Corner points are always reported in the coordinates of the frame the camera produced, not of a
cropped scan region, so an overlay drawn from them lines up with what the user sees. The built-in
highlight also accounts for a mirrored front camera and for cropping under `Fill`.

### Styling

Every colour, radius and dimension is a CSS custom property, declared in a `:where(.bscan)` block
so that it carries no specificity at all. An application therefore restyles the scanner without
overriding a single selector, and without depending on stylesheet order:

```css
.my-scanner {
    --bscan-accent: #35c77c;
    --bscan-reticle-color: #35c77c;
    --bscan-reticle-thickness: 4px;
    --bscan-radius: 1.5rem;
    --bscan-overlay: rgba(0, 0, 0, 0.7);
}
```

Because the properties are read on the root element, setting them on an ancestor works too.

The full list is in [`blazor-barcode-scanner.css`](src/BlazorBarcodeScanner/wwwroot/blazor-barcode-scanner.css) and on the
demo's customisation page.

The component is a labelled region, the controls are real buttons with pressed state, results are
announced through a polite live region, and the layout uses logical properties throughout so a
right-to-left document needs no extra work. Animation is suppressed under
`prefers-reduced-motion`. Under `forced-colors` the reticle, highlight and control borders switch
to system colours, focus rings stay visible, and the video and its dimming mask opt out of colour
substitution so the viewfinder is still a viewfinder.

## Using the decoder without Blazor

The pipeline has no dependency on Blazor, JavaScript or a camera. It runs anywhere .NET runs,
which is what makes it unit testable and benchmarkable.

```csharp
using var decoder = new BarcodeDecoder(new ScannerOptions
{
    Formats = BarcodeFormat.QrCode | BarcodeFormat.DataMatrix,
    DuplicateSuppressionWindow = TimeSpan.Zero,
});

var result = decoder.Decode(rgbaPixels, width, height, PixelFormat.Rgba32);
if (result is not null)
{
    Console.WriteLine($"{result.Format}: {result.Text}");
    Console.WriteLine($"took {decoder.LastFrame.Total.TotalMilliseconds:0.00} ms");
}
```

One instance owns all the scratch state a frame needs and is **not** thread safe; use one per
scanning session. `ScannerOptions.ForStillImages()` flips the trade-offs for one-shot decoding:
more scan lines, every ten degrees of rotation, the exhaustive detector path, and inverted
symbols allowed.

### Tuning

| Setting | Default | Effect |
| --- | --- | --- |
| `Formats` | `All` | Only the decoders that can produce a requested format are created. |
| `Region` | `Full` | Restricts every stage proportionally. |
| `MaxScanLines` | `15` | Scan lines per orientation for linear symbologies. |
| `DownsampleFactor` | `1` | Subsamples while converting to grayscale. Quadratic. |
| `TryHarder` | `false` | Exhaustive detection: every row for finder patterns, more candidate regions, the full rotation sweep. |
| `AllowInverted` | `false` | Also read light-on-dark symbols. Roughly doubles the cost of a frame that finds nothing. |
| `TryReversedRows` | `true` | Also read linear symbols right to left. |
| `TryVerticalLines` | `true` | Also scan columns and tilted lines, so a rotated linear symbol is read. |
| `ItfLengths` | 6–20 even | Digit counts an ITF result may have. ITF has no check character, so this is the main defence against a truncated read. |
| `Code39CheckDigit` | `false` | Require and strip the optional modulo 43 check character. |
| `Code39ExtendedMode` | `false` | Interpret Code 39 as full ASCII. |
| `DuplicateSuppressionWindow` | 2 s | How long an identical value stays suppressed. |

## What it reads, and what it does not

The decoders are exercised against transformed renders — rotated, tilted, blurred, noisy,
unevenly lit, scaled, off-centre and on cluttered backgrounds — rather than only against clean
round trips. What that establishes:

- **Rotation.** Matrix symbols read at any angle. Linear symbols read within about 30 degrees of
  upright or sideways while scanning live, which is the range a hand actually produces, and at
  any angle with `ForStillImages()`, which sweeps every ten degrees plus both diagonals.
- **Perspective.** Matrix symbols tolerate roughly 30 degrees of tilt.
- **Position.** A symbol anywhere in the frame is found, not only one in the middle.
- **Lighting.** Strong gradients, low contrast and underexposure are handled by the adaptive
  binarisers; the block threshold estimates the frame's noise floor so that grainy blank paper is
  still recognised as blank.
- **Focus and noise.** Roughly a pixel of blur, and sensor noise up to about a quarter of full
  scale, on four-pixel modules.

What it does not do: read symbols whose modules are below about two pixels, recover from blur
much wider than a module, or read a linear symbol rotated more than 30 degrees during live
scanning. Misreading is treated as worse than missing throughout: ITF and unchecked Code 39
results must repeat on a second scan line before they are reported, guard patterns must be
adjacent rather than merely present, and a Data Matrix grid must have a plausible finder and
timing pattern before its codewords are trusted.

## Performance

Measured with BenchmarkDotNet on .NET 10, x64, 640×480 grayscale frames with the symbol centred,
short run (3 iterations). Absolute numbers on WebAssembly are several times larger; the ratios
are what transfer.

| Frame | All formats | QR only | Linear only |
| --- | ---: | ---: | ---: |
| QR, 29×29 modules | 603 µs | 546 µs | 206 µs |
| QR, 57×57 modules | 701 µs | 646 µs | 569 µs |
| QR with sensor noise | 702 µs | 748 µs | 4 509 µs |
| Data Matrix | 793 µs | 618 µs | 222 µs |
| Code 128 | 940 µs | 695 µs | 21 µs |
| EAN-13 | 837 µs | 697 µs | 13 µs |
| Empty frame | 876 µs | 572 µs | 38 µs |

The "linear only" column on a frame that holds a matrix symbol is the cost of looking for
something that is not there at every orientation, which is the price of reading tilted symbols.
It is only paid when no enabled symbology matches, and narrowing `Formats` removes it.

Allocation per frame:

| Case | Allocated |
| --- | ---: |
| Linear only, no symbol found | **0 B** |
| QR only, empty frame | 40 B |
| All formats, empty frame | 152 B |
| Linear only, EAN-13 found | 496 B |
| All formats, QR found | 2 552 B |

A frame that finds nothing is the common case while a user is still aiming. It allocates nothing
at all on the linear path, and on the matrix path only the bit plane's wrapper object, whose
buffer is pooled. Detectors, pattern finders and their callbacks are all reused across frames
rather than rebuilt, which is what keeps continuous scanning free of collection pauses.

Three decisions do most of the work in that table. Scan lines are probed for contrast at a
quarter resolution before the line is gathered, so the many lines that cross nothing but
background cost a fraction of a full read. The block binariser estimates the frame's noise floor
instead of using a fixed threshold, which stops a grainy frame from binarising into a field of
speckle: that alone made a noisy frame roughly three times faster to reject, as well as
decodable. And every detector, pattern finder and callback is built once and reused, so the only
thing a frame allocates is the bit plane's wrapper.

Where the time goes on a 640×480 frame:

| Stage | Cost |
| --- | ---: |
| Grayscale conversion from RGBA | 328 µs |
| Grayscale with 2× subsampling | 120 µs |
| Adaptive block binarisation | 494 µs |
| Binarising one scan line | 1.0 µs |
| QR detection and decode, symbol present | 82 µs |
| Data Matrix detection, nothing present | 204 µs |
| Code 128 on a row with no symbol | 1.5 µs |

The two whole-image stages dominate, which is why `MaxProcessingSize` and `DownsampleFactor` are
the controls that matter most.

What costs what:

| Setting | Effect | When to change it |
| --- | --- | --- |
| `MaxProcessingSize` | Quadratic. The dominant control. | Lower until the smallest symbol you care about stops decoding, then go one step back. |
| `Formats` | Linear in the number of decoders that run. | Always. Most applications need one or two symbologies. |
| `Region` | Proportional to the area kept. | Whenever the interface asks the user to aim. |
| `MaxScanLines` | Linear, linear symbologies only. | Lower for aimed scanning, raise for hands-off scanning. |
| `TryVerticalLines` | Rows only when off; six orientations when on. | Turn off only if symbols are always upright. |
| `TryHarder` | Sweeps eighteen orientations and many more detector candidates. | Still images only; off for live frames by design. |
| `AllowInverted` | Doubles the cost of frames that find nothing. | Only when white-on-black symbols are expected. |

Run them yourself:

```bash
dotnet run -c Release --project src/tests/BlazorBarcodeScanner.Benchmarks -- --filter '*DecoderBenchmarks*'
```

The benchmark host verifies that every frame still decodes before taking any timing, because a
benchmark that silently stops finding the symbol measures the failure path instead.

## Architecture

```
Browser                          │ Managed
─────────────────────────────────┼──────────────────────────────────────────────
getUserMedia → <video>           │
canvas.drawImage (scales down)   │
canvas.getImageData              │
        │                        │
        └── JSImport MemoryView ──┼──► byte[] (reused, one per session)
                                 │        │
                                 │        ├─ PixelConverter    RGBA → luminance, optional subsample
                                 │        ├─ HybridBinarizer   8×8 adaptive blocks, noise floor  ─┐ matrix codes
                                 │        │  AdaptiveRowBin…   per-segment thresholds            ─┘ linear codes
                                 │        ├─ DarkRegionFinder  connected ink, for symbols with no finder
                                 │        ├─ Detectors         finder patterns, edge fitting, line sweep
                                 │        ├─ GridSampler       perspective transform + sampling
                                 │        ├─ ReedSolomon       GF(256) and GF(929)
                                 │        └─ Bit stream parsers per symbology
                                 │                 │
                                 │                 └──► BarcodeResult
```

Layers, and where they live:

| Layer | Namespace | Responsibility |
| --- | --- | --- |
| Component | `BlazorBarcodeScanner` | Parameters, rendering, lifecycle, overlays |
| Interop | `BlazorBarcodeScanner.Interop` | The complete JavaScript surface, all `JSImport` |
| Orchestration | `BlazorBarcodeScanner.Pipeline` | Frame budget, scratch state, duplicate suppression, diagnostics |
| Imaging | `BlazorBarcodeScanner.Imaging` | Luminance planes, bit planes, binarisers |
| Decoding | `BlazorBarcodeScanner.Decoding.*` | One namespace per symbology, plus shared algorithms |

Design decisions worth knowing about:

- **One copy per frame.** `JSImport` with a `JSType.MemoryView` parameter hands JavaScript a view
  straight onto the managed buffer. No JSON, no base64, no intermediate array.
- **The browser downsamples.** Frames are scaled to `MaxProcessingSize` by the canvas before they
  cross the boundary, so the expensive stages never see pixels they do not need.
- **Backpressure by construction.** The frame loop asks for the *latest* frame on a timer and gets
  zero back when nothing new has arrived. A slow decode misses ticks rather than building a queue
  of stale frames. A track that ends, because the camera was unplugged or claimed by another
  application, is reported as an error rather than spinning.
- **Renders are rare.** Frames never enter the render tree. `ShouldRender` gates on state a user
  can actually see, so a scanner running at twenty frames a second renders a handful of times a
  minute.
- **Detection proposes, decoding disposes.** Both matrix detectors offer several candidate grids
  — with and without the alignment pattern, at neighbouring dimensions, from several patches of
  ink — and the first one that actually decodes wins. Committing to a single geometric guess is
  what makes a detector fail on symbols that are perfectly readable.
- **Failure is not exceptional.** Most frames contain no barcode, so every decode path reports
  failure by returning `null`, never by throwing.

Extension points: `IRowDecoder` for a new linear symbology, `IMatrixDecoder` for a new matrix one,
`IBinarizer` for a different thresholding strategy. All three receive already-prepared input, so a
new reader only contributes its own tables and structure.

## PDF417 status

PDF417 is implemented end to end — row detection, row indicator interpretation, Reed–Solomon over
GF(929), and the text, byte and numeric compaction modes — with one exception: **the symbol
character table**.

That table maps the 2 787 legal seventeen-module patterns to codeword values, and it is pure
specification data from ISO/IEC 15438. It cannot be derived from the structural rules. Of the
10 480 patterns that satisfy every structural constraint, the three usable clusters hold 1 484,
1 002 and 1 002 members, and no linear function of the eight element widths selects 929 from each;
the specification simply lists them.

So the library ships a **generated placeholder** table. It is internally consistent — it round
trips against the matching encoder and exercises every other part of the implementation — but it
does not match the specification and will not read real PDF417 symbols. To avoid pretending
otherwise:

- `Pdf417SymbolTable.Current.IsSpecificationTable` is `false`.
- `BarcodeDecoder` does **not** create its PDF417 reader while that is the case, so enabling
  `BarcodeFormat.Pdf417` costs nothing and silently finds nothing rather than burning CPU.

Supplying the real table makes PDF417 fully functional:

```csharp
Pdf417SymbolTable.Register(
    Pdf417SymbolTable.FromPatterns(cluster0, cluster3, cluster6, isSpecificationTable: true));
```

Each argument is 929 seventeen-bit patterns indexed by codeword value, most significant bit first,
starting with a bar.

## Repository layout

```
src/BlazorBarcodeScanner                     the library
src/BlazorBarcodeScanner.Demo                documentation site and reference implementation
src/tests/BlazorBarcodeScanner.TestKit       encoders and image helpers, shared by tests, benchmarks and the demo
src/tests/BlazorBarcodeScanner.Tests         unit, round-trip and image robustness tests
src/tests/BlazorBarcodeScanner.E2E           browser tests: a real page, a real camera stream
src/tests/BlazorBarcodeScanner.Benchmarks    BenchmarkDotNet suites
```

The encoders live in the test kit, not in the library: the library only reads barcodes. They exist
so that every decoder can be validated by round trip instead of by binary fixtures, and so that the
demo can render real, scannable symbols.

## Building and testing

```bash
dotnet build src/BlazorBarcodeScanner.slnx
dotnet test src/BlazorBarcodeScanner.slnx
dotnet run --project src/BlazorBarcodeScanner.Demo
```

The demo doubles as the documentation site: a live playground for every parameter, generated
sample barcodes for every symbology, an in-browser decoder benchmark, and pages for camera
control, scan regions, image scanning, customisation and error handling.

### What the tests cover

- Every symbology round trips from a generated symbol through a rendered image.
- Every symbology is decoded again through camera-like transforms: rotation, perspective, blur,
  sensor noise, lighting gradients, low contrast, inversion, scaling, off-centre placement and
  cluttered backgrounds.
- Misreads are tested for directly. A truncated ITF or Code 39 symbol, a damaged EAN-13, an
  out-of-tolerance print and twenty thousand random scan lines must all produce nothing.
- Malformed Data Matrix codeword streams — reserved values, a misplaced unlatch, truncated ECI —
  must be rejected rather than partially accepted, and the encodation modes the test kit's encoder
  cannot emit are driven directly.
- The QR block tables are checked against the published codeword totals for all 40 versions and
  all four error correction levels, and the format and version information codes against published
  constants.
- The Data Matrix shape table is checked for internal consistency: region tiling, and that the
  codeword placement covers every module of every data region exactly once.
- Reed–Solomon is checked over both fields by damaging codewords and requiring exact recovery.
- The pipeline is checked for zero allocations across repeated empty frames.

### Browser tests

`src/tests/BlazorBarcodeScanner.E2E` runs the demo in a real browser whose camera is a generated
video file, so the whole stack is exercised: the WebAssembly runtime, the JavaScript module, a
real `MediaStream`, the frame loop and the decoder. Only the sensor is synthetic.

They cover starting, continuous scanning of four symbologies, pausing, resuming, stopping,
switching cameras, repeated start/stop cycles, superseded starts, navigation, reload, denied
permission, heap growth across a minute of continuous scanning, and the promise that frames never
reach the render tree. Camera tracks are counted as they are opened and stopped, so a leaked
stream fails a test rather than only lighting up the camera indicator.

The tests drive an installed Chrome or Edge, so there is no browser download step; if neither is
present they skip.

```bash
dotnet test src/tests/BlazorBarcodeScanner.E2E
```
