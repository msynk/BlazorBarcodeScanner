using BlazorBarcodeScanner.Decoding;
using BlazorBarcodeScanner.Decoding.DataMatrix;
using BlazorBarcodeScanner.Imaging;
using BlazorBarcodeScanner.TestKit;
using Xunit;

namespace BlazorBarcodeScanner.Tests;

/// <summary>Round trips generated Data Matrix symbols through the decoding path.</summary>
public class DataMatrixDecoderTests
{
    [Theory]
    [InlineData("A")]
    [InlineData("HELLO")]
    [InlineData("Data Matrix 2026!")]
    [InlineData("1234567890123456")]
    [InlineData("https://example.com/item/42")]
    public void DecodesGrid(string content)
    {
        using var matrix = DataMatrixEncoder.Encode(content);
        var result = new DataMatrixReader().DecodeMatrix(matrix);

        Assert.NotNull(result);
        Assert.Equal(content, result.Text);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(17)]
    public void DecodesEverySymbolShapeBand(int minimumVersion)
    {
        const string Content = "AB";
        using var matrix = DataMatrixEncoder.Encode(Content, minimumVersion);

        var expected = DataMatrixVersion.All.First(v => v.VersionNumber == minimumVersion);
        Assert.Equal(expected.SymbolSizeRows, matrix.Height);

        var result = new DataMatrixReader().DecodeMatrix(matrix);

        Assert.NotNull(result);
        Assert.Equal(Content, result.Text);
        Assert.Equal(expected.ToString(), result.SymbolVersion);
    }

    [Fact]
    public void DecodesFromRenderedImage()
    {
        const string Content = "SCANNER";
        using var matrix = DataMatrixEncoder.Encode(Content);
        using var image = SyntheticImage.FromMatrix(matrix, scale: 6, quietZoneModules: 4);

        using var binary = new HybridBinarizer().GetBlackMatrix(image.View);
        Assert.NotNull(binary);

        var result = new DataMatrixReader().Decode(binary, MatrixDecodeOptions.Thorough);

        Assert.NotNull(result);
        Assert.Equal(Content, result.Payload.Text);
        Assert.Equal(BarcodeFormat.DataMatrix, result.Format);
        Assert.Equal(4, result.Points.Length);
    }

    [Fact]
    public void RecoversFromDamagedModules()
    {
        const string Content = "RESILIENCE";
        using var matrix = DataMatrixEncoder.Encode(Content, minimumVersion: 6);

        // Flip a couple of data modules well inside the symbol.
        matrix[8, 8] = !matrix[8, 8];
        matrix[9, 9] = !matrix[9, 9];

        var result = new DataMatrixReader().DecodeMatrix(matrix);

        Assert.NotNull(result);
        Assert.Equal(Content, result.Text);
        Assert.True(result.ErrorsCorrected > 0);
    }

    [Fact]
    public void SymbolShapeTableIsInternallyConsistent()
    {
        foreach (var version in DataMatrixVersion.All)
        {
            var regionsDown = version.SymbolSizeRows / version.DataRegionSizeRows;
            var regionsAcross = version.SymbolSizeColumns / version.DataRegionSizeColumns;

            // Each data region is surrounded by a one module border on every side.
            Assert.Equal(version.SymbolSizeRows, regionsDown * (version.DataRegionSizeRows + 2));
            Assert.Equal(version.SymbolSizeColumns, regionsAcross * (version.DataRegionSizeColumns + 2));

            // Every module of every data region holds exactly one bit of one codeword.
            var dataModules = regionsDown * regionsAcross * version.DataRegionSizeRows * version.DataRegionSizeColumns;
            var placed = version.TotalCodewords * 8;

            // The 144 by 144 symbol leaves four modules to the fixed corner pattern.
            Assert.True(
                dataModules - placed is 0 or 4,
                $"Version {version.VersionNumber}: {dataModules} modules but {placed} placed bits.");
        }
    }

    [Fact]
    public void ReturnsNullForAnEmptyImage()
    {
        using var blank = new BitMatrix(64, 64);
        Assert.Null(new DataMatrixReader().Decode(blank, MatrixDecodeOptions.Live));
    }
}
