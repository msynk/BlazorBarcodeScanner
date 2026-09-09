using System.Text;
using BlazorBarcodeScanner.Decoding.DataMatrix;
using BlazorBarcodeScanner.Imaging;
using BlazorBarcodeScanner.TestKit;
using Xunit;

namespace BlazorBarcodeScanner.Tests;

/// <summary>
/// Drives the Data Matrix codeword parser directly, because the encoder in the test kit only
/// emits ASCII: every other encodation mode, and every malformed stream, is only reachable this
/// way.
/// </summary>
public class DataMatrixBitStreamTests
{
    private static readonly DataMatrixVersion Version = DataMatrixVersion.All.First(v => v.VersionNumber == 5);

    private static byte[] Pad(params int[] codewords)
    {
        var data = new byte[Version.TotalDataCodewords];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = i < codewords.Length ? (byte)codewords[i] : (byte)129;
        }

        return data;
    }

    private static string? DecodeText(params int[] codewords) =>
        DataMatrixBitStreamParser.Decode(Pad(codewords), Version, 0)?.Text;

    [Fact]
    public void DecodesAsciiAndDigitPairs()
    {
        // Values 1 to 128 are the character plus one; 130 to 229 are a pair of digits.
        Assert.Equal("AB", DecodeText('A' + 1, 'B' + 1));
        Assert.Equal("42", DecodeText(130 + 42));
        Assert.Equal("A077", DecodeText('A' + 1, 130 + 7, '7' + 1));
    }

    [Fact]
    public void DecodesTheUpperShift()
    {
        // 235 shifts the next value into the upper half of Latin-1.
        var result = DataMatrixBitStreamParser.Decode(Pad(235, 0xE9 - 128 + 1), Version, 0);

        Assert.NotNull(result);
        Assert.Equal("é", result.Text);
    }

    [Fact]
    public void RejectsReservedCodewords()
    {
        // 242 to 253 and 255 are not assigned. A stream containing one is corrupt, and accepting
        // it would turn a Reed-Solomon miscorrection into a plausible looking value.
        foreach (var reserved in new[] { 0, 242, 247, 250, 253, 255 })
        {
            Assert.Null(DecodeText('A' + 1, 'B' + 1, reserved, 'C' + 1));
        }
    }

    [Fact]
    public void RejectsAnUnlatchThatIsNotLast()
    {
        Assert.Null(DecodeText('A' + 1, 254, 'B' + 1));
    }

    [Fact]
    public void DecodesTheStructuredAppendCountAsTheSpecificationDefinesIt()
    {
        // ISO/IEC 16022 stores the total as 17 minus the low nibble, not the low nibble plus two.
        // Symbol 2 of 3: index nibble 1, count nibble 17 - 3 = 14.
        var result = DataMatrixBitStreamParser.Decode(Pad(233, (1 << 4) | 14, 0x12, 0x34, 'A' + 1), Version, 0);

        Assert.NotNull(result);
        Assert.Equal(2, result.StructuredAppendIndex);
        Assert.Equal(3, result.StructuredAppendCount);
        Assert.Equal(0x1234, result.StructuredAppendParity);
        Assert.Equal("A", result.Text);
    }

    [Fact]
    public void DecodesTheOneCodewordEciForm()
    {
        var result = DataMatrixBitStreamParser.Decode(Pad(241, 27, 'A' + 1), Version, 0);

        Assert.NotNull(result);
        Assert.Equal(26, result.Eci);
    }

    [Fact]
    public void DecodesTheTwoCodewordEciForm()
    {
        // c1 between 128 and 191 means two codewords: (c1 - 128) * 254 + c2 - 1 + 127.
        var result = DataMatrixBitStreamParser.Decode(Pad(241, 131, 2, 'A' + 1), Version, 0);

        Assert.NotNull(result);
        Assert.Equal(((131 - 128) * 254) + 2 - 1 + 127, result.Eci);
        Assert.Equal("A", result.Text);
    }

    [Fact]
    public void DecodesTheThreeCodewordEciForm()
    {
        var result = DataMatrixBitStreamParser.Decode(Pad(241, 200, 5, 9, 'A' + 1), Version, 0);

        Assert.NotNull(result);
        Assert.Equal(((200 - 192) * 64516) + ((5 - 1) * 254) + 9 - 1 + 16383, result.Eci);
        Assert.Equal("A", result.Text);
    }

    [Fact]
    public void AppliesTheDeclaredCharacterSetToTheText()
    {
        // ECI 26 is UTF-8. The bytes are two code units of one character; without applying the
        // ECI they would come out as two Latin-1 characters.
        var utf8 = Encoding.UTF8.GetBytes("é");
        Assert.Equal(2, utf8.Length);

        // ECI 26, then a Base 256 run of two bytes. Base 256 values are randomised by their
        // one based position in the data stream, which the ECI codewords occupy first.
        var result = DataMatrixBitStreamParser.Decode(
            Pad(241, 27, 231, Randomise(2, 4), Randomise(utf8[0], 5), Randomise(utf8[1], 6)),
            Version,
            0);

        Assert.NotNull(result);
        Assert.Equal("é", result.Text);
        Assert.Equal(utf8, result.RawBytes);
    }

    [Fact]
    public void FlagsGs1OnlyWhenTheStreamOpensWithFnc1()
    {
        var leading = DataMatrixBitStreamParser.Decode(Pad(232, '0' + 1, '1' + 1), Version, 0);
        Assert.NotNull(leading);
        Assert.True(leading.IsGs1);

        // A separator in the middle of the payload is not a GS1 declaration.
        var middle = DataMatrixBitStreamParser.Decode(Pad('A' + 1, 232, 'B' + 1), Version, 0);
        Assert.NotNull(middle);
        Assert.False(middle.IsGs1);
        Assert.Equal("AB", middle.Text);
    }

    [Fact]
    public void DecodesBase256WithItsRandomisation()
    {
        // Base 256 bytes are randomised by position, and a one byte length prefix covers up to
        // 249 bytes.
        var payload = new byte[] { 0x00, 0x7F, 0xFF, 0x41 };
        var codewords = new List<int> { 231, Randomise(payload.Length, 2) };
        for (var i = 0; i < payload.Length; i++)
        {
            codewords.Add(Randomise(payload[i], 3 + i));
        }

        var result = DataMatrixBitStreamParser.Decode(Pad([.. codewords]), Version, 0);

        Assert.NotNull(result);
        Assert.Equal(payload, result.RawBytes);
    }

    private static int Randomise(int value, int position)
    {
        var pseudoRandom = ((149 * position) % 255) + 1;
        var temp = value + pseudoRandom;
        return temp <= 255 ? temp : temp - 256;
    }

    [Fact]
    public void DecodesC40Text()
    {
        // C40 packs three values into two codewords: 1600 * c1 + 40 * c2 + c3 + 1.
        // Basic set values 14 to 39 are 'A' to 'Z'.
        var value = (1600 * ('A' - 'A' + 14)) + (40 * ('B' - 'A' + 14)) + ('C' - 'A' + 14) + 1;
        var result = DataMatrixBitStreamParser.Decode(
            Pad(230, value >> 8, value & 0xFF, 254, 'D' + 1), Version, 0);

        Assert.NotNull(result);
        Assert.Equal("ABCD", result.Text);
    }

    [Fact]
    public void NeverThrowsOnArbitraryCodewordStreams()
    {
        // Reed-Solomon accepts about one random block in a million, so the parser is reached
        // with garbage in normal operation and has to fail by returning null.
        var random = new Random(31337);
        var accepted = 0;

        foreach (var version in DataMatrixVersion.All)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var data = new byte[version.TotalDataCodewords];
                random.NextBytes(data);
                if (DataMatrixBitStreamParser.Decode(data, version, 0) is not null)
                {
                    accepted++;
                }
            }
        }

        // Some random streams are legal ASCII, which is fine; the contract is only that nothing
        // throws and that the parser is willing to say no.
        Assert.True(accepted < 200 * DataMatrixVersion.All.Count, "Every random stream was accepted.");
    }

    [Fact]
    public void RejectsAGridWhoseFinderPatternIsMissing()
    {
        // The solid L and the dashed timing pattern are the cheapest possible check that a
        // sampled grid is really a symbol, and they cost two lines of modules to verify.
        using var real = DataMatrixEncoder.Encode("BORDERS");
        Assert.True(DataMatrixReader.HasPlausibleBorders(real));

        using var damaged = DataMatrixEncoder.Encode("BORDERS");
        for (var y = 0; y < damaged.Height; y++)
        {
            damaged[0, y] = (y & 1) == 0;
        }

        Assert.False(DataMatrixReader.HasPlausibleBorders(damaged));

        var random = new Random(99);
        using var noise = new BitMatrix(real.Width, real.Height);
        for (var y = 0; y < noise.Height; y++)
        {
            for (var x = 0; x < noise.Width; x++)
            {
                noise[x, y] = random.Next(2) == 0;
            }
        }

        Assert.False(DataMatrixReader.HasPlausibleBorders(noise));
    }
}
