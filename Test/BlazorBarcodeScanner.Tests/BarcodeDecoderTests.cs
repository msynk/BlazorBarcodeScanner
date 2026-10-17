using BlazorBarcodeScanner.Imaging;
using BlazorBarcodeScanner.Pipeline;
using BlazorBarcodeScanner.TestKit;
using Xunit;

namespace BlazorBarcodeScanner.Tests;

/// <summary>Exercises the end to end pipeline the way an application uses it.</summary>
public class BarcodeDecoderTests
{
    private static LuminanceBuffer RenderQr(string content) =>
        SyntheticImage.FromMatrix(QrEncoder.Encode(content), scale: 5);

    [Fact]
    public void DecodesAQrCodeFromAGrayscaleFrame()
    {
        using var decoder = new BarcodeDecoder();
        using var image = RenderQr("PIPELINE");

        var result = decoder.Decode(image.View);

        Assert.NotNull(result);
        Assert.Equal("PIPELINE", result.Text);
        Assert.Equal(BarcodeFormat.QrCode, result.Format);
        Assert.Equal(image.Width, result.FrameWidth);
        Assert.True(result.BoundingBox.Width > 0);
    }

    [Fact]
    public void DecodesALinearCodeFromAGrayscaleFrame()
    {
        using var decoder = new BarcodeDecoder();
        using var image = SyntheticImage.FromLinear(LinearEncoders.Code128("PIPELINE-128"), scale: 3, height: 120);

        var result = decoder.Decode(image.View);

        Assert.NotNull(result);
        Assert.Equal("PIPELINE-128", result.Text);
        Assert.Equal(BarcodeFormat.Code128, result.Format);
    }

    [Fact]
    public void DecodesFromPackedColourPixels()
    {
        using var image = RenderQr("RGBA FRAME");
        var rgba = new byte[image.Width * image.Height * 4];
        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i++)
        {
            rgba[(i * 4) + 0] = pixels[i];
            rgba[(i * 4) + 1] = pixels[i];
            rgba[(i * 4) + 2] = pixels[i];
            rgba[(i * 4) + 3] = 255;
        }

        using var decoder = new BarcodeDecoder();
        var result = decoder.Decode(rgba, image.Width, image.Height, PixelFormat.Rgba32);

        Assert.NotNull(result);
        Assert.Equal("RGBA FRAME", result.Text);
    }

    [Fact]
    public void SuppressesDuplicatesWithinTheWindow()
    {
        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            DuplicateSuppressionWindow = TimeSpan.FromMinutes(5),
        });

        using var image = RenderQr("DUPLICATE");

        Assert.NotNull(decoder.Decode(image.View));
        Assert.Null(decoder.Decode(image.View));

        decoder.ResetDuplicateFilter();
        Assert.NotNull(decoder.Decode(image.View));
    }

    [Fact]
    public void DoesNotSuppressWhenTheWindowIsZero()
    {
        using var decoder = new BarcodeDecoder(new ScannerOptions { DuplicateSuppressionWindow = TimeSpan.Zero });
        using var image = RenderQr("REPEATED");

        Assert.NotNull(decoder.Decode(image.View));
        Assert.NotNull(decoder.Decode(image.View));
    }

    [Fact]
    public void RestrictingFormatsSkipsOtherSymbologies()
    {
        using var decoder = new BarcodeDecoder(new ScannerOptions { Formats = BarcodeFormat.Ean13 });
        using var image = RenderQr("IGNORED");

        Assert.Null(decoder.Decode(image.View));
    }

    [Fact]
    public void ScanRegionRestrictsTheSearchAndPointsStayInFrameCoordinates()
    {
        using var qr = RenderQr("REGION");

        // Compose a frame twice as wide, with the symbol in the right half only.
        var width = qr.Width * 2;
        var height = qr.Height;
        using var frame = LuminanceBuffer.Rent(width, height);
        frame.Pixels.Fill(255);
        for (var y = 0; y < height; y++)
        {
            qr.Pixels.Slice(y * qr.Width, qr.Width).CopyTo(frame.Pixels.Slice((y * width) + qr.Width, qr.Width));
        }

        using var leftOnly = new BarcodeDecoder(new ScannerOptions { Region = new ScanRegion(0, 0, 0.5, 1) });
        Assert.Null(leftOnly.Decode(frame.View));

        using var rightOnly = new BarcodeDecoder(new ScannerOptions { Region = new ScanRegion(0.5, 0, 0.5, 1) });
        var result = rightOnly.Decode(frame.View);

        Assert.NotNull(result);
        Assert.Equal("REGION", result.Text);
        Assert.True(result.BoundingBox.X >= qr.Width, "Points must be reported in full frame coordinates.");
    }

    [Fact]
    public void DownsamplingStillDecodesAndReportsFullFrameCoordinates()
    {
        using var image = SyntheticImage.FromMatrix(QrEncoder.Encode("DOWNSAMPLED"), scale: 8);
        var gray = image.Pixels.ToArray();

        using var decoder = new BarcodeDecoder(new ScannerOptions { DownsampleFactor = 2 });
        var result = decoder.Decode(gray, image.Width, image.Height, PixelFormat.Gray8);

        Assert.NotNull(result);
        Assert.Equal("DOWNSAMPLED", result.Text);
        Assert.True(result.BoundingBox.Width > image.Width / 4);
    }

    [Fact]
    public void ReportsDiagnosticsForEveryFrame()
    {
        using var decoder = new BarcodeDecoder();
        using var image = RenderQr("DIAGNOSTICS");

        decoder.Decode(image.View);

        var diagnostics = decoder.LastFrame;
        Assert.True(diagnostics.Decoded);
        Assert.Equal(image.Width, diagnostics.DecodedWidth);
        Assert.True(diagnostics.Total > TimeSpan.Zero);
        Assert.Equal(1, decoder.FramesDecoded);
        Assert.Equal(1, decoder.FramesWithSymbol);
    }

    [Fact]
    public void RepeatedFramesDoNotAllocateAfterWarmUp()
    {
        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.QrCode,
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        // A blank frame is the common case during continuous scanning: nothing is in view.
        using var blank = LuminanceBuffer.Rent(320, 240);
        blank.Pixels.Fill(255);

        for (var i = 0; i < 5; i++)
        {
            decoder.Decode(blank.View);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            decoder.Decode(blank.View);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // A small, fixed overhead is acceptable; growth proportional to frames is not.
        Assert.True(allocated < 20 * 1024, $"Allocated {allocated} bytes over 20 empty frames.");
    }
}
