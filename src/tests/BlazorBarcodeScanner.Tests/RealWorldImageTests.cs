using BlazorBarcodeScanner.Decoding.QrCode;
using BlazorBarcodeScanner.Imaging;
using BlazorBarcodeScanner.Pipeline;
using BlazorBarcodeScanner.TestKit;
using Xunit;

namespace BlazorBarcodeScanner.Tests;

/// <summary>
/// Decodes symbols through the transforms a camera actually applies: rotation, perspective,
/// blur, sensor noise, uneven lighting, low contrast, scaling and a cluttered background.
/// </summary>
/// <remarks>
/// A round trip through a clean, axis aligned render proves the tables are right. It does not
/// prove the detector works, because detection is exactly the stage that a real frame breaks.
/// These cases are the ones that failed before the detectors were rebuilt, so they are the
/// regression net for them.
/// </remarks>
public class RealWorldImageTests
{
    private const string QrPayload = "https://example.com/p/12345";
    private const string DataMatrixPayload = "DM-2026-TEST";
    private const string Code128Payload = "AB-1234";
    private const string Ean13Payload = "4006381333931";

    private static LuminanceBuffer Qr(int scale = 4) =>
        SyntheticImage.FromMatrix(QrEncoder.Encode(QrPayload, QrErrorCorrectionLevel.M, 2), scale);

    private static LuminanceBuffer DataMatrix(int scale = 4) =>
        SyntheticImage.FromMatrix(DataMatrixEncoder.Encode(DataMatrixPayload), scale);

    private static LuminanceBuffer Code128(int scale = 3) =>
        SyntheticImage.FromLinear(LinearEncoders.Code128(Code128Payload), scale, 80);

    private static LuminanceBuffer Ean13(int scale = 3) =>
        SyntheticImage.FromLinear(LinearEncoders.Ean13(Ean13Payload), scale, 80);

    private static string? Decode(LuminanceBuffer frame, BarcodeFormat formats, bool stillImage = false)
    {
        var options = stillImage ? ScannerOptions.ForStillImages() : new ScannerOptions();
        options.Formats = formats;
        options.DuplicateSuppressionWindow = TimeSpan.Zero;

        using var decoder = new BarcodeDecoder(options);
        return decoder.Decode(frame.View, suppressDuplicates: false)?.Text;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(180)]
    [InlineData(270)]
    public void QrCodeDecodesAtAnyRotation(double degrees)
    {
        using var symbol = Qr();
        using var rotated = ImageTransforms.Rotate(symbol, degrees);
        using var frame = ImageTransforms.Frame(rotated, 640, 480);

        Assert.Equal(QrPayload, Decode(frame, BarcodeFormat.QrCode));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(180)]
    public void DataMatrixDecodesAtAnyRotation(double degrees)
    {
        using var symbol = DataMatrix();
        using var rotated = ImageTransforms.Rotate(symbol, degrees);
        using var frame = ImageTransforms.Frame(rotated, 640, 480);

        Assert.Equal(DataMatrixPayload, Decode(frame, BarcodeFormat.DataMatrix));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(180)]
    public void LinearSymbolsDecodeAtTheRotationsAHandProduces(double degrees)
    {
        using var symbol = Code128();
        using var rotated = ImageTransforms.Rotate(symbol, degrees);
        using var frame = ImageTransforms.Frame(rotated, 640, 480);

        Assert.Equal(Code128Payload, Decode(frame, BarcodeFormat.Code128));
    }

    [Fact]
    public void LinearSymbolsDecodeWhenPresentedVertically()
    {
        using var symbol = Ean13();
        using var rotated = ImageTransforms.Rotate(symbol, 90);
        using var frame = ImageTransforms.Frame(rotated, 640, 480);

        Assert.Equal(Ean13Payload, Decode(frame, BarcodeFormat.Ean13));
    }

    [Fact]
    public void TurningOffVerticalScanningGivesUpRotatedSymbols()
    {
        using var symbol = Ean13();
        using var rotated = ImageTransforms.Rotate(symbol, 90);
        using var frame = ImageTransforms.Frame(rotated, 640, 480);

        using var decoder = new BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.Ean13,
            TryVerticalLines = false,
            DuplicateSuppressionWindow = TimeSpan.Zero,
        });

        Assert.Null(decoder.Decode(frame.View, suppressDuplicates: false));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(30)]
    public void MatrixSymbolsSurvivePerspective(double degrees)
    {
        using var qr = Qr();
        using var tiltedQr = ImageTransforms.Tilt(qr, degrees);
        using var qrFrame = ImageTransforms.Frame(tiltedQr, 640, 480);
        Assert.Equal(QrPayload, Decode(qrFrame, BarcodeFormat.QrCode));

        using var dm = DataMatrix();
        using var tiltedDm = ImageTransforms.Tilt(dm, degrees);
        using var dmFrame = ImageTransforms.Frame(tiltedDm, 640, 480);
        Assert.Equal(DataMatrixPayload, Decode(dmFrame, BarcodeFormat.DataMatrix));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void SensorNoiseDoesNotPreventDecoding(int amplitude)
    {
        using var qr = Qr();
        using var qrFrame = ImageTransforms.Frame(qr, 640, 480);
        SyntheticImage.AddNoise(qrFrame, amplitude, seed: 7);
        Assert.Equal(QrPayload, Decode(qrFrame, BarcodeFormat.QrCode));

        using var dm = DataMatrix();
        using var dmFrame = ImageTransforms.Frame(dm, 640, 480);
        SyntheticImage.AddNoise(dmFrame, amplitude, seed: 7);
        Assert.Equal(DataMatrixPayload, Decode(dmFrame, BarcodeFormat.DataMatrix));
    }

    [Fact]
    public void NoiseDoesNotShredBlankPaperIntoInk()
    {
        // A blank block thresholded against its own mean comes out half black, and a frame of
        // speckle costs an order of magnitude more time and finds nothing. This is what the
        // binariser's noise floor estimate exists to prevent.
        using var frame = LuminanceBuffer.Rent(640, 480);
        frame.Pixels.Fill(220);
        SyntheticImage.AddNoise(frame, amplitude: 25, seed: 11);

        using var binary = new HybridBinarizer().GetBlackMatrix(frame.View);
        Assert.NotNull(binary);

        var ink = 0;
        for (var y = 0; y < binary.Height; y++)
        {
            for (var x = 0; x < binary.Width; x++)
            {
                if (binary[x, y])
                {
                    ink++;
                }
            }
        }

        var fraction = (double)ink / (binary.Width * binary.Height);
        Assert.True(fraction < 0.05, $"Blank noisy paper binarised to {fraction:P1} ink.");
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(1.0)]
    [InlineData(1.4)]
    public void BlurDoesNotPreventDecoding(double sigma)
    {
        using var qr = Qr();
        using var blurredQr = ImageTransforms.Blur(qr, sigma);
        using var qrFrame = ImageTransforms.Frame(blurredQr, 640, 480);
        Assert.Equal(QrPayload, Decode(qrFrame, BarcodeFormat.QrCode));

        using var dm = DataMatrix();
        using var blurredDm = ImageTransforms.Blur(dm, sigma);
        using var dmFrame = ImageTransforms.Frame(blurredDm, 640, 480);
        Assert.Equal(DataMatrixPayload, Decode(dmFrame, BarcodeFormat.DataMatrix));
    }

    [Fact]
    public void UnevenLightingDoesNotPreventDecoding()
    {
        foreach (var (left, right) in new[] { (0.35, 1.0), (1.0, 0.3) })
        {
            using var qr = Qr();
            using var frame = ImageTransforms.Frame(qr, 640, 480);
            using var lit = ImageTransforms.LightingGradient(frame, left, right);
            Assert.Equal(QrPayload, Decode(lit, BarcodeFormat.QrCode));

            using var dm = DataMatrix();
            using var dmFrame = ImageTransforms.Frame(dm, 640, 480);
            using var dmLit = ImageTransforms.LightingGradient(dmFrame, left, right);
            Assert.Equal(DataMatrixPayload, Decode(dmLit, BarcodeFormat.DataMatrix));

            using var ean = Ean13();
            using var eanFrame = ImageTransforms.Frame(ean, 640, 480);
            using var eanLit = ImageTransforms.LightingGradient(eanFrame, left, right);
            Assert.Equal(Ean13Payload, Decode(eanLit, BarcodeFormat.Ean13));
        }
    }

    [Theory]
    [InlineData((byte)90, (byte)160)]
    [InlineData((byte)10, (byte)70)]
    public void LowContrastAndUnderexposureStillDecode(byte dark, byte light)
    {
        using var qr = Qr();
        using var faded = ImageTransforms.Contrast(qr, dark, light);
        using var frame = ImageTransforms.Frame(faded, 640, 480, light);

        Assert.Equal(QrPayload, Decode(frame, BarcodeFormat.QrCode));
    }

    [Fact]
    public void InvertedSymbolsDecodeWithTheStillImageSettings()
    {
        using var qr = Qr();
        using var inverted = ImageTransforms.Invert(qr);
        using var frame = ImageTransforms.Frame(inverted, 640, 480, background: 0);
        Assert.Equal(QrPayload, Decode(frame, BarcodeFormat.QrCode, stillImage: true));

        using var dm = DataMatrix();
        using var invertedDm = ImageTransforms.Invert(dm);
        using var dmFrame = ImageTransforms.Frame(invertedDm, 640, 480, background: 0);
        Assert.Equal(DataMatrixPayload, Decode(dmFrame, BarcodeFormat.DataMatrix, stillImage: true));

        using var c128 = Code128();
        using var invertedLinear = ImageTransforms.Invert(c128);
        using var linearFrame = ImageTransforms.Frame(invertedLinear, 640, 480, background: 0);
        Assert.Equal(Code128Payload, Decode(linearFrame, BarcodeFormat.Code128, stillImage: true));
    }

    [Fact]
    public void LiveSettingsDoNotSpendTimeOnInvertedSymbols()
    {
        // Inverted retries double the cost of every frame that finds nothing, which is most of
        // them, so they are deliberately off for live scanning.
        using var qr = Qr();
        using var inverted = ImageTransforms.Invert(qr);
        using var frame = ImageTransforms.Frame(inverted, 640, 480, background: 0);

        Assert.Null(Decode(frame, BarcodeFormat.QrCode));
    }

    [Theory]
    [InlineData(0.4)]
    [InlineData(0.6)]
    [InlineData(1.5)]
    public void SymbolsDecodeAtAnyDistance(double factor)
    {
        using var qr = Qr(6);
        using var scaled = ImageTransforms.Scale(qr, factor);
        using var frame = ImageTransforms.Frame(scaled, 640, 480);

        Assert.Equal(QrPayload, Decode(frame, BarcodeFormat.QrCode));
    }

    [Fact]
    public void SymbolsAwayFromTheCentreAreStillFound()
    {
        foreach (var (x, y) in new[] { (20, 20), (380, 260), (20, 260), (380, 20) })
        {
            using var qr = Qr(3);
            using var frame = LuminanceBuffer.Rent(640, 480);
            frame.Pixels.Fill(255);
            ImageTransforms.Paste(qr, frame, x, y);
            Assert.Equal(QrPayload, Decode(frame, BarcodeFormat.QrCode));

            using var dm = DataMatrix(4);
            using var dmFrame = LuminanceBuffer.Rent(640, 480);
            dmFrame.Pixels.Fill(255);
            ImageTransforms.Paste(dm, dmFrame, x, y);
            Assert.Equal(DataMatrixPayload, Decode(dmFrame, BarcodeFormat.DataMatrix));
        }
    }

    [Fact]
    public void ABusyBackgroundDoesNotHideTheSymbol()
    {
        using var qr = Qr();
        using var frame = ImageTransforms.Frame(qr, 640, 480);
        ImageTransforms.Clutter(frame, seed: 3, (640 - qr.Width) / 2, (480 - qr.Height) / 2, qr.Width, qr.Height);

        Assert.Equal(QrPayload, Decode(frame, BarcodeFormat.QrCode));
    }

    [Fact]
    public void ACombinationOfEverydayDefectsStillDecodes()
    {
        // Slightly tilted, slightly out of focus, unevenly lit and noisy: an ordinary photograph.
        foreach (var (symbol, format, expected) in new (Func<LuminanceBuffer>, BarcodeFormat, string)[]
                 {
                     (() => Qr(), BarcodeFormat.QrCode, QrPayload),
                     (() => DataMatrix(), BarcodeFormat.DataMatrix, DataMatrixPayload),
                 })
        {
            using var image = symbol();
            using var rotated = ImageTransforms.Rotate(image, 10);
            using var blurred = ImageTransforms.Blur(rotated, 1.0);
            using var framed = ImageTransforms.Frame(blurred, 640, 480);
            using var lit = ImageTransforms.LightingGradient(framed, 0.6, 1.0);
            SyntheticImage.AddNoise(lit, amplitude: 20, seed: 11);

            Assert.Equal(expected, Decode(lit, format));
        }
    }

    [Fact]
    public void ColourFramesDecodeThroughEveryPixelLayout()
    {
        using var qr = Qr();
        using var frame = ImageTransforms.Frame(qr, 640, 480);
        var rgba = ImageTransforms.ToRgba(frame);

        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }

        var rgb = new byte[frame.Width * frame.Height * 3];
        for (var i = 0; i < frame.Width * frame.Height; i++)
        {
            rgb[(i * 3) + 0] = rgba[(i * 4) + 0];
            rgb[(i * 3) + 1] = rgba[(i * 4) + 1];
            rgb[(i * 3) + 2] = rgba[(i * 4) + 2];
        }

        var options = new ScannerOptions
        {
            Formats = BarcodeFormat.QrCode,
            DuplicateSuppressionWindow = TimeSpan.Zero,
        };

        using var decoder = new BarcodeDecoder(options);
        Assert.Equal(QrPayload, decoder.Decode(rgba, frame.Width, frame.Height, PixelFormat.Rgba32, false)?.Text);
        Assert.Equal(QrPayload, decoder.Decode(bgra, frame.Width, frame.Height, PixelFormat.Bgra32, false)?.Text);
        Assert.Equal(QrPayload, decoder.Decode(rgb, frame.Width, frame.Height, PixelFormat.Rgb24, false)?.Text);
        Assert.Equal(
            QrPayload,
            decoder.Decode(frame.Pixels.ToArray(), frame.Width, frame.Height, PixelFormat.Gray8, false)?.Text);
    }

    [Fact]
    public void EveryFrameShapeSurvivesArbitraryContent()
    {
        // The binariser, the region finder and the line sweep all derive their geometry from the
        // frame's dimensions. Extreme shapes are where an off-by-one in that arithmetic shows up,
        // and a camera that hands back an unusual size must not crash the scanner.
        var random = new Random(2026);
        var options = ScannerOptions.ForStillImages();
        options.DuplicateSuppressionWindow = TimeSpan.Zero;

        (int Width, int Height)[] shapes =
        [
            (8, 8), (9, 9), (16, 16), (31, 17), (33, 33), (40, 40), (41, 39),
            (64, 8), (8, 64), (640, 9), (9, 640), (127, 3), (3, 127), (320, 240), (65, 65),
        ];

        foreach (var (width, height) in shapes)
        {
            using var decoder = new BarcodeDecoder(options);
            using var frame = LuminanceBuffer.Rent(width, height);

            for (var attempt = 0; attempt < 12; attempt++)
            {
                switch (attempt % 4)
                {
                    case 0:
                        frame.Pixels.Fill(0);
                        break;
                    case 1:
                        frame.Pixels.Fill(255);
                        break;
                    case 2:
                        random.NextBytes(frame.Array.AsSpan(0, width * height));
                        break;
                    default:
                        for (var i = 0; i < frame.Pixels.Length; i++)
                        {
                            frame.Pixels[i] = (byte)((i % 2) * 255);
                        }

                        break;
                }

                // Anything at all may come back, including null; nothing may throw.
                if (width >= 8 && height >= 8)
                {
                    decoder.Decode(frame.View, suppressDuplicates: false);
                }
            }
        }
    }

    [Fact]
    public void ContinuousScanningOfChangingFrameSizesDoesNotCorruptState()
    {
        // A camera that renegotiates its resolution mid-stream hands the same decoder frames of
        // different shapes, so every cached buffer has to grow and shrink safely.
        var options = new ScannerOptions { DuplicateSuppressionWindow = TimeSpan.Zero };
        using var decoder = new BarcodeDecoder(options);

        foreach (var (width, height) in new[] { (320, 240), (640, 480), (240, 320), (1280, 720), (64, 48), (640, 480) })
        {
            using var qr = Qr(Math.Max(2, Math.Min(width, height) / 40));
            using var frame = LuminanceBuffer.Rent(width, height);
            frame.Pixels.Fill(255);
            ImageTransforms.Paste(qr, frame, Math.Max(0, (width - qr.Width) / 2), Math.Max(0, (height - qr.Height) / 2));

            var result = decoder.Decode(frame.View, suppressDuplicates: false);
            if (qr.Width <= width && qr.Height <= height)
            {
                Assert.Equal(QrPayload, result?.Text);
            }
        }
    }

    [Fact]
    public void RandomFramesNeverProduceAResultAndNeverThrow()
    {
        // Continuous scanning runs the whole pipeline on hundreds of frames of nothing. A single
        // accepted frame is a wrong value; a single exception kills the scanner.
        var random = new Random(5);
        var options = ScannerOptions.ForStillImages();
        options.DuplicateSuppressionWindow = TimeSpan.Zero;

        using var decoder = new BarcodeDecoder(options);
        using var frame = LuminanceBuffer.Rent(320, 240);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            switch (attempt % 4)
            {
                case 0:
                    random.NextBytes(frame.Array.AsSpan(0, 320 * 240));
                    break;
                case 1:
                    for (var i = 0; i < frame.Pixels.Length; i++)
                    {
                        frame.Pixels[i] = (byte)(random.Next(2) == 0 ? 0 : 255);
                    }

                    break;
                case 2:
                    frame.Pixels.Fill(255);
                    ImageTransforms.Clutter(frame, attempt, 0, 0, 0, 0);
                    break;
                default:
                    frame.Pixels.Fill((byte)random.Next(256));
                    break;
            }

            Assert.Null(decoder.Decode(frame.View, suppressDuplicates: false));
        }
    }
}
