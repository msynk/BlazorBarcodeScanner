using BlazorScanner.Decoding;
using BlazorScanner.Decoding.OneD;
using BlazorScanner.Imaging;
using BlazorScanner.TestKit;
using Xunit;

namespace BlazorScanner.Tests;

/// <summary>Round trips every linear symbology through a rendered image.</summary>
public class LinearDecoderTests
{
    private static SymbolDecodeResult? DecodeMiddleRow(
        IRowDecoder decoder, bool[] modules, BarcodeFormat formats, int scale = 3)
    {
        using var image = SyntheticImage.FromLinear(modules, scale);
        var row = new BitRow(image.Width);
        var binarizer = new GlobalHistogramBinarizer();
        var middle = image.Height / 2;

        return binarizer.TryGetBlackRow(image.View, middle, row)
            ? decoder.DecodeRow(middle, row, formats)
            : null;
    }

    [Theory]
    [InlineData("HELLO")]
    [InlineData("Blazor-Scanner_2026")]
    [InlineData("ABC123def!@#")]
    [InlineData("0123456789")]
    public void Code128RoundTrips(string content)
    {
        var result = DecodeMiddleRow(new Code128Reader(), LinearEncoders.Code128(content), BarcodeFormat.Code128);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.Code128, result.Format);
        Assert.Equal(content, result.Payload.Text);
    }

    [Theory]
    [InlineData("HELLO")]
    [InlineData("ABC-123")]
    [InlineData("CODE39 TEST")]
    public void Code39RoundTrips(string content)
    {
        var result = DecodeMiddleRow(new Code39Reader(), LinearEncoders.Code39(content), BarcodeFormat.Code39);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.Code39, result.Format);
        Assert.Equal(content, result.Payload.Text);
    }

    [Theory]
    [InlineData("400638133393")]
    [InlineData("978020137962")]
    [InlineData("590123412345")]
    public void Ean13RoundTrips(string withoutCheck)
    {
        var digits = withoutCheck + LinearEncoders.EanCheckDigit(withoutCheck);
        var result = DecodeMiddleRow(new EanUpcReader(), LinearEncoders.Ean13(digits), BarcodeFormat.Ean13);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.Ean13, result.Format);
        Assert.Equal(digits, result.Payload.Text);
    }

    [Theory]
    [InlineData("9638507")]
    [InlineData("5512345")]
    public void Ean8RoundTrips(string withoutCheck)
    {
        var digits = withoutCheck + LinearEncoders.EanCheckDigit(withoutCheck);
        var result = DecodeMiddleRow(new EanUpcReader(), LinearEncoders.Ean8(digits), BarcodeFormat.Ean8);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.Ean8, result.Format);
        Assert.Equal(digits, result.Payload.Text);
    }

    [Theory]
    [InlineData("03600029145")]
    [InlineData("01234567890")]
    public void UpcARoundTrips(string withoutCheck)
    {
        var digits = withoutCheck + LinearEncoders.EanCheckDigit("0" + withoutCheck);
        var result = DecodeMiddleRow(new EanUpcReader(), LinearEncoders.UpcA(digits), BarcodeFormat.UpcA);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.UpcA, result.Format);
        Assert.Equal(digits, result.Payload.Text);
    }

    [Theory]
    [InlineData("01234565")]
    [InlineData("04252614")]
    public void UpcERoundTrips(string digits)
    {
        // The parity of a UPC-E symbol encodes its check digit, so only self-consistent values
        // can be encoded at all; the expanded UPC-A form is what the decoder verifies.
        var expanded = EanUpcReader.ExpandUpcEToUpcA(digits);
        Assert.NotNull(expanded);
        Assert.Equal(expanded[^1], LinearEncoders.EanCheckDigit(expanded[..^1]));

        var result = DecodeMiddleRow(new EanUpcReader(), LinearEncoders.UpcE(digits), BarcodeFormat.UpcE);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.UpcE, result.Format);
        Assert.Equal(digits, result.Payload.Text);
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("00123456")]
    [InlineData("98765432109876")]
    public void ItfRoundTrips(string digits)
    {
        var result = DecodeMiddleRow(new ItfReader(), LinearEncoders.Itf(digits), BarcodeFormat.Itf, scale: 4);

        Assert.NotNull(result);
        Assert.Equal(BarcodeFormat.Itf, result.Format);
        Assert.Equal(digits, result.Payload.Text);
    }

    [Fact]
    public void Code128ReportsTheScanLinePosition()
    {
        var modules = LinearEncoders.Code128("POSITION");
        var result = DecodeMiddleRow(new Code128Reader(), modules, BarcodeFormat.Code128);

        Assert.NotNull(result);
        Assert.Equal(2, result.Points.Length);
        Assert.True(result.Points[0].X < result.Points[1].X);
        Assert.Equal(result.Points[0].Y, result.Points[1].Y);
    }

    [Fact]
    public void DisabledFormatsAreNotDecoded()
    {
        var modules = LinearEncoders.Code128("SKIPPED");
        Assert.Null(DecodeMiddleRow(new Code128Reader(), modules, BarcodeFormat.Ean13));
    }

    [Fact]
    public void RandomNoiseDoesNotProduceAResult()
    {
        var random = new Random(99);
        var modules = new bool[400];
        for (var i = 0; i < modules.Length; i++)
        {
            modules[i] = random.Next(2) == 0;
        }

        Assert.Null(DecodeMiddleRow(new Code128Reader(), modules, BarcodeFormat.Code128));
        Assert.Null(DecodeMiddleRow(new EanUpcReader(), modules, BarcodeFormat.All));
    }
}
