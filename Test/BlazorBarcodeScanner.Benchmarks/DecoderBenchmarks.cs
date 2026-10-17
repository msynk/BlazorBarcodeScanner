using BenchmarkDotNet.Attributes;
using BlazorBarcodeScanner.Imaging;
using BlazorBarcodeScanner.Pipeline;
using BlazorBarcodeScanner.TestKit;

namespace BlazorBarcodeScanner.Benchmarks;

/// <summary>
/// End to end pipeline cost per frame, which is the number that decides whether a scanner keeps
/// up with the camera.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class DecoderBenchmarks
{
    private readonly Dictionary<string, FrameFactory.Frame> _frames = [];
    private BarcodeDecoder _allFormats = null!;
    private BarcodeDecoder _qrOnly = null!;
    private BarcodeDecoder _linearOnly = null!;

    /// <summary>The frame case to decode.</summary>
    [Params("QrSmall", "QrLarge", "QrNoisy", "DataMatrix", "Code128", "Ean13", "Empty")]
    public string Case { get; set; } = "QrSmall";

    [GlobalSetup]
    public void Setup()
    {
        _frames["QrSmall"] = FrameFactory.QrCode("https://example.com/p/12345", scale: 3);
        _frames["QrLarge"] = FrameFactory.QrCode(new string('A', 300), scale: 3);
        _frames["QrNoisy"] = FrameFactory.QrCode("https://example.com/p/12345", scale: 4, noise: 35);
        _frames["DataMatrix"] = FrameFactory.DataMatrix("BLAZORBARCODESCANNER-DM-2026");
        _frames["Code128"] = FrameFactory.Linear(
            "Code 128", BarcodeFormat.Code128, LinearEncoders.Code128("BARCODE-SCANNER-128"), "BARCODE-SCANNER-128");
        _frames["Ean13"] = FrameFactory.Linear(
            "EAN-13", BarcodeFormat.Ean13, LinearEncoders.Ean13("4006381333931"), "4006381333931");
        _frames["Empty"] = FrameFactory.Empty();

        _allFormats = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.All,
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        _qrOnly = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.QrCode,
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        _linearOnly = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.AllLinear,
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _allFormats.Dispose();
        _qrOnly.Dispose();
        _linearOnly.Dispose();
    }

    /// <summary>Every symbology enabled, which is the worst case and the default configuration.</summary>
    [Benchmark(Baseline = true)]
    public BarcodeResult? AllFormats()
    {
        var frame = _frames[Case];
        return _allFormats.Decode(frame.Pixels, frame.Width, frame.Height, PixelFormat.Gray8, suppressDuplicates: false);
    }

    /// <summary>Only QR, the most common single-symbology configuration.</summary>
    [Benchmark]
    public BarcodeResult? QrOnly()
    {
        var frame = _frames[Case];
        return _qrOnly.Decode(frame.Pixels, frame.Width, frame.Height, PixelFormat.Gray8, suppressDuplicates: false);
    }

    /// <summary>Only the linear symbologies, which skips binarising the whole frame.</summary>
    [Benchmark]
    public BarcodeResult? LinearOnly()
    {
        var frame = _frames[Case];
        return _linearOnly.Decode(frame.Pixels, frame.Width, frame.Height, PixelFormat.Gray8, suppressDuplicates: false);
    }
}
