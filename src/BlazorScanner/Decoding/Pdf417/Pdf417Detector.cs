using BlazorScanner.Decoding.OneD;
using BlazorScanner.Imaging;

namespace BlazorScanner.Decoding.Pdf417;

/// <summary>
/// Locates a PDF417 symbol and reads its codewords row by row.
/// </summary>
/// <remarks>
/// <para>
/// PDF417 is a stacked linear symbology: every row is a self-contained sequence of seventeen
/// module characters bracketed by a start and a stop pattern. That makes detection a row-wise
/// search for those two patterns rather than a two dimensional finder search, and it makes
/// sampling exact: a codeword is eight consecutive runs, whatever the module width happens to be
/// at that point in the row.
/// </para>
/// <para>
/// The number of rows is not known in advance, so every image row that yields a complete codeword
/// sequence is decoded and consecutive image rows that produce identical sequences are collapsed
/// into one symbol row. That is both simpler and more tolerant of row height variation than
/// trying to derive the row pitch geometrically.
/// </para>
/// </remarks>
public sealed class Pdf417Detector
{
    /// <summary>The start pattern: a wide bar followed by six narrow elements and a three module space.</summary>
    private static readonly int[] StartPattern = [8, 1, 1, 1, 1, 1, 1, 3];

    /// <summary>The stop pattern, which is eighteen modules rather than seventeen.</summary>
    private static readonly int[] StopPattern = [7, 1, 1, 3, 1, 1, 1, 2, 1];

    private const float MaxAvgVariance = 0.42f;
    private const float MaxIndividualVariance = 0.8f;

    /// <summary>One decoded symbol row.</summary>
    /// <param name="Codewords">Every codeword of the row, including both row indicators.</param>
    /// <param name="Clusters">The cluster number each codeword was read from.</param>
    /// <param name="Top">First image row this symbol row was seen on.</param>
    /// <param name="Bottom">Last image row this symbol row was seen on.</param>
    /// <param name="Left">X of the first module of the start pattern.</param>
    /// <param name="Right">X just past the last module of the stop pattern.</param>
    public sealed record SymbolRow(int[] Codewords, int[] Clusters, int Top, int Bottom, int Left, int Right);

    /// <summary>
    /// Scans the image and returns the symbol rows it found, in top to bottom order.
    /// </summary>
    /// <param name="image">The binarised image.</param>
    /// <param name="table">The symbol character table to decode with.</param>
    /// <returns>The rows, or an empty list when no symbol was found.</returns>
    public IReadOnlyList<SymbolRow> Detect(BitMatrix image, Pdf417SymbolTable table)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(table);

        var rows = new List<SymbolRow>();
        int[]? previous = null;

        Span<int> counters = stackalloc int[9];

        for (var y = 0; y < image.Height; y++)
        {
            if (!TryFindPattern(image, y, 0, StartPattern, counters, out var startBegin, out var startEnd))
            {
                previous = null;
                continue;
            }

            if (!TryFindPattern(image, y, startEnd, StopPattern, counters, out var stopBegin, out var stopEnd))
            {
                previous = null;
                continue;
            }

            var codewords = TryReadRow(image, y, startEnd, stopBegin, table, out var clusters);
            if (codewords is null)
            {
                previous = null;
                continue;
            }

            // Consecutive image rows that decode identically belong to the same symbol row.
            if (previous is not null && codewords.AsSpan().SequenceEqual(previous))
            {
                var last = rows[^1];
                rows[^1] = last with { Bottom = y };
                continue;
            }

            rows.Add(new SymbolRow(codewords, clusters!, y, y, startBegin, stopEnd));
            previous = codewords;
        }

        return rows;
    }

    /// <summary>
    /// Reads the codewords between the start and stop patterns of one image row.
    /// </summary>
    private static int[]? TryReadRow(
        BitMatrix image, int y, int from, int to, Pdf417SymbolTable table, out int[]? clusters)
    {
        clusters = null;

        var codewords = new List<int>(16);
        var clusterList = new List<int>(16);

        Span<int> counters = stackalloc int[8];
        Span<int> widths = stackalloc int[8];

        var x = from;
        while (x < to)
        {
            if (!TryRecordRuns(image, y, ref x, counters, to))
            {
                return null;
            }

            if (!TryNormalize(counters, widths))
            {
                return null;
            }

            var cluster = Pdf417SymbolTable.ClusterOf(widths);
            var pattern = Pdf417SymbolTable.ToPattern(widths);
            if (!table.TryGetCodeword(cluster, pattern, out var codeword))
            {
                return null;
            }

            codewords.Add(codeword);
            clusterList.Add(cluster);

            // A row can hold at most 30 columns plus two indicators.
            if (codewords.Count > 34)
            {
                return null;
            }
        }

        // A usable row has a left indicator, at least one data column and a right indicator.
        if (codewords.Count < 3)
        {
            return null;
        }

        clusters = [.. clusterList];
        return [.. codewords];
    }

    /// <summary>Measures eight alternating runs starting at a bar.</summary>
    private static bool TryRecordRuns(BitMatrix image, int y, ref int x, Span<int> counters, int limit)
    {
        counters.Clear();

        if (x >= limit || !image[x, y])
        {
            return false;
        }

        var position = 0;
        var isBlack = true;

        while (x < limit)
        {
            var pixel = image[x, y];
            if (pixel == isBlack)
            {
                counters[position]++;
                x++;
                continue;
            }

            if (++position == 8)
            {
                return true;
            }

            isBlack = !isBlack;
        }

        // The last run may end exactly at the stop pattern.
        return position == 7 && counters[7] > 0;
    }

    /// <summary>
    /// Converts measured pixel widths into module widths that sum to seventeen.
    /// </summary>
    /// <remarks>
    /// Rounding each element independently rarely sums to seventeen, so the remainder is
    /// distributed to the elements whose rounding error was largest. That recovers the correct
    /// pattern from a row whose module width is not an exact number of pixels.
    /// </remarks>
    private static bool TryNormalize(ReadOnlySpan<int> counters, Span<int> widths)
    {
        var total = 0;
        foreach (var counter in counters)
        {
            if (counter <= 0)
            {
                return false;
            }

            total += counter;
        }

        if (total < Pdf417SymbolTable.ModulesPerCodeword)
        {
            return false;
        }

        var moduleWidth = (float)total / Pdf417SymbolTable.ModulesPerCodeword;

        Span<float> errors = stackalloc float[8];
        var sum = 0;
        for (var i = 0; i < 8; i++)
        {
            var exact = counters[i] / moduleWidth;
            var rounded = Math.Clamp((int)MathF.Round(exact), 1, 6);
            widths[i] = rounded;
            errors[i] = exact - rounded;
            sum += rounded;
        }

        // Hand the remaining modules to the elements that were rounded down the most, and take
        // them back from the ones rounded up the most.
        while (sum != Pdf417SymbolTable.ModulesPerCodeword)
        {
            var grow = sum < Pdf417SymbolTable.ModulesPerCodeword;
            var bestIndex = -1;
            var bestError = grow ? float.NegativeInfinity : float.PositiveInfinity;

            for (var i = 0; i < 8; i++)
            {
                if (grow && widths[i] >= 6)
                {
                    continue;
                }

                if (!grow && widths[i] <= 1)
                {
                    continue;
                }

                if (grow ? errors[i] > bestError : errors[i] < bestError)
                {
                    bestError = errors[i];
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            widths[bestIndex] += grow ? 1 : -1;
            errors[bestIndex] += grow ? -1 : 1;
            sum += grow ? 1 : -1;
        }

        return true;
    }

    /// <summary>Finds a fixed pattern in one row of the image, starting at a bar.</summary>
    private static bool TryFindPattern(
        BitMatrix image, int y, int fromX, ReadOnlySpan<int> pattern, Span<int> counters, out int start, out int end)
    {
        start = 0;
        end = 0;

        var patternLength = pattern.Length;
        counters[..patternLength].Clear();

        var width = image.Width;
        var x = fromX;
        while (x < width && !image[x, y])
        {
            x++;
        }

        if (x >= width)
        {
            return false;
        }

        var patternStart = x;
        var counterPosition = 0;
        var isBlack = true;

        for (; x < width; x++)
        {
            if (image[x, y] == isBlack)
            {
                counters[counterPosition]++;
                continue;
            }

            if (counterPosition == patternLength - 1)
            {
                if (LinearPatterns.PatternMatchVariance(counters[..patternLength], pattern, MaxIndividualVariance) < MaxAvgVariance)
                {
                    start = patternStart;
                    end = x;
                    return true;
                }

                patternStart += counters[0] + counters[1];
                counters[2..patternLength].CopyTo(counters[..(patternLength - 2)]);
                counters[patternLength - 2] = 0;
                counters[patternLength - 1] = 0;
                counterPosition--;
            }
            else
            {
                counterPosition++;
            }

            counters[counterPosition] = 1;
            isBlack = !isBlack;
        }

        // The stop pattern ends with a bar, so a symbol flush against the right edge of the image
        // produces no closing transition. Evaluate the window the loop was still accumulating.
        if (counterPosition == patternLength - 1 &&
            LinearPatterns.PatternMatchVariance(counters[..patternLength], pattern, MaxIndividualVariance) < MaxAvgVariance)
        {
            start = patternStart;
            end = width;
            return true;
        }

        return false;
    }
}
