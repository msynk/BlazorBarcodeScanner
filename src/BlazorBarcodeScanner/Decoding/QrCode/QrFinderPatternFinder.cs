using BlazorBarcodeScanner.Decoding.Common;
using BlazorBarcodeScanner.Imaging;

namespace BlazorBarcodeScanner.Decoding.QrCode;

/// <summary>
/// Locates the three finder patterns of a QR symbol in a binarised image.
/// </summary>
/// <remarks>
/// <para>
/// The search looks for the 1:1:3:1:1 dark/light ratio that a horizontal line through a finder
/// pattern always produces, whatever the rotation of the symbol. A horizontal hit is then
/// confirmed vertically and diagonally before it is accepted, which is what keeps the false
/// positive rate low enough to run this on every camera frame.
/// </para>
/// <para>
/// Rows are sampled with a stride rather than exhaustively. The stride starts at roughly one
/// third of the smallest readable finder pattern and drops once a first candidate is confirmed,
/// so a frame containing no symbol costs a fraction of a frame that contains one.
/// </para>
/// </remarks>
public sealed class QrFinderPatternFinder
{
    private const int MinSkip = 3;
    private const int MaxModules = 97;

    private readonly BitMatrix _image;
    private readonly List<QrFinderPattern> _possibleCenters = new(8);
    private readonly int[] _crossCheckStateCount = new int[5];

    private bool _hasSkipped;

    /// <summary>Creates a finder over a binarised image.</summary>
    /// <param name="image">The binarised image.</param>
    public QrFinderPatternFinder(BitMatrix image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
    }

    /// <summary>
    /// Searches for three finder patterns.
    /// </summary>
    /// <param name="tryHarder">When <see langword="true"/> every row is scanned instead of a strided subset.</param>
    /// <param name="patterns">
    /// Receives the patterns ordered bottom-left, top-left, top-right, which is the order the
    /// perspective transform expects.
    /// </param>
    public bool TryFind(bool tryHarder, out QrFinderPattern[] patterns)
    {
        patterns = [];
        _possibleCenters.Clear();
        _hasSkipped = false;

        var maxI = _image.Height;
        var maxJ = _image.Width;

        var iSkip = (3 * maxI) / (4 * MaxModules);
        if (iSkip < MinSkip || tryHarder)
        {
            iSkip = MinSkip;
        }

        Span<int> stateCount = stackalloc int[5];
        var done = false;

        for (var i = iSkip - 1; i < maxI && !done; i += iSkip)
        {
            stateCount.Clear();
            var currentState = 0;

            for (var j = 0; j < maxJ; j++)
            {
                if (_image[j, i])
                {
                    // Black.
                    if ((currentState & 1) == 1)
                    {
                        currentState++;
                    }

                    stateCount[currentState]++;
                }
                else
                {
                    // White.
                    if ((currentState & 1) == 1)
                    {
                        stateCount[currentState]++;
                        continue;
                    }

                    if (currentState != 4)
                    {
                        stateCount[++currentState]++;
                        continue;
                    }

                    if (!FoundPatternCross(stateCount))
                    {
                        ShiftCountsBy2(stateCount);
                        currentState = 3;
                        continue;
                    }

                    if (!HandlePossibleCenter(stateCount, i, j))
                    {
                        ShiftCountsBy2(stateCount);
                        currentState = 3;
                        continue;
                    }

                    // A confirmed centre lets the scan tighten its stride and, the first time,
                    // jump straight down to where the next finder pattern must be.
                    iSkip = 2;
                    if (_hasSkipped)
                    {
                        done = HaveMultiplyConfirmedCenters();
                    }
                    else
                    {
                        var rowSkip = FindRowSkip();
                        if (rowSkip > stateCount[2])
                        {
                            i += rowSkip - stateCount[2] - iSkip;
                            j = maxJ - 1;
                        }
                    }

                    currentState = 0;
                    stateCount.Clear();
                }
            }

            if (FoundPatternCross(stateCount) &&
                HandlePossibleCenter(stateCount, i, maxJ) &&
                _hasSkipped)
            {
                done = HaveMultiplyConfirmedCenters();
            }
        }

        return TrySelectBestPatterns(out patterns);
    }

    /// <summary>Checks the 1:1:3:1:1 ratio that a line through a finder pattern produces.</summary>
    /// <param name="stateCount">Five alternating run lengths, dark first.</param>
    public static bool FoundPatternCross(ReadOnlySpan<int> stateCount)
    {
        var totalModuleSize = 0;
        for (var i = 0; i < 5; i++)
        {
            var count = stateCount[i];
            if (count == 0)
            {
                return false;
            }

            totalModuleSize += count;
        }

        if (totalModuleSize < 7)
        {
            return false;
        }

        var moduleSize = totalModuleSize / 7.0f;
        var maxVariance = moduleSize / 2.0f;

        return Math.Abs(moduleSize - stateCount[0]) < maxVariance
            && Math.Abs(moduleSize - stateCount[1]) < maxVariance
            && Math.Abs((3.0f * moduleSize) - stateCount[2]) < 3 * maxVariance
            && Math.Abs(moduleSize - stateCount[3]) < maxVariance
            && Math.Abs(moduleSize - stateCount[4]) < maxVariance;
    }

    private static float CenterFromEnd(ReadOnlySpan<int> stateCount, int end) =>
        (end - stateCount[4] - stateCount[3]) - (stateCount[2] / 2.0f);

    private static void ShiftCountsBy2(Span<int> stateCount)
    {
        stateCount[0] = stateCount[2];
        stateCount[1] = stateCount[3];
        stateCount[2] = stateCount[4];
        stateCount[3] = 1;
        stateCount[4] = 0;
    }

    private bool HandlePossibleCenter(ReadOnlySpan<int> stateCount, int i, int j)
    {
        var stateCountTotal = 0;
        for (var k = 0; k < 5; k++)
        {
            stateCountTotal += stateCount[k];
        }

        var centerJ = CenterFromEnd(stateCount, j);
        var centerI = CrossCheckVertical(i, (int)centerJ, stateCount[2], stateCountTotal);
        if (float.IsNaN(centerI))
        {
            return false;
        }

        centerJ = CrossCheckHorizontal((int)centerJ, (int)centerI, stateCount[2], stateCountTotal);
        if (float.IsNaN(centerJ) || !CrossCheckDiagonal((int)centerI, (int)centerJ))
        {
            return false;
        }

        var estimatedModuleSize = stateCountTotal / 7.0f;
        for (var index = 0; index < _possibleCenters.Count; index++)
        {
            var center = _possibleCenters[index];
            if (center.AboutEquals(estimatedModuleSize, centerI, centerJ))
            {
                _possibleCenters[index] = center.CombineEstimate(centerI, centerJ, estimatedModuleSize);
                return true;
            }
        }

        _possibleCenters.Add(new QrFinderPattern(centerJ, centerI, estimatedModuleSize));
        return true;
    }

    private float CrossCheckVertical(int startI, int centerJ, int maxCount, int originalStateCountTotal)
    {
        var maxI = _image.Height;
        var stateCount = _crossCheckStateCount;
        Array.Clear(stateCount);

        var i = startI;
        while (i >= 0 && _image[centerJ, i])
        {
            stateCount[2]++;
            i--;
        }

        if (i < 0)
        {
            return float.NaN;
        }

        while (i >= 0 && !_image[centerJ, i] && stateCount[1] <= maxCount)
        {
            stateCount[1]++;
            i--;
        }

        if (i < 0 || stateCount[1] > maxCount)
        {
            return float.NaN;
        }

        while (i >= 0 && _image[centerJ, i] && stateCount[0] <= maxCount)
        {
            stateCount[0]++;
            i--;
        }

        if (stateCount[0] > maxCount)
        {
            return float.NaN;
        }

        i = startI + 1;
        while (i < maxI && _image[centerJ, i])
        {
            stateCount[2]++;
            i++;
        }

        if (i == maxI)
        {
            return float.NaN;
        }

        while (i < maxI && !_image[centerJ, i] && stateCount[3] < maxCount)
        {
            stateCount[3]++;
            i++;
        }

        if (i == maxI || stateCount[3] >= maxCount)
        {
            return float.NaN;
        }

        while (i < maxI && _image[centerJ, i] && stateCount[4] < maxCount)
        {
            stateCount[4]++;
            i++;
        }

        if (stateCount[4] >= maxCount)
        {
            return float.NaN;
        }

        var stateCountTotal = stateCount[0] + stateCount[1] + stateCount[2] + stateCount[3] + stateCount[4];
        if (5 * Math.Abs(stateCountTotal - originalStateCountTotal) >= 2 * originalStateCountTotal)
        {
            return float.NaN;
        }

        return FoundPatternCross(stateCount) ? CenterFromEnd(stateCount, i) : float.NaN;
    }

    private float CrossCheckHorizontal(int startJ, int centerI, int maxCount, int originalStateCountTotal)
    {
        var maxJ = _image.Width;
        var stateCount = _crossCheckStateCount;
        Array.Clear(stateCount);

        var j = startJ;
        while (j >= 0 && _image[j, centerI])
        {
            stateCount[2]++;
            j--;
        }

        if (j < 0)
        {
            return float.NaN;
        }

        while (j >= 0 && !_image[j, centerI] && stateCount[1] <= maxCount)
        {
            stateCount[1]++;
            j--;
        }

        if (j < 0 || stateCount[1] > maxCount)
        {
            return float.NaN;
        }

        while (j >= 0 && _image[j, centerI] && stateCount[0] <= maxCount)
        {
            stateCount[0]++;
            j--;
        }

        if (stateCount[0] > maxCount)
        {
            return float.NaN;
        }

        j = startJ + 1;
        while (j < maxJ && _image[j, centerI])
        {
            stateCount[2]++;
            j++;
        }

        if (j == maxJ)
        {
            return float.NaN;
        }

        while (j < maxJ && !_image[j, centerI] && stateCount[3] < maxCount)
        {
            stateCount[3]++;
            j++;
        }

        if (j == maxJ || stateCount[3] >= maxCount)
        {
            return float.NaN;
        }

        while (j < maxJ && _image[j, centerI] && stateCount[4] < maxCount)
        {
            stateCount[4]++;
            j++;
        }

        if (stateCount[4] >= maxCount)
        {
            return float.NaN;
        }

        var stateCountTotal = stateCount[0] + stateCount[1] + stateCount[2] + stateCount[3] + stateCount[4];
        if (5 * Math.Abs(stateCountTotal - originalStateCountTotal) >= originalStateCountTotal)
        {
            return float.NaN;
        }

        return FoundPatternCross(stateCount) ? CenterFromEnd(stateCount, j) : float.NaN;
    }

    /// <summary>
    /// Confirms a candidate along its main diagonal. This is what rejects the many 1:1:3:1:1
    /// horizontal and vertical coincidences produced by text and packaging artwork.
    /// </summary>
    private bool CrossCheckDiagonal(int centerI, int centerJ)
    {
        Span<int> stateCount = stackalloc int[5];

        var i = 0;
        while (centerI >= i && centerJ >= i && _image[centerJ - i, centerI - i])
        {
            stateCount[2]++;
            i++;
        }

        if (centerI < i || centerJ < i)
        {
            return false;
        }

        while (centerI >= i && centerJ >= i && !_image[centerJ - i, centerI - i] && stateCount[1] <= centerI)
        {
            stateCount[1]++;
            i++;
        }

        if (centerI < i || centerJ < i || stateCount[1] > centerI)
        {
            return false;
        }

        while (centerI >= i && centerJ >= i && _image[centerJ - i, centerI - i])
        {
            stateCount[0]++;
            i++;
        }

        var maxI = _image.Height;
        var maxJ = _image.Width;

        i = 1;
        while (centerI + i < maxI && centerJ + i < maxJ && _image[centerJ + i, centerI + i])
        {
            stateCount[2]++;
            i++;
        }

        if (centerI + i >= maxI || centerJ + i >= maxJ)
        {
            return false;
        }

        while (centerI + i < maxI && centerJ + i < maxJ && !_image[centerJ + i, centerI + i])
        {
            stateCount[3]++;
            i++;
        }

        if (centerI + i >= maxI || centerJ + i >= maxJ)
        {
            return false;
        }

        while (centerI + i < maxI && centerJ + i < maxJ && _image[centerJ + i, centerI + i])
        {
            stateCount[4]++;
            i++;
        }

        return FoundPatternCross(stateCount);
    }

    /// <summary>
    /// Once two centres are known, the third finder pattern must lie a predictable distance away,
    /// so the scan can jump most of the way there instead of walking.
    /// </summary>
    private int FindRowSkip()
    {
        if (_possibleCenters.Count <= 1)
        {
            return 0;
        }

        QrFinderPattern? firstConfirmed = null;
        foreach (var center in _possibleCenters)
        {
            if (center.Count < 2)
            {
                continue;
            }

            if (firstConfirmed is null)
            {
                firstConfirmed = center;
                continue;
            }

            _hasSkipped = true;
            return (int)((Math.Abs(firstConfirmed.X - center.X) - Math.Abs(firstConfirmed.Y - center.Y)) / 2);
        }

        return 0;
    }

    private bool HaveMultiplyConfirmedCenters()
    {
        var confirmedCount = 0;
        var totalModuleSize = 0.0f;
        var max = _possibleCenters.Count;

        foreach (var pattern in _possibleCenters)
        {
            if (pattern.Count < 2)
            {
                continue;
            }

            confirmedCount++;
            totalModuleSize += pattern.EstimatedModuleSize;
        }

        if (confirmedCount < 3)
        {
            return false;
        }

        // Three confirmed centres are only trustworthy when their module sizes agree; a large
        // spread means at least one of them belongs to something that is not this symbol.
        var average = totalModuleSize / max;
        var totalDeviation = 0.0f;
        foreach (var pattern in _possibleCenters)
        {
            totalDeviation += Math.Abs(pattern.EstimatedModuleSize - average);
        }

        return totalDeviation <= 0.05f * totalModuleSize;
    }

    private bool TrySelectBestPatterns(out QrFinderPattern[] patterns)
    {
        patterns = [];
        var count = _possibleCenters.Count;
        if (count < 3)
        {
            return false;
        }

        if (count == 3)
        {
            patterns = [_possibleCenters[0], _possibleCenters[1], _possibleCenters[2]];
            OrderBestPatterns(patterns);
            return true;
        }

        _possibleCenters.Sort(static (a, b) => a.EstimatedModuleSize.CompareTo(b.EstimatedModuleSize));

        var bestDistortion = double.MaxValue;
        QrFinderPattern[]? best = null;

        for (var i = 0; i < count - 2; i++)
        {
            var fpi = _possibleCenters[i];
            var minModuleSize = fpi.EstimatedModuleSize;

            for (var j = i + 1; j < count - 1; j++)
            {
                var fpj = _possibleCenters[j];
                var squares0 = SquaredDistance(fpi, fpj);

                for (var k = j + 1; k < count; k++)
                {
                    var fpk = _possibleCenters[k];
                    if (fpk.EstimatedModuleSize > minModuleSize * 1.4f)
                    {
                        continue;
                    }

                    // The three centres of a QR symbol form an isosceles right triangle, so the
                    // longest side squared should be twice each of the others.
                    Span<double> sides = [squares0, SquaredDistance(fpj, fpk), SquaredDistance(fpi, fpk)];
                    sides.Sort();

                    var distortion = Math.Abs(sides[2] - (2 * sides[1])) + Math.Abs(sides[2] - (2 * sides[0]));
                    if (distortion < bestDistortion)
                    {
                        bestDistortion = distortion;
                        best = [fpi, fpj, fpk];
                    }
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        OrderBestPatterns(best);
        patterns = best;
        return true;
    }

    private static double SquaredDistance(QrFinderPattern a, QrFinderPattern b) =>
        MathUtils.DistanceSquared(a.X, a.Y, b.X, b.Y);

    /// <summary>
    /// Puts three finder patterns into bottom-left, top-left, top-right order.
    /// </summary>
    /// <param name="patterns">The three patterns, reordered in place.</param>
    public static void OrderBestPatterns(QrFinderPattern[] patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        var zeroOne = MathUtils.Distance(patterns[0].X, patterns[0].Y, patterns[1].X, patterns[1].Y);
        var oneTwo = MathUtils.Distance(patterns[1].X, patterns[1].Y, patterns[2].X, patterns[2].Y);
        var zeroTwo = MathUtils.Distance(patterns[0].X, patterns[0].Y, patterns[2].X, patterns[2].Y);

        // The pattern opposite the longest side is the corner one: the top-left.
        QrFinderPattern pointA, pointB, pointC;
        if (oneTwo >= zeroOne && oneTwo >= zeroTwo)
        {
            pointB = patterns[0];
            pointA = patterns[1];
            pointC = patterns[2];
        }
        else if (zeroTwo >= oneTwo && zeroTwo >= zeroOne)
        {
            pointB = patterns[1];
            pointA = patterns[0];
            pointC = patterns[2];
        }
        else
        {
            pointB = patterns[2];
            pointA = patterns[0];
            pointC = patterns[1];
        }

        // The sign of the cross product says whether A and C are the right way round.
        if (CrossProductZ(pointA, pointB, pointC) < 0.0f)
        {
            (pointA, pointC) = (pointC, pointA);
        }

        patterns[0] = pointA;
        patterns[1] = pointB;
        patterns[2] = pointC;
    }

    private static float CrossProductZ(QrFinderPattern a, QrFinderPattern b, QrFinderPattern c) =>
        ((c.X - b.X) * (a.Y - b.Y)) - ((c.Y - b.Y) * (a.X - b.X));
}
