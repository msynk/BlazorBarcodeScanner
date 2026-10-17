using BlazorBarcodeScanner.Imaging;
using BlazorBarcodeScanner.Pipeline;
using BlazorBarcodeScanner.TestKit;
using BlazorBarcodeScanner.Decoding.QrCode;
using Xunit;

namespace BlazorBarcodeScanner.Tests;

/// <summary>
/// Exercises the contracts an application depends on: option validation, disposal, the duplicate
/// filter, the reported geometry, and the promise that a frame that finds nothing allocates
/// nothing.
/// </summary>
public class PublicApiTests
{
    private static LuminanceBuffer Qr(string content = "API") =>
        SyntheticImage.FromMatrix(QrEncoder.Encode(content), scale: 5);

    [Fact]
    public void OptionsValidateTheirRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScannerOptions { MaxScanLines = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScannerOptions { DownsampleFactor = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScannerOptions { DownsampleFactor = 9 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScannerOptions { DuplicateSuppressionWindow = TimeSpan.FromSeconds(-1) }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScannerOptions { ItfLengths = [7] }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScannerOptions { ItfLengths = [0] }.Validate());

        new ScannerOptions().Validate();
        ScannerOptions.ForStillImages().Validate();
    }

    [Fact]
    public void CloneCopiesEverySetting()
    {
        var options = new ScannerOptions
        {
            Formats = BarcodeFormat.Ean13,
            Region = new ScanRegion(0.1, 0.2, 0.3, 0.4),
            MaxScanLines = 7,
            DownsampleFactor = 3,
            TryHarder = true,
            AllowInverted = true,
            TryReversedRows = false,
            TryVerticalLines = false,
            ItfLengths = [14],
            Code39CheckDigit = true,
            Code39ExtendedMode = true,
            DuplicateSuppressionWindow = TimeSpan.FromSeconds(9),
        };

        var clone = options.Clone();

        Assert.Equal(options.Formats, clone.Formats);
        Assert.Equal(options.Region, clone.Region);
        Assert.Equal(options.MaxScanLines, clone.MaxScanLines);
        Assert.Equal(options.DownsampleFactor, clone.DownsampleFactor);
        Assert.Equal(options.TryHarder, clone.TryHarder);
        Assert.Equal(options.AllowInverted, clone.AllowInverted);
        Assert.Equal(options.TryReversedRows, clone.TryReversedRows);
        Assert.Equal(options.TryVerticalLines, clone.TryVerticalLines);
        Assert.Equal(options.ItfLengths, clone.ItfLengths);
        Assert.Equal(options.Code39CheckDigit, clone.Code39CheckDigit);
        Assert.Equal(options.Code39ExtendedMode, clone.Code39ExtendedMode);
        Assert.Equal(options.DuplicateSuppressionWindow, clone.DuplicateSuppressionWindow);
    }

    [Fact]
    public void TheDecoderTakesACopyOfItsOptions()
    {
        var options = new ScannerOptions { Formats = BarcodeFormat.QrCode };
        using var decoder = new BarcodeDecoder(options);

        // Editing the caller's instance afterwards must not reconfigure a running scanner.
        options.Formats = BarcodeFormat.None;

        using var image = Qr();
        Assert.NotNull(decoder.Decode(image.View, suppressDuplicates: false));
    }

    [Fact]
    public void DisposedDecodersRefuseToWork()
    {
        var decoder = new BarcodeDecoder();
        decoder.Dispose();
        decoder.Dispose();

        using var image = Qr();
        Assert.Throws<ObjectDisposedException>(() => decoder.Decode(image.View));
    }

    [Fact]
    public void DecodeRejectsImpossibleDimensions()
    {
        using var decoder = new BarcodeDecoder();
        var pixels = new byte[64 * 64];

        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Decode(pixels, 0, 64, PixelFormat.Gray8));
        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.Decode(pixels, 64, -1, PixelFormat.Gray8));
        Assert.Throws<ArgumentException>(() => decoder.Decode(pixels, 4, 4, PixelFormat.Gray8));
    }

    [Fact]
    public void TheDuplicateWindowCanBeChangedWhileRunning()
    {
        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            DuplicateSuppressionWindow = TimeSpan.FromMinutes(5),
        });

        using var image = Qr("WINDOW");
        Assert.NotNull(decoder.Decode(image.View));
        Assert.Null(decoder.Decode(image.View));

        decoder.DuplicateSuppressionWindow = TimeSpan.Zero;
        Assert.NotNull(decoder.Decode(image.View));

        Assert.Throws<ArgumentOutOfRangeException>(() => decoder.DuplicateSuppressionWindow = TimeSpan.FromTicks(-1));
    }

    [Fact]
    public void DuplicateSuppressionIsKeyedOnValueAndSymbologyTogether()
    {
        var filter = new DuplicateFilter(TimeSpan.FromSeconds(2));

        Assert.True(filter.ShouldReport("A", BarcodeFormat.QrCode, 0));
        Assert.False(filter.ShouldReport("A", BarcodeFormat.QrCode, 500));

        // A different symbology carrying the same text is a different symbol.
        Assert.True(filter.ShouldReport("A", BarcodeFormat.Code128, 600));

        // A different value reports at once rather than waiting out the window.
        Assert.True(filter.ShouldReport("B", BarcodeFormat.Code128, 700));

        filter.Reset();
        Assert.True(filter.ShouldReport("B", BarcodeFormat.Code128, 750));
    }

    [Fact]
    public void HoldingASymbolInViewKeepsItSuppressed()
    {
        var filter = new DuplicateFilter(TimeSpan.FromSeconds(1));
        Assert.True(filter.ShouldReport("HELD", BarcodeFormat.QrCode, 0));

        // Reported once, then suppressed for as long as it keeps being seen, rather than firing
        // again every time the window elapses.
        for (var t = 500L; t < 10_000; t += 500)
        {
            Assert.False(filter.ShouldReport("HELD", BarcodeFormat.QrCode, t));
        }

        // Once it leaves the view for longer than the window, it reports again.
        Assert.True(filter.ShouldReport("HELD", BarcodeFormat.QrCode, 20_000));
    }

    [Fact]
    public void AZeroWindowReportsEveryFrame()
    {
        var filter = new DuplicateFilter(TimeSpan.Zero);
        Assert.True(filter.ShouldReport("X", BarcodeFormat.QrCode, 0));
        Assert.True(filter.ShouldReport("X", BarcodeFormat.QrCode, 0));
    }

    [Fact]
    public void ResultsCarryTheirGeometryInFrameCoordinates()
    {
        using var qr = Qr("GEOMETRY");
        using var frame = ImageTransforms.Frame(qr, 640, 480);

        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.QrCode,
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        var result = decoder.Decode(frame.View, suppressDuplicates: false);

        Assert.NotNull(result);
        Assert.Equal(640, result.FrameWidth);
        Assert.Equal(480, result.FrameHeight);
        Assert.NotEmpty(result.Corners);

        // The symbol was centred, so its box must be too.
        var box = result.BoundingBox;
        Assert.InRange(box.Center.X, 640 / 2.0 - 40, (640 / 2.0) + 40);
        Assert.InRange(box.Center.Y, 480 / 2.0 - 40, (480 / 2.0) + 40);
        Assert.InRange(box.Width, qr.Width * 0.4, qr.Width);
        Assert.Equal(BarcodeFormat.QrCode, result.Format);
        Assert.Equal("GEOMETRY", result.Text);
        Assert.NotNull(result.Metadata);
        Assert.Equal("M", result.Metadata.ErrorCorrectionLevel);
    }

    [Fact]
    public void PointsFromARegionAreReportedInFullFrameCoordinates()
    {
        using var qr = Qr("REGION");
        using var frame = LuminanceBuffer.Rent(640, 480);
        frame.Pixels.Fill(255);
        ImageTransforms.Paste(qr, frame, 400, 300);

        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.QrCode,
            Region = new ScanRegion(0.5, 0.5, 0.5, 0.5),
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        var result = decoder.Decode(frame.View, suppressDuplicates: false);

        Assert.NotNull(result);
        Assert.True(result.BoundingBox.X >= 380, $"Expected the box near x=400, got {result.BoundingBox}.");
        Assert.True(result.BoundingBox.Y >= 280, $"Expected the box near y=300, got {result.BoundingBox}.");
    }

    [Fact]
    public void ScanRectFromPointsEnclosesThemAll()
    {
        ReadOnlySpan<ScanPoint> points = [new(10, 20), new(30, 5), new(15, 40)];
        var rect = ScanRect.FromPoints(points);

        Assert.Equal(10, rect.X);
        Assert.Equal(5, rect.Y);
        Assert.Equal(20, rect.Width);
        Assert.Equal(35, rect.Height);
        Assert.Equal(30, rect.Right);
        Assert.Equal(40, rect.Bottom);
        Assert.Equal(default, ScanRect.FromPoints([]));
    }

    [Fact]
    public void DiagnosticsDescribeTheFrameThatWasActuallyDecoded()
    {
        using var qr = Qr("DIAG");
        using var frame = ImageTransforms.Frame(qr, 640, 480);

        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.QrCode,
            Region = ScanRegion.CenteredSquare(0.5),
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        decoder.Decode(frame.View, suppressDuplicates: false);
        var diagnostics = decoder.LastFrame;

        Assert.Equal(320, diagnostics.DecodedWidth);
        Assert.Equal(240, diagnostics.DecodedHeight);
        Assert.True(diagnostics.Total > TimeSpan.Zero);
        Assert.True(diagnostics.EquivalentFramesPerSecond > 0);
        Assert.Equal(1, decoder.FramesDecoded);
    }

    [Fact]
    public void ContinuousScanningOfEmptyFramesDoesNotAllocate()
    {
        // This is the property that keeps a WebAssembly scanner free of collection pauses: the
        // common case, a frame the user has not yet aimed, must cost nothing but time.
        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.AllLinear,
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        using var blank = LuminanceBuffer.Rent(320, 240);
        blank.Pixels.Fill(255);

        for (var i = 0; i < 10; i++)
        {
            decoder.Decode(blank.View, suppressDuplicates: false);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
        {
            decoder.Decode(blank.View, suppressDuplicates: false);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"Fifty empty frames allocated {allocated} bytes on the linear path.");
    }

    [Fact]
    public void ContinuousScanningWithEveryFormatStaysWithinAFixedOverhead()
    {
        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        using var blank = LuminanceBuffer.Rent(320, 240);
        blank.Pixels.Fill(255);

        for (var i = 0; i < 10; i++)
        {
            decoder.Decode(blank.View, suppressDuplicates: false);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
        {
            decoder.Decode(blank.View, suppressDuplicates: false);
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / 50;

        // The matrix path still allocates the bit plane wrapper for the frame, but nothing that
        // scales with the work done: detectors, finders and their callbacks are all reused.
        Assert.True(allocated <= 128, $"Each empty frame allocated {allocated} bytes with every format enabled.");
    }

    [Fact]
    public void SeparateDecodersDoNotShareState()
    {
        // The decoder is documented as one per session, so two of them must be independent even
        // when used from different threads at the same time.
        using var image = Qr("PARALLEL");
        var pixels = image.Pixels.ToArray();
        var width = image.Width;
        var height = image.Height;

        var results = new string?[8];
        Parallel.For(0, results.Length, i =>
        {
            using var decoder = new BarcodeDecoder(new ScannerOptions
            {
                Formats = BarcodeFormat.QrCode,
                DuplicateSuppressionWindow = TimeSpan.Zero,
            });

            results[i] = decoder.Decode(pixels, width, height, PixelFormat.Gray8, false)?.Text;
        });

        Assert.All(results, r => Assert.Equal("PARALLEL", r));
    }

    [Fact]
    public void EnablingPdf417CostsNothingWhileTheTableIsAPlaceholder()
    {
        // The shipped symbol character table is generated, not the specification's, so the reader
        // is deliberately not created; the format must simply find nothing rather than misread.
        Assert.False(Decoding.Pdf417.Pdf417SymbolTable.Current.IsSpecificationTable);

        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.Pdf417,
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        using var image = Qr("NOT PDF417");
        Assert.Null(decoder.Decode(image.View, suppressDuplicates: false));
    }

    [Fact]
    public void EveryFormatFlagIsDistinctAndTheGroupsAgree()
    {
        var all = Enum.GetValues<BarcodeFormat>()
            .Where(f => f is not (BarcodeFormat.None or BarcodeFormat.All or BarcodeFormat.AllLinear or BarcodeFormat.AllMatrix))
            .ToArray();

        Assert.Equal(all.Length, all.Distinct().Count());
        Assert.Equal(BarcodeFormat.All, BarcodeFormat.AllLinear | BarcodeFormat.AllMatrix);
        Assert.Equal(BarcodeFormat.None, BarcodeFormat.AllLinear & BarcodeFormat.AllMatrix);

        var union = BarcodeFormat.None;
        foreach (var format in all)
        {
            union |= format;
        }

        Assert.Equal(BarcodeFormat.All, union);
    }

    [Fact]
    public void BrowserErrorsAreClassifiedByTheirDomExceptionName()
    {
        var cases = new (string Message, ScannerErrorKind Kind)[]
        {
            ("NotAllowedError: permission dismissed", ScannerErrorKind.PermissionDenied),
            ("SecurityError", ScannerErrorKind.PermissionDenied),
            ("NotFoundError: no device", ScannerErrorKind.NoCamera),
            ("NotReadableError: could not start", ScannerErrorKind.CameraUnavailable),
            ("OverconstrainedError: deviceId", ScannerErrorKind.ConstraintsUnsatisfied),
            ("NotSupportedError", ScannerErrorKind.NotSupported),
            ("something else entirely", ScannerErrorKind.Unknown),
        };

        foreach (var (message, kind) in cases)
        {
            var exception = ScannerException.FromBrowserError(new InvalidOperationException(message));
            Assert.Equal(kind, exception.Kind);
            Assert.NotEmpty(exception.Message);
            Assert.NotNull(exception.InnerException);
        }

        Assert.Throws<ArgumentNullException>(() => ScannerException.FromBrowserError(null!));
    }
}
