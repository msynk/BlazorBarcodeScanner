using BlazorScanner.Decoding;
using BlazorScanner.Decoding.QrCode;
using BlazorScanner.Imaging;
using BlazorScanner.TestKit;
using Xunit;

namespace BlazorScanner.Tests;

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
        const string Content = "BLAZOR SCANNER 2026";
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
}
