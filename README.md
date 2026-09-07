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
- [Performance](#performance)
- [Architecture](#architecture)
- [PDF417 status](#pdf417-status)
- [Repository layout](#repository-layout)
- [Building and testing](#building-and-testing)

## Symbologies

| Symbology | Status | Notes |
| --- | --- | --- |
| QR Code | Supported | Versions 1–40, all four error correction levels, all eight masks, ECI, structured append, mirrored symbols |
| Data Matrix | Supported | ECC 200, all 24 square and 6 rectangular shapes, ASCII / C40 / Text / X12 / EDIFACT / Base 256 |
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
| `RequestedWidth` / `RequestedHeight` | `int` | `1280` / `720` | Camera constraints; the browser may negotiate otherwise. |
| `MaxProcessingSize` | `int` | `640` | Longest edge of the image the decoder sees. |
| `Facing` | `CameraFacing` | `Rear` | Preferred camera when no device id is given. |
| `DeviceId` | `string?` | `null` | A specific camera to open. |
| `AutoStart` | `bool` | `true` | Open the camera as soon as the component is ready. |
| `DuplicateSuppressionWindow` | `TimeSpan` | 2 s | How long an identical value stays suppressed. |
| `ShowControls`, `ShowStatusBar`, `ShowReticle`, `ShowMask`, `HighlightResults` | `bool` | `true` | Built-in chrome. |
| `EnableFileScanning` | `bool` | `true` | Offer still image scanning. |
| `Fill` | `bool` | `false` | Fill the parent height instead of a 4:3 box. |
| `Overlay` | `RenderFragment<BarcodeScanner>?` | `null` | Replaces the reticle; receives the scanner. |
| `MessageTemplate` | `RenderFragment<string>?` | `null` | Replaces status and error message rendering. |
| `ControlsContent`, `ChildContent` | `RenderFragment?` | `null` | Extra content. |

### Events

| Event | Payload | Raised when |
| --- | --- | --- |
| `OnScan` | `BarcodeResult` | A symbol is decoded and passes duplicate suppression. |
| `OnError` | `ScannerException` | The camera cannot start, or a frame fails. |
| `OnStatusChanged` | `ScannerStatus` | The lifecycle state changes. |
| `OnCameraChanged` | `CameraInfo` | A camera opens, with what was negotiated. |
| `OnDiagnostics` | `ScanDiagnostics` | At most twice a second, with frame timings. |

### Methods

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
```

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
cropped scan region, so an overlay drawn from them lines up with what the user sees.

### Styling

Every colour, radius and dimension is a CSS custom property on `.bscan`, so an application
restyles the scanner without overriding a single selector:

```css
.my-scanner {
    --bscan-accent: #35c77c;
    --bscan-reticle-color: #35c77c;
    --bscan-reticle-thickness: 4px;
    --bscan-radius: 1.5rem;
    --bscan-overlay: rgba(0, 0, 0, 0.7);
}
```

The full list is in [`blazor-barcode-scanner.css`](src/BlazorBarcodeScanner/wwwroot/blazor-barcode-scanner.css) and on the
demo's customisation page.

The component is a labelled region, the controls are real buttons with pressed state, results are
announced through a polite live region, and the layout uses logical properties throughout so a
right-to-left document needs no extra work. Animation is suppressed under
`prefers-reduced-motion`, and colours switch to system colours under `forced-colors`.

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

`ScannerOptions.ForStillImages()` flips the trade-offs for one-shot decoding: more scan lines, the
exhaustive detector path, and inverted symbols allowed.

## Performance

Measured with BenchmarkDotNet on .NET 10, x64, 640×480 grayscale frames with the symbol centred,
short run (3 iterations). Absolute numbers on WebAssembly are several times larger; the ratios
are what transfer.

| Frame | All formats | QR only | Linear only |
| --- | ---: | ---: | ---: |
| QR, 29×29 modules | 395 µs | 394 µs | 33 µs |
| QR, 57×57 modules | 500 µs | 491 µs | 48 µs |
| QR with sensor noise | 2 131 µs | 2 089 µs | 788 µs |
| Data Matrix | 405 µs | 383 µs | 35 µs |
| Code 128 | 405 µs | 431 µs | 16 µs |
| EAN-13 | 414 µs | 401 µs | 10 µs |
| Empty frame | 406 µs | 411 µs | 21 µs |

Allocation per frame:

| Case | Allocated |
| --- | ---: |
| Linear only, no symbol found | **0 B** |
| Linear only, EAN-13 found | 416 B |
| All formats, empty frame | 352 B |
| All formats, QR found | 2 848 B |

A frame that finds nothing is the common case while a user is still aiming, and it allocates
nothing on the linear path and only the result-free scaffolding elsewhere. That is what keeps
continuous scanning free of garbage collection pauses.

What costs what:

| Setting | Effect | When to change it |
| --- | --- | --- |
| `MaxProcessingSize` | Quadratic. The dominant control. | Lower until the smallest symbol you care about stops decoding, then go one step back. |
| `Formats` | Linear in the number of decoders that run. | Always. Most applications need one or two symbologies. |
| `Region` | Proportional to the area kept. | Whenever the interface asks the user to aim. |
| `MaxScanLines` | Linear, linear symbologies only. | Lower for aimed scanning, raise for hands-off scanning. |
| `TryHarder` | Roughly triples matrix detection cost. | Still images only; off for live frames by design. |
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
                                 │        ├─ HybridBinarizer   8×8 adaptive blocks   ─┐ matrix codes
                                 │        │  GlobalHistogram…  per-row histogram     ─┘ linear codes
                                 │        ├─ Detectors         finder patterns, L-shape, row patterns
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
  of stale frames.
- **Renders are rare.** Frames never enter the render tree. `ShouldRender` gates on state a user
  can actually see, so a scanner running at twenty frames a second renders a handful of times a
  minute.
- **Masks are applied while reading.** The QR data mask is undone during the module read rather
  than by XORing the whole matrix and undoing it afterwards, which keeps detector output immutable
  and saves two full passes.
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
src/tests/BlazorBarcodeScanner.Tests         unit and round-trip tests
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
- The QR block tables are checked against the published codeword totals for all 40 versions and
  all four error correction levels, and the format and version information codes against published
  constants.
- The Data Matrix shape table is checked for internal consistency: region tiling, and that the
  codeword placement covers every module of every data region exactly once.
- Reed–Solomon is checked over both fields by damaging codewords and requiring exact recovery.
- The pipeline is checked for zero allocations across repeated empty frames.
