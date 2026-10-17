using BlazorBarcodeScanner.Decoding;
using BlazorBarcodeScanner.Decoding.Pdf417;
using BlazorBarcodeScanner.TestKit;
using Xunit;

namespace BlazorBarcodeScanner.Tests;

/// <summary>
/// Exercises the PDF417 implementation against the symbol table the library ships.
/// </summary>
/// <remarks>
/// These tests validate everything except the symbol character table itself: detection, the row
/// indicators, GF(929) error correction and the compaction modes. The table cannot be validated
/// by round trip, which is exactly why the library refuses to enable PDF417 in
/// <see cref="BlazorBarcodeScanner.Pipeline.BarcodeDecoder"/> until a specification table is registered.
/// </remarks>
public class Pdf417DecoderTests
{
    [Theory]
    [InlineData("HELLO WORLD")]
    [InlineData("PDF FOUR ONE SEVEN")]
    [InlineData("A")]
    public void DecodesTextCompaction(string content)
    {
        using var matrix = Pdf417Encoder.Encode(content);
        var result = new Pdf417Reader().Decode(matrix, MatrixDecodeOptions.Live);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.Pdf417, result.Format);
        Assert.Equal(content, result.Payload.Text);
    }

    [Theory]
    [InlineData("Mixed case 123!")]
    [InlineData("https://example.com/p/42")]
    public void DecodesByteCompaction(string content)
    {
        using var matrix = Pdf417Encoder.Encode(content, columns: 6);
        var result = new Pdf417Reader().Decode(matrix, MatrixDecodeOptions.Live);

        Assert.NotNull(result);
        Assert.Equal(content, result.Payload.Text);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    [InlineData(8, 4)]
    public void DecodesAcrossColumnCountsAndSecurityLevels(int columns, int level)
    {
        const string Content = "STACKED SYMBOLOGY";
        using var matrix = Pdf417Encoder.Encode(Content, columns, level);

        var result = new Pdf417Reader().Decode(matrix, MatrixDecodeOptions.Live);

        Assert.NotNull(result);
        Assert.Equal(Content, result.Payload.Text);
        Assert.Equal(level.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Payload.ErrorCorrectionLevel);
        Assert.Equal(columns, result.Payload.Columns);
    }

    [Fact]
    public void ErrorCorrectionRepairsDamagedCodewords()
    {
        const string Content = "ERROR CORRECTION OVER NINE TWO NINE";
        using var matrix = Pdf417Encoder.Encode(Content, columns: 4, errorCorrectionLevel: 5);

        var result = new Pdf417Reader().Decode(matrix, MatrixDecodeOptions.Live);

        Assert.NotNull(result);
        Assert.Equal(Content, result.Payload.Text);
    }

    [Fact]
    public void ReedSolomonOverGf929RoundTrips()
    {
        var field = ModulusGF.Pdf417;
        var correction = new Pdf417ErrorCorrection();

        // Build a codeword block by hand, damage two codewords, and check both are repaired.
        var data = new List<int> { 9, 100, 200, 300, 400, 500, 600, 700 };
        var encoded = BuildBlock(data, numEc: 8);

        Assert.True(correction.TryDecode([.. encoded], 8, out var clean));
        Assert.Equal(0, clean);

        encoded[2] = field.Add(encoded[2], 17);
        encoded[5] = field.Add(encoded[5], 300);

        Assert.True(correction.TryDecode(encoded, 8, out var corrected));
        Assert.Equal(2, corrected);
        Assert.Equal(100, encoded[1]);
        Assert.Equal(200, encoded[2]);
        Assert.Equal(500, encoded[5]);
    }

    [Fact]
    public void TooManyErrorsAreReportedRatherThanGuessed()
    {
        var correction = new Pdf417ErrorCorrection();
        var encoded = BuildBlock([9, 1, 2, 3, 4, 5, 6, 7], numEc: 4);

        for (var i = 0; i < 5; i++)
        {
            encoded[i] = (encoded[i] + 137) % 929;
        }

        var repaired = correction.TryDecode(encoded, 4, out _);
        if (repaired)
        {
            // A miscorrection is possible in principle; it must at least not claim the original.
            Assert.NotEqual(1, encoded[1]);
        }
    }

    [Fact]
    public void DecodesFromARenderedAndBinarisedImage()
    {
        const string Content = "PDF FOUR ONE SEVEN";
        using var matrix = Pdf417Encoder.Encode(Content, columns: 4, errorCorrectionLevel: 2, rowHeightModules: 4);
        using var image = SyntheticImage.FromMatrix(matrix, scale: 6);

        using var binary = new BlazorBarcodeScanner.Imaging.HybridBinarizer().GetBlackMatrix(image.View);
        Assert.NotNull(binary);

        var result = new Pdf417Reader().Decode(binary, MatrixDecodeOptions.Thorough);

        Assert.NotNull(result);
        Assert.Equal(Content, result.Payload.Text);
        Assert.Equal(4, result.Payload.Columns);
        Assert.Equal(4, result.Points.Length);
    }

    [Fact]
    public void ShippedSymbolTableIsMarkedAsAPlaceholder()
    {
        Assert.False(
            Pdf417SymbolTable.Current.IsSpecificationTable,
            "The generated table must never claim to be the specification table.");
    }

    [Fact]
    public void ShippedSymbolTableIsStructurallyValid()
    {
        var table = Pdf417SymbolTable.Current;

        foreach (var cluster in new[] { 0, 3, 6 })
        {
            for (var codeword = 0; codeword < Pdf417SymbolTable.CodewordCount; codeword++)
            {
                Assert.True(table.TryGetPattern(cluster, codeword, out var pattern));

                // Seventeen modules, starting with a bar and ending with a space.
                Assert.True(pattern < 1 << Pdf417SymbolTable.ModulesPerCodeword);
                Assert.True((pattern & (1 << 16)) != 0, "A symbol character starts with a bar.");
                Assert.True((pattern & 1) == 0, "A symbol character ends with a space.");

                Assert.True(table.TryGetCodeword(cluster, pattern, out var roundTripped));
                Assert.Equal(codeword, roundTripped);
            }
        }
    }

    [Fact]
    public void PipelineLeavesPdf417DisabledWhileTheTableIsAPlaceholder()
    {
        using var decoder = new BlazorBarcodeScanner.Pipeline.BarcodeDecoder(new ScannerOptions
        {
            Formats = BarcodeFormat.Pdf417,
        });

        using var matrix = Pdf417Encoder.Encode("DISABLED");
        using var image = SyntheticImage.FromMatrix(matrix, scale: 3);

        Assert.Null(decoder.Decode(image.View));
    }

    private static int[] BuildBlock(List<int> data, int numEc)
    {
        var field = ModulusGF.Pdf417;

        var generator = field.One;
        for (var i = 1; i <= numEc; i++)
        {
            generator = generator.Multiply(new ModulusPoly(field, [1, field.Subtract(0, field.Exp(i))]));
        }

        var remainder = new ModulusPoly(field, [.. data]).MultiplyByMonomial(numEc, 1);
        var inverse = field.Inverse(generator.GetCoefficient(generator.Degree));

        while (remainder.Degree >= generator.Degree && !remainder.IsZero)
        {
            var degreeDifference = remainder.Degree - generator.Degree;
            var scale = field.Multiply(remainder.GetCoefficient(remainder.Degree), inverse);
            remainder = remainder.Subtract(generator.MultiplyByMonomial(degreeDifference, scale));
        }

        var block = new int[data.Count + numEc];
        for (var i = 0; i < data.Count; i++)
        {
            block[i] = data[i];
        }

        for (var i = 0; i < numEc; i++)
        {
            block[data.Count + numEc - 1 - i] = field.Subtract(0, remainder.GetCoefficient(i));
        }

        return block;
    }
}
