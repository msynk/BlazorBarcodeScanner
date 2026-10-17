using BlazorBarcodeScanner.Decoding.Common;
using BlazorBarcodeScanner.Imaging;
using Xunit;

namespace BlazorBarcodeScanner.Tests;

/// <summary>Covers the primitives every decoding path is built on.</summary>
public class ImagingTests
{
    [Fact]
    public void BitRowFindsRunsAcrossWordBoundaries()
    {
        var row = new BitRow(100);
        row[31] = true;
        row[32] = true;
        row[64] = true;

        Assert.Equal(31, row.GetNextSet(0));
        Assert.Equal(32, row.GetNextSet(32));
        Assert.Equal(64, row.GetNextSet(33));
        Assert.Equal(100, row.GetNextSet(65));
        Assert.Equal(0, row.GetNextUnset(0));
        Assert.Equal(33, row.GetNextUnset(31));
        Assert.True(row.IsRange(0, 31, false));
        Assert.True(row.IsRange(31, 33, true));
    }

    [Fact]
    public void BitRowReverseIsItsOwnInverse()
    {
        var random = new Random(11);
        foreach (var size in new[] { 1, 31, 32, 33, 100, 640 })
        {
            var row = new BitRow(size);
            var expected = new bool[size];
            for (var i = 0; i < size; i++)
            {
                expected[i] = random.Next(2) == 0;
                row[i] = expected[i];
            }

            row.Reverse();
            for (var i = 0; i < size; i++)
            {
                Assert.Equal(expected[size - 1 - i], row[i]);
            }

            row.Reverse();
            for (var i = 0; i < size; i++)
            {
                Assert.Equal(expected[i], row[i]);
            }
        }
    }

    [Fact]
    public void BitRowInvertFlipsEveryBitAndNothingBeyondTheEnd()
    {
        foreach (var size in new[] { 1, 31, 32, 33, 65 })
        {
            var row = new BitRow(size);
            for (var i = 0; i < size; i += 2)
            {
                row[i] = true;
            }

            row.Invert();

            for (var i = 0; i < size; i++)
            {
                Assert.Equal(i % 2 == 1, row[i]);
            }

            // Bits past the end must stay clear, or GetNextSet would report a phantom run.
            Assert.Equal(size, row.GetNextSet(size));
        }
    }

    [Fact]
    public void BitRowResetGrowsAndClears()
    {
        var row = new BitRow(8);
        row[3] = true;

        row.Reset(64);
        Assert.Equal(64, row.Size);
        Assert.Equal(64, row.GetNextSet(0));
    }

    [Fact]
    public void LuminanceViewCropsWithoutCopying()
    {
        var data = new byte[100];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)i;
        }

        var view = new LuminanceView(data, 10, 10);
        var crop = view.Crop(2, 3, 4, 5);

        Assert.Equal(4, crop.Width);
        Assert.Equal(5, crop.Height);
        Assert.Equal(data[(3 * 10) + 2], crop[0, 0]);
        Assert.Same(view.Buffer, crop.Buffer);
        Assert.Equal(10, crop.Stride);
    }

    [Fact]
    public void LuminanceViewRejectsAWindowOutsideItsBuffer()
    {
        var data = new byte[16];
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuminanceView(data, 0, 4, 4, 5));

        var view = new LuminanceView(data, 4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => view.Crop(2, 2, 4, 4));
    }

    [Theory]
    [InlineData(0.0, 0.0, 1.0, 1.0, true)]
    [InlineData(0.25, 0.25, 0.5, 0.5, false)]
    public void ScanRegionReportsWhetherItCoversTheFrame(double x, double y, double w, double h, bool full)
    {
        Assert.Equal(full, new ScanRegion(x, y, w, h).IsFull);
    }

    [Fact]
    public void ScanRegionNormalizationClampsIntoTheUnitSquare()
    {
        var region = new ScanRegion(-0.5, 0.8, 3, 3).Normalized();

        Assert.Equal(0, region.X);
        Assert.Equal(0.8, region.Y, 6);
        Assert.Equal(1, region.Width, 6);
        Assert.Equal(0.2, region.Height, 6);
    }

    [Fact]
    public void CenteredSquareRejectsAnImpossibleFraction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanRegion.CenteredSquare(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanRegion.CenteredSquare(1.5));

        var region = ScanRegion.CenteredSquare(0.5);
        Assert.Equal(0.25, region.X, 6);
        Assert.Equal(0.5, region.Width, 6);
    }

    [Fact]
    public void CroppingAViewByARegionNeverLeavesTheBuffer()
    {
        var data = new byte[64 * 48];
        var view = new LuminanceView(data, 64, 48);

        foreach (var region in new[]
                 {
                     new ScanRegion(0.9, 0.9, 0.5, 0.5),
                     new ScanRegion(0, 0, 0.001, 0.001),
                     new ScanRegion(0.5, 0.5, 0.5, 0.5),
                 })
        {
            var crop = view.Crop(region);
            Assert.True(crop.Width >= 1 && crop.Height >= 1);
            Assert.True(crop.Offset + ((crop.Height - 1) * crop.Stride) + crop.Width <= data.Length);
        }
    }

    [Theory]
    [InlineData(PixelFormat.Rgba32, 4)]
    [InlineData(PixelFormat.Bgra32, 4)]
    [InlineData(PixelFormat.Rgb24, 3)]
    [InlineData(PixelFormat.Gray8, 1)]
    public void PixelConverterKnowsEveryLayout(PixelFormat format, int bytesPerPixel)
    {
        Assert.Equal(bytesPerPixel, PixelConverter.BytesPerPixel(format));
    }

    [Fact]
    public void PixelConverterProducesTheSameLuminanceFromEveryColourLayout()
    {
        const int Width = 8;
        const int Height = 4;
        var random = new Random(3);
        var rgba = new byte[Width * Height * 4];
        random.NextBytes(rgba);

        var bgra = new byte[rgba.Length];
        var rgb = new byte[Width * Height * 3];
        for (var i = 0; i < Width * Height; i++)
        {
            bgra[(i * 4) + 0] = rgba[(i * 4) + 2];
            bgra[(i * 4) + 1] = rgba[(i * 4) + 1];
            bgra[(i * 4) + 2] = rgba[(i * 4) + 0];
            rgb[(i * 3) + 0] = rgba[(i * 4) + 0];
            rgb[(i * 3) + 1] = rgba[(i * 4) + 1];
            rgb[(i * 3) + 2] = rgba[(i * 4) + 2];
        }

        var fromRgba = new byte[Width * Height];
        var fromBgra = new byte[Width * Height];
        var fromRgb = new byte[Width * Height];

        PixelConverter.ToGrayscale(rgba, fromRgba, Width, Height, PixelFormat.Rgba32);
        PixelConverter.ToGrayscale(bgra, fromBgra, Width, Height, PixelFormat.Bgra32);
        PixelConverter.ToGrayscale(rgb, fromRgb, Width, Height, PixelFormat.Rgb24);

        Assert.Equal(fromRgba, fromBgra);
        Assert.Equal(fromRgba, fromRgb);
    }

    [Fact]
    public void PixelConverterDownsamplesByTakingEveryNthPixel()
    {
        const int Width = 8;
        const int Height = 4;
        var gray = new byte[Width * Height];
        for (var i = 0; i < gray.Length; i++)
        {
            gray[i] = (byte)i;
        }

        var destination = new byte[(Width / 2) * (Height / 2)];
        var (w, h) = PixelConverter.ToGrayscaleDownsampled(gray, destination, Width, Height, PixelFormat.Gray8, 2);

        Assert.Equal(4, w);
        Assert.Equal(2, h);
        Assert.Equal(gray[0], destination[0]);
        Assert.Equal(gray[2], destination[1]);
        Assert.Equal(gray[Width * 2], destination[4]);
    }

    [Fact]
    public void PixelConverterRejectsBuffersThatAreTooSmall()
    {
        var source = new byte[8];
        var destination = new byte[4];

        Assert.Throws<ArgumentException>(() =>
            PixelConverter.ToGrayscale(source, destination, 8, 8, PixelFormat.Gray8));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PixelConverter.ToGrayscale(source, destination, 0, 1, PixelFormat.Gray8));
    }

    [Fact]
    public void BitMatrixInvertLeavesNoBitsSetBeyondTheWidth()
    {
        foreach (var width in new[] { 1, 31, 32, 33, 65 })
        {
            using var matrix = new BitMatrix(width, 3);
            matrix.Invert();

            var set = 0;
            for (var y = 0; y < matrix.Height; y++)
            {
                for (var x = 0; x < matrix.Width; x++)
                {
                    if (matrix[x, y])
                    {
                        set++;
                    }
                }
            }

            Assert.Equal(width * 3, set);

            // A stray bit past the width would make the row words disagree with the matrix.
            var words = matrix.GetRowWords(0);
            var trailing = width & 31;
            if (trailing != 0)
            {
                Assert.Equal(0u, words[^1] & ~((1u << trailing) - 1));
            }
        }
    }

    [Fact]
    public void BitMatrixRotationsArePureRelabelings()
    {
        var random = new Random(5);
        using var source = new BitMatrix(9, 6);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                source[x, y] = random.Next(2) == 0;
            }
        }

        using var rotated = source.Rotate180();
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                Assert.Equal(source[x, y], rotated[source.Width - 1 - x, source.Height - 1 - y]);
            }
        }

        using var quarter = source.Rotate90();
        Assert.Equal(source.Height, quarter.Width);
        Assert.Equal(source.Width, quarter.Height);
    }

    [Fact]
    public void DarkRegionFinderSeparatesPatchesAndIgnoresSpeckle()
    {
        using var image = new BitMatrix(200, 120);
        image.SetRegion(10, 10, 40, 40);
        image.SetRegion(140, 60, 40, 40);

        // Isolated pixels are sensor noise, not content, and must not join the two patches.
        var random = new Random(17);
        for (var i = 0; i < 300; i++)
        {
            image[random.Next(image.Width), random.Next(image.Height)] = true;
        }

        var regions = new DarkRegionFinder().Find(image, minCells: 4);

        Assert.Equal(2, regions.Count);
        Assert.Contains(regions, r => r.Left <= 10 && r.Right >= 49);
        Assert.Contains(regions, r => r.Left <= 140 && r.Right >= 179);
    }

    [Fact]
    public void DarkRegionFinderReportsNothingForABlankImage()
    {
        using var image = new BitMatrix(100, 100);
        Assert.Empty(new DarkRegionFinder().Find(image, minCells: 4));
    }

    [Fact]
    public void GlobalHistogramBinarizerRejectsAFlatRow()
    {
        using var buffer = LuminanceBuffer.Rent(64, 4);
        buffer.Pixels.Fill(200);

        var row = new BitRow(64);
        Assert.False(new GlobalHistogramBinarizer().TryGetBlackRow(buffer.View, 0, row));
        Assert.False(new AdaptiveRowBinarizer().TryGetBlackRow(buffer.View, 0, row));
    }

    [Fact]
    public void AdaptiveRowBinarizerFollowsALightingGradient()
    {
        // A bar pattern under a strong gradient: the dark end of the paper is darker than the
        // light end's ink, so no single threshold can separate them.
        const int Width = 256;
        using var buffer = LuminanceBuffer.Rent(Width, 1);
        for (var x = 0; x < Width; x++)
        {
            var bar = (x / 8) % 2 == 0;
            var brightness = 0.25 + (0.75 * x / (Width - 1));
            buffer.Pixels[x] = (byte)((bar ? 20 : 240) * brightness);
        }

        var row = new BitRow(Width);
        Assert.True(new AdaptiveRowBinarizer().TryGetBlackRow(buffer.View, 0, row));

        var correct = 0;
        for (var x = 4; x < Width - 4; x++)
        {
            if (row[x] == ((x / 8) % 2 == 0))
            {
                correct++;
            }
        }

        Assert.True(correct > (Width - 8) * 0.95, $"Only {correct} of {Width - 8} pixels were classified correctly.");
    }

    [Fact]
    public void LuminanceBufferReturnsItsPooledArrayOnce()
    {
        var buffer = LuminanceBuffer.Rent(32, 32);
        buffer.Dispose();
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.Array);
    }

    [Fact]
    public void WrappedBuffersAreNotPooled()
    {
        var data = new byte[64];
        var buffer = LuminanceBuffer.Wrap(data, 8, 8);

        Assert.Same(data, buffer.Array);
        buffer.Dispose();

        // Disposing a wrapped buffer must not return a caller owned array to the pool.
        Assert.Throws<ObjectDisposedException>(() => buffer.Array);
    }

    [Fact]
    public void WrapRejectsAnUndersizedBuffer()
    {
        Assert.Throws<ArgumentException>(() => LuminanceBuffer.Wrap(new byte[10], 8, 8));
    }
}
