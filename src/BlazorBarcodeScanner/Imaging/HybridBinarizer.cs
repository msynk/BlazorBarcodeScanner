namespace BlazorBarcodeScanner.Imaging;

/// <summary>
/// Adaptive, block local binariser used for matrix symbologies.
/// </summary>
/// <remarks>
/// <para>
/// The image is divided into 8 by 8 pixel blocks. Each block gets its own threshold, taken as
/// the mean luminance of the 5 by 5 neighbourhood of blocks around it. This tracks the gradual
/// illumination gradients that dominate phone camera captures, which a single global threshold
/// cannot follow.
/// </para>
/// <para>
/// Blocks whose own dynamic range is small carry no edge, so they are almost certainly all
/// paper or all ink. Those fall back to a value derived from the neighbourhood minimum, which
/// stops flat paper from being shredded into noise.
/// </para>
/// <para>
/// The two scratch arrays are cached on the instance and only regrown when the frame size
/// changes, so continuous scanning does not allocate here.
/// </para>
/// </remarks>
public sealed class HybridBinarizer : IBinarizer
{
    private const int BlockSizePower = 3;
    private const int BlockSize = 1 << BlockSizePower;
    private const int BlockSizeMask = BlockSize - 1;
    private const int MinDynamicRange = 24;

    private readonly GlobalHistogramBinarizer _rowFallback = new();

    private int[] _blockAverages = [];
    private int _blockColumns;
    private int _blockRows;

    /// <inheritdoc />
    public bool TryGetBlackRow(in LuminanceView source, int y, BitRow row) =>
        _rowFallback.TryGetBlackRow(source, y, row);

    /// <inheritdoc />
    public BitMatrix? GetBlackMatrix(in LuminanceView source)
    {
        var width = source.Width;
        var height = source.Height;

        // Very small images have too few blocks for the neighbourhood average to mean anything.
        if (width < BlockSize * 5 || height < BlockSize * 5)
        {
            return _rowFallback.GetBlackMatrix(source);
        }

        var columns = (width + BlockSizeMask) >> BlockSizePower;
        var rows = (height + BlockSizeMask) >> BlockSizePower;

        EnsureCapacity(columns, rows);
        ComputeBlockAverages(source, columns, rows);

        var matrix = new BitMatrix(width, height);
        ApplyThresholds(source, matrix, columns, rows);
        return matrix;
    }

    private void EnsureCapacity(int columns, int rows)
    {
        if (_blockAverages.Length < columns * rows)
        {
            _blockAverages = new int[columns * rows];
        }

        _blockColumns = columns;
        _blockRows = rows;
    }

    private void ComputeBlockAverages(in LuminanceView source, int columns, int rows)
    {
        var width = source.Width;
        var height = source.Height;
        var averages = _blockAverages;

        for (var by = 0; by < rows; by++)
        {
            var yOffset = ClampBlockOrigin(by, rows, height);
            for (var bx = 0; bx < columns; bx++)
            {
                var xOffset = ClampBlockOrigin(bx, columns, width);

                var sum = 0;
                var min = 255;
                var max = 0;
                for (var yy = 0; yy < BlockSize; yy++)
                {
                    var pixels = source.GetRow(yOffset + yy).Slice(xOffset, BlockSize);
                    for (var xx = 0; xx < BlockSize; xx++)
                    {
                        int pixel = pixels[xx];
                        sum += pixel;
                        if (pixel < min) min = pixel;
                        if (pixel > max) max = pixel;
                    }
                }

                var average = sum >> (BlockSizePower * 2);
                if (max - min <= MinDynamicRange)
                {
                    // Uniform block. Assume it is all background and bias the threshold below it,
                    // unless a darker block sits above and to the left, in which case this block is
                    // probably inside a large dark area and should keep some of that context.
                    average = min / 2;
                    if (by > 0 && bx > 0)
                    {
                        var neighbourAverage =
                            (averages[((by - 1) * columns) + bx] +
                             (2 * averages[(by * columns) + bx - 1]) +
                             averages[((by - 1) * columns) + bx - 1]) / 4;
                        if (min < neighbourAverage)
                        {
                            average = neighbourAverage;
                        }
                    }
                }

                averages[(by * columns) + bx] = average;
            }
        }
    }

    private void ApplyThresholds(in LuminanceView source, BitMatrix matrix, int columns, int rows)
    {
        var width = source.Width;
        var height = source.Height;
        var averages = _blockAverages;

        for (var by = 0; by < rows; by++)
        {
            var yOffset = ClampBlockOrigin(by, rows, height);
            var top = Clamp(by, rows - 3);
            for (var bx = 0; bx < columns; bx++)
            {
                var xOffset = ClampBlockOrigin(bx, columns, width);
                var left = Clamp(bx, columns - 3);

                var sum = 0;
                for (var dy = -2; dy <= 2; dy++)
                {
                    var rowStart = (top + dy) * columns;
                    sum += averages[rowStart + left - 2] +
                           averages[rowStart + left - 1] +
                           averages[rowStart + left] +
                           averages[rowStart + left + 1] +
                           averages[rowStart + left + 2];
                }

                var threshold = sum / 25;

                for (var yy = 0; yy < BlockSize; yy++)
                {
                    var y = yOffset + yy;
                    var pixels = source.GetRow(y);
                    var target = matrix.GetRowWords(y);
                    for (var xx = 0; xx < BlockSize; xx++)
                    {
                        var x = xOffset + xx;
                        if (pixels[x] <= threshold)
                        {
                            target[x >> 5] |= 1u << (x & 31);
                        }
                    }
                }
            }
        }
    }

    private static int ClampBlockOrigin(int blockIndex, int blockCount, int size)
    {
        var offset = blockIndex << BlockSizePower;
        var limit = size - BlockSize;
        return offset > limit ? limit : offset;
    }

    private static int Clamp(int value, int max) => value < 2 ? 2 : (value > max ? max : value);
}
