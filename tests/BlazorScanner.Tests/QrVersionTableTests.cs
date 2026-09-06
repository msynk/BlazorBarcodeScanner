using BlazorScanner.Decoding.QrCode;
using Xunit;

namespace BlazorScanner.Tests;

/// <summary>
/// Validates the transcribed QR block tables against an independent source of truth: the total
/// number of codewords each version holds. Every error correction level of a version has to add
/// up to the same total, and that total has to match the published figure, which makes a
/// transcription slip in any of the 160 table entries impossible to miss.
/// </summary>
public class QrVersionTableTests
{
    /// <summary>Total codewords per version, from ISO/IEC 18004 table 1.</summary>
    private static readonly int[] TotalCodewordsPerVersion =
    [
        26, 44, 70, 100, 134, 172, 196, 242, 292, 346,
        404, 466, 532, 581, 655, 733, 815, 901, 991, 1085,
        1156, 1258, 1364, 1474, 1588, 1706, 1828, 1921, 2051, 2185,
        2323, 2465, 2611, 2761, 2876, 3034, 3196, 3362, 3532, 3706,
    ];

    [Fact]
    public void EveryLevelOfEveryVersionMatchesThePublishedCodewordTotal()
    {
        for (var number = 1; number <= 40; number++)
        {
            var version = QrVersion.GetVersionForNumber(number);
            var expected = TotalCodewordsPerVersion[number - 1];

            Assert.Equal(expected, version.TotalCodewords);

            foreach (var level in new[]
                     {
                         QrErrorCorrectionLevel.L,
                         QrErrorCorrectionLevel.M,
                         QrErrorCorrectionLevel.Q,
                         QrErrorCorrectionLevel.H,
                     })
            {
                var blocks = version.GetEcBlocksForLevel(level);
                Assert.Equal(expected, blocks.TotalDataCodewords + blocks.TotalEcCodewords);
            }
        }
    }

    [Fact]
    public void ErrorCorrectionCapacityIncreasesWithLevel()
    {
        for (var number = 1; number <= 40; number++)
        {
            var version = QrVersion.GetVersionForNumber(number);
            var l = version.GetEcBlocksForLevel(QrErrorCorrectionLevel.L).TotalEcCodewords;
            var m = version.GetEcBlocksForLevel(QrErrorCorrectionLevel.M).TotalEcCodewords;
            var q = version.GetEcBlocksForLevel(QrErrorCorrectionLevel.Q).TotalEcCodewords;
            var h = version.GetEcBlocksForLevel(QrErrorCorrectionLevel.H).TotalEcCodewords;

            Assert.True(l < m, $"Version {number}: L should carry fewer EC codewords than M.");
            Assert.True(m < q, $"Version {number}: M should carry fewer EC codewords than Q.");
            Assert.True(q < h, $"Version {number}: Q should carry fewer EC codewords than H.");
        }
    }

    [Fact]
    public void AlignmentPatternCentersAreOrderedAndInsideTheSymbol()
    {
        for (var number = 1; number <= 40; number++)
        {
            var version = QrVersion.GetVersionForNumber(number);
            var centers = version.AlignmentPatternCenters;
            if (centers.Length == 0)
            {
                Assert.Equal(1, number);
                continue;
            }

            Assert.Equal(6, centers[0]);
            Assert.Equal(version.DimensionForVersion - 7, centers[^1]);

            for (var i = 1; i < centers.Length; i++)
            {
                Assert.True(centers[i] > centers[i - 1], $"Version {number}: centres must ascend.");
            }
        }
    }

    [Theory]
    [InlineData(0, 0x5412)]
    [InlineData(1, 0x5125)]
    [InlineData(8, 0x77C4)]
    [InlineData(16, 0x1689)]
    [InlineData(31, 0x2BED)]
    public void FormatInformationMatchesThePublishedCode(int data, int expected) =>
        Assert.Equal(expected, QrFormatInformation.EncodeFormatBits(data));

    [Theory]
    [InlineData(7, 0x07C94)]
    [InlineData(8, 0x085BC)]
    [InlineData(9, 0x09A99)]
    [InlineData(10, 0x0A4D3)]
    [InlineData(40, 0x28C69)]
    public void VersionInformationMatchesThePublishedCode(int version, int expected) =>
        Assert.Equal(expected, QrFormatInformation.EncodeVersionBits(version));

    [Fact]
    public void FormatInformationRoundTripsThroughDecoding()
    {
        for (var data = 0; data < 32; data++)
        {
            var encoded = QrFormatInformation.EncodeFormatBits(data);
            var decoded = QrFormatInformation.Decode(encoded, encoded);

            Assert.NotNull(decoded);
            Assert.Equal((QrErrorCorrectionLevel)((data >> 3) & 0x03), decoded.ErrorCorrectionLevel);
            Assert.Equal(data & 0x07, decoded.DataMask);
        }
    }

    [Fact]
    public void FormatInformationSurvivesThreeBitErrors()
    {
        var encoded = QrFormatInformation.EncodeFormatBits(0b10_101);
        var damaged = encoded ^ 0b000_0000_0000_0111;

        var decoded = QrFormatInformation.Decode(damaged, damaged);

        Assert.NotNull(decoded);
        Assert.Equal(QrErrorCorrectionLevel.H, decoded.ErrorCorrectionLevel);
        Assert.Equal(5, decoded.DataMask);
    }

    [Fact]
    public void VersionInformationRoundTripsThroughDecoding()
    {
        for (var version = 7; version <= 40; version++)
        {
            var encoded = QrFormatInformation.EncodeVersionBits(version);
            var decoded = QrFormatInformation.DecodeVersionInformation(encoded);

            Assert.NotNull(decoded);
            Assert.Equal(version, decoded.VersionNumber);
        }
    }
}
