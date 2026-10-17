using BlazorBarcodeScanner.Decoding;
using BlazorBarcodeScanner.Decoding.QrCode;
using BlazorBarcodeScanner.Imaging;
using BlazorBarcodeScanner.TestKit;
using Xunit;

namespace BlazorBarcodeScanner.Tests;

/// <summary>
/// Round trips generated symbols through the full decoding path, from module grid to payload
/// and from rendered image to payload.
/// </summary>
public class QrCodeDecoderTests
{
    [Theory]
    [InlineData("HELLO WORLD")]
    [InlineData("12345678901234567890")]
    [InlineData("https://example.com/products/12345?utm_source=scanner")]
    [InlineData("a")]
    [InlineData("Grüße aus München")]
    public void DecodesGridForEveryMaskAndLevel(string content)
    {
        var decoder = new QrDecoder();

        foreach (var level in new[]
                 {
                     QrErrorCorrectionLevel.L,
                     QrErrorCorrectionLevel.M,
                     QrErrorCorrectionLevel.Q,
                     QrErrorCorrectionLevel.H,
                 })
        {
            for (var mask = 0; mask < 8; mask++)
            {
                using var matrix = QrEncoder.Encode(content, level, mask);
                var result = decoder.Decode(matrix);

                Assert.NotNull(result);
                Assert.Equal(content, result.Text);
                Assert.Equal(level.ToLetter(), result.ErrorCorrectionLevel);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(27)]
    [InlineData(40)]
    public void DecodesEveryVersionBand(int minimumVersion)
    {
        var content = new string('A', 12);
        using var matrix = QrEncoder.Encode(content, QrErrorCorrectionLevel.M, maskPattern: 3, minimumVersion);
        Assert.Equal(17 + (4 * minimumVersion), matrix.Width);

        var result = new QrDecoder().Decode(matrix);

        Assert.NotNull(result);
        Assert.Equal(content, result.Text);
        Assert.Equal(minimumVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), result.SymbolVersion);
    }

    [Fact]
    public void DecodesFromRenderedImage()
    {
        const string Content = "BLAZOR BARCODE SCANNER 2026";
        using var matrix = QrEncoder.Encode(Content, QrErrorCorrectionLevel.Q, maskPattern: 2);
        using var image = SyntheticImage.FromMatrix(matrix, scale: 5);

        var binarizer = new HybridBinarizer();
        using var binary = binarizer.GetBlackMatrix(image.View);
        Assert.NotNull(binary);

        var result = new QrCodeReader().Decode(binary, MatrixDecodeOptions.Thorough);

        Assert.NotNull(result);
        Assert.Equal(Content, result.Payload.Text);
        Assert.Equal(BarcodeFormat.QrCode, result.Format);
        Assert.True(result.Points.Length >= 3);
    }

    [Fact]
    public void DecodesFromNoisyImage()
    {
        const string Content = "NOISE TOLERANCE CHECK";
        using var matrix = QrEncoder.Encode(Content, QrErrorCorrectionLevel.H, maskPattern: 5);
        using var image = SyntheticImage.FromMatrix(matrix, scale: 6);
        SyntheticImage.AddNoise(image, amplitude: 40, seed: 1234);

        using var binary = new HybridBinarizer().GetBlackMatrix(image.View);
        Assert.NotNull(binary);

        var result = new QrCodeReader().Decode(binary, MatrixDecodeOptions.Thorough);

        Assert.NotNull(result);
        Assert.Equal(Content, result.Payload.Text);
    }

    [Fact]
    public void RecoversFromDamagedModules()
    {
        const string Content = "ERROR CORRECTION";
        using var matrix = QrEncoder.Encode(Content, QrErrorCorrectionLevel.H, maskPattern: 0);

        // Flip a small block of data modules, well inside the symbol so that no function pattern
        // is touched. Level H should absorb this comfortably.
        for (var y = 12; y < 16; y++)
        {
            for (var x = 12; x < 16; x++)
            {
                matrix[x, y] = !matrix[x, y];
            }
        }

        var result = new QrDecoder().Decode(matrix);

        Assert.NotNull(result);
        Assert.Equal(Content, result.Text);
        Assert.True(result.ErrorsCorrected > 0);
    }

    [Fact]
    public void ReturnsNullForAnEmptyImage()
    {
        using var blank = new BitMatrix(64, 64);
        Assert.Null(new QrCodeReader().Decode(blank, MatrixDecodeOptions.Live));
    }

    /// <summary>
    /// Large symbols whose padding region forms lattices of 1:1:3:1:1 coincidences. Each of
    /// these once stopped the finder pattern search before it reached the real third corner,
    /// so the detector was handed a triple with a false centre and gave up.
    /// </summary>
    [Theory]
    [InlineData(35, QrErrorCorrectionLevel.Q, 6, 0)]
    [InlineData(28, QrErrorCorrectionLevel.L, 5, 180)]
    [InlineData(21, QrErrorCorrectionLevel.M, 5, 90)]
    [InlineData(21, QrErrorCorrectionLevel.M, 5, 270)]
    [InlineData(38, QrErrorCorrectionLevel.H, 0, 180)]
    [InlineData(34, QrErrorCorrectionLevel.M, 2, 270)]
    [InlineData(37, QrErrorCorrectionLevel.H, 7, 90)]
    [InlineData(1, QrErrorCorrectionLevel.Q, 2, 0)]
    public void DecodesSymbolsWhoseDataRegionMimicsFinderPatterns(
        int version, QrErrorCorrectionLevel level, int maskPattern, int rotation)
    {
        var content = $"VERSION {version} LEVEL {level}";
        using var matrix = QrEncoder.Encode(content, level, maskPattern, version);
        using var rotated = Rotate(matrix, rotation);

        foreach (var scale in new[] { 3, 4, 5 })
        {
            using var image = SyntheticImage.FromMatrix(rotated, scale);
            using var binary = new HybridBinarizer().GetBlackMatrix(image.View);
            Assert.NotNull(binary);

            foreach (var options in new[] { MatrixDecodeOptions.Live, MatrixDecodeOptions.Thorough })
            {
                var result = new QrCodeReader().Decode(binary, options);

                Assert.True(result is not null, $"No symbol found at scale {scale} with TryHarder={options.TryHarder}.");
                Assert.Equal(content, result.Payload.Text);
            }
        }
    }

    public static TheoryData<int> EveryVersion
    {
        get
        {
            var data = new TheoryData<int>();
            for (var version = 1; version <= 40; version++)
            {
                data.Add(version);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EveryVersion))]
    public void DecodesEveryVersionInEveryOrientation(int version)
    {
        var content = $"VERSION {version} LEVEL M";
        using var matrix = QrEncoder.Encode(content, QrErrorCorrectionLevel.M, version % 8, version);
        var reader = new QrCodeReader();

        foreach (var rotation in new[] { 0, 90, 180, 270 })
        {
            using var rotated = Rotate(matrix, rotation);
            using var image = SyntheticImage.FromMatrix(rotated, scale: 4);
            using var binary = new HybridBinarizer().GetBlackMatrix(image.View);
            Assert.NotNull(binary);

            var result = reader.Decode(binary, MatrixDecodeOptions.Thorough);

            Assert.True(result is not null, $"Version {version} rotated {rotation} degrees was not found.");
            Assert.Equal(content, result.Payload.Text);
        }
    }

    [Fact]
    public void FinderRanksTheRealCornersFirstAndKeepsAlternatives()
    {
        // The upright version 35 symbol is the case where a lattice coincidence used to be taken
        // as the bottom-left corner. The best triple must now be the real one: an exact isosceles
        // right triangle with its centres 3.5 modules inside the symbol's corners.
        using var matrix = QrEncoder.Encode("VERSION 35 LEVEL Q", QrErrorCorrectionLevel.Q, maskPattern: 6, minimumVersion: 35);
        using var image = SyntheticImage.FromMatrix(matrix, scale: 4);
        using var binary = new HybridBinarizer().GetBlackMatrix(image.View);
        Assert.NotNull(binary);

        var finder = new QrFinderPatternFinder(binary);
        var candidates = finder.FindCandidates(tryHarder: true);

        Assert.True(candidates.Length >= 1);
        Assert.True(candidates.Length <= QrFinderPatternFinder.MaxCandidates);

        var best = candidates[0];
        const float Expected = (4 + 3.5f) * 4;
        const float Far = (4 + 157 - 3.5f) * 4;
        Assert.Equal(Expected, best.TopLeft.X, 1.0f);
        Assert.Equal(Expected, best.TopLeft.Y, 1.0f);
        Assert.Equal(Far, best.TopRight.X, 1.0f);
        Assert.Equal(Expected, best.TopRight.Y, 1.0f);
        Assert.Equal(Expected, best.BottomLeft.X, 1.0f);
        Assert.Equal(Far, best.BottomLeft.Y, 1.0f);

        for (var i = 1; i < candidates.Length; i++)
        {
            Assert.True(candidates[i].Distortion >= candidates[i - 1].Distortion);
        }

        Assert.True(finder.TryFind(tryHarder: true, out var patterns));
        Assert.Equal(3, patterns.Length);
        Assert.Equal(Far, patterns[0].Y, 1.0f);
    }

    private static BitMatrix Rotate(BitMatrix matrix, int degrees)
    {
        switch (degrees)
        {
            case 0:
                return matrix.Crop(0, 0, matrix.Width, matrix.Height);
            case 90:
                return matrix.Rotate90();
            case 180:
                return matrix.Rotate180();
            default:
                using (var half = matrix.Rotate180())
                {
                    return half.Rotate90();
                }
        }
    }
}
