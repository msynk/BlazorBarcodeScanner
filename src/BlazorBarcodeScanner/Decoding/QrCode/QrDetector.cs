using BlazorBarcodeScanner.Decoding.Common;
using BlazorBarcodeScanner.Imaging;

namespace BlazorBarcodeScanner.Decoding.QrCode;

/// <summary>
/// Finds a QR symbol in a binarised image and samples its module grid.
/// </summary>
public sealed class QrDetector
{
    private readonly BitMatrix _image;

    /// <summary>Creates a detector over a binarised image.</summary>
    /// <param name="image">The binarised image.</param>
    public QrDetector(BitMatrix image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
    }

    /// <summary>Detects and samples a symbol.</summary>
    /// <param name="tryHarder">When <see langword="true"/> the finder pattern search scans every row.</param>
    /// <returns>The sampled grid and its corner points, or <see langword="null"/> when no symbol is found.</returns>
    public DetectorResult? Detect(bool tryHarder)
    {
        var finder = new QrFinderPatternFinder(_image);
        if (!finder.TryFind(tryHarder, out var patterns))
        {
            return null;
        }

        var bottomLeft = patterns[0];
        var topLeft = patterns[1];
        var topRight = patterns[2];

        var moduleSize = CalculateModuleSize(topLeft, topRight, bottomLeft);
        if (moduleSize < 1.0f)
        {
            return null;
        }

        if (!TryComputeDimension(topLeft, topRight, bottomLeft, moduleSize, out var dimension))
        {
            return null;
        }

        var provisionalVersion = QrVersion.GetProvisionalVersionForDimension(dimension);
        if (provisionalVersion is null)
        {
            return null;
        }

        QrFinderPattern? alignmentPattern = null;
        if (provisionalVersion.AlignmentPatternCenters.Length > 0)
        {
            var modulesBetweenCenters = provisionalVersion.DimensionForVersion - 7;

            // The fourth corner of the parallelogram formed by the three finder patterns is where
            // the bottom-right of the symbol falls; the alignment pattern sits three modules in.
            var bottomRightX = topRight.X - topLeft.X + bottomLeft.X;
            var bottomRightY = topRight.Y - topLeft.Y + bottomLeft.Y;
            var correction = 1.0f - (3.0f / modulesBetweenCenters);
            var estimatedX = (int)(topLeft.X + (correction * (bottomRightX - topLeft.X)));
            var estimatedY = (int)(topLeft.Y + (correction * (bottomRightY - topLeft.Y)));

            // Widen the search until it succeeds; a tight region is much cheaper when it works.
            for (var allowance = 4; allowance <= 16; allowance <<= 1)
            {
                alignmentPattern = FindAlignmentInRegion(moduleSize, estimatedX, estimatedY, allowance);
                if (alignmentPattern is not null)
                {
                    break;
                }
            }
        }

        var transform = CreateTransform(topLeft, topRight, bottomLeft, alignmentPattern, dimension);
        var bits = GridSampler.Sample(_image, dimension, dimension, transform);
        if (bits is null)
        {
            return null;
        }

        ScanPoint[] points = alignmentPattern is null
            ? [bottomLeft.Point, topLeft.Point, topRight.Point]
            : [bottomLeft.Point, topLeft.Point, topRight.Point, alignmentPattern.Point];

        return new DetectorResult(bits, points);
    }

    private static PerspectiveTransform CreateTransform(
        QrFinderPattern topLeft,
        QrFinderPattern topRight,
        QrFinderPattern bottomLeft,
        QrFinderPattern? alignmentPattern,
        int dimension)
    {
        var dimMinusThree = dimension - 3.5f;

        float bottomRightX, bottomRightY, sourceBottomRightX, sourceBottomRightY;
        if (alignmentPattern is not null)
        {
            bottomRightX = alignmentPattern.X;
            bottomRightY = alignmentPattern.Y;
            sourceBottomRightX = dimMinusThree - 3.0f;
            sourceBottomRightY = sourceBottomRightX;
        }
        else
        {
            bottomRightX = topRight.X - topLeft.X + bottomLeft.X;
            bottomRightY = topRight.Y - topLeft.Y + bottomLeft.Y;
            sourceBottomRightX = dimMinusThree;
            sourceBottomRightY = dimMinusThree;
        }

        return PerspectiveTransform.QuadrilateralToQuadrilateral(
            3.5f, 3.5f,
            dimMinusThree, 3.5f,
            sourceBottomRightX, sourceBottomRightY,
            3.5f, dimMinusThree,
            topLeft.X, topLeft.Y,
            topRight.X, topRight.Y,
            bottomRightX, bottomRightY,
            bottomLeft.X, bottomLeft.Y);
    }

    private static bool TryComputeDimension(
        QrFinderPattern topLeft,
        QrFinderPattern topRight,
        QrFinderPattern bottomLeft,
        float moduleSize,
        out int dimension)
    {
        var tlTr = MathUtils.Round(MathUtils.Distance(topLeft.X, topLeft.Y, topRight.X, topRight.Y) / moduleSize);
        var tlBl = MathUtils.Round(MathUtils.Distance(topLeft.X, topLeft.Y, bottomLeft.X, bottomLeft.Y) / moduleSize);
        dimension = ((tlTr + tlBl) / 2) + 7;

        // Every legal QR dimension is congruent to 1 modulo 4, so a result that is off by one can
        // be snapped; being off by two means the estimate is not trustworthy at all.
        switch (dimension & 0x03)
        {
            case 0:
                dimension++;
                break;
            case 2:
                dimension--;
                break;
            case 3:
                return false;
        }

        return dimension is >= 21 and <= 177;
    }

    private float CalculateModuleSize(QrFinderPattern topLeft, QrFinderPattern topRight, QrFinderPattern bottomLeft) =>
        (CalculateModuleSizeOneWay(topLeft, topRight) + CalculateModuleSizeOneWay(topLeft, bottomLeft)) / 2.0f;

    private float CalculateModuleSizeOneWay(QrFinderPattern pattern, QrFinderPattern otherPattern)
    {
        var estimate1 = SizeOfBlackWhiteBlackRunBothWays(
            (int)pattern.X, (int)pattern.Y, (int)otherPattern.X, (int)otherPattern.Y);
        var estimate2 = SizeOfBlackWhiteBlackRunBothWays(
            (int)otherPattern.X, (int)otherPattern.Y, (int)pattern.X, (int)pattern.Y);

        if (float.IsNaN(estimate1))
        {
            return estimate2 / 7.0f;
        }

        if (float.IsNaN(estimate2))
        {
            return estimate1 / 7.0f;
        }

        // Seven modules span a finder pattern, and each estimate covers one of them twice.
        return (estimate1 + estimate2) / 14.0f;
    }

    private float SizeOfBlackWhiteBlackRunBothWays(int fromX, int fromY, int toX, int toY)
    {
        var result = SizeOfBlackWhiteBlackRun(fromX, fromY, toX, toY);

        // Measure the mirrored run as well, clipping the ray to the image if it leaves it.
        var scale = 1.0f;
        var otherToX = fromX - (toX - fromX);
        if (otherToX < 0)
        {
            scale = fromX / (float)(fromX - otherToX);
            otherToX = 0;
        }
        else if (otherToX >= _image.Width)
        {
            scale = (_image.Width - 1 - fromX) / (float)(otherToX - fromX);
            otherToX = _image.Width - 1;
        }

        var otherToY = (int)(fromY - ((toY - fromY) * scale));

        scale = 1.0f;
        if (otherToY < 0)
        {
            scale = fromY / (float)(fromY - otherToY);
            otherToY = 0;
        }
        else if (otherToY >= _image.Height)
        {
            scale = (_image.Height - 1 - fromY) / (float)(otherToY - fromY);
            otherToY = _image.Height - 1;
        }

        otherToX = (int)(fromX + ((otherToX - fromX) * scale));

        result += SizeOfBlackWhiteBlackRun(fromX, fromY, otherToX, otherToY);

        // The starting pixel was counted by both halves.
        return result - 1.0f;
    }

    /// <summary>
    /// Walks a Bresenham line outwards from a finder pattern centre and returns the distance at
    /// which the black, white, black transition sequence completes.
    /// </summary>
    private float SizeOfBlackWhiteBlackRun(int fromX, int fromY, int toX, int toY)
    {
        var steep = Math.Abs(toY - fromY) > Math.Abs(toX - fromX);
        if (steep)
        {
            (fromX, fromY) = (fromY, fromX);
            (toX, toY) = (toY, toX);
        }

        var dx = Math.Abs(toX - fromX);
        var dy = Math.Abs(toY - fromY);
        var error = -dx / 2;
        var xStep = fromX < toX ? 1 : -1;
        var yStep = fromY < toY ? 1 : -1;

        var state = 0;
        var x = fromX;
        var y = fromY;
        for (; x != toX + xStep; x += xStep)
        {
            var realX = steep ? y : x;
            var realY = steep ? x : y;

            if ((uint)realX >= (uint)_image.Width || (uint)realY >= (uint)_image.Height)
            {
                break;
            }

            if ((state == 1) == _image[realX, realY])
            {
                if (state == 2)
                {
                    return MathUtils.Distance(x, y, fromX, fromY);
                }

                state++;
            }

            error += dy;
            if (error > 0)
            {
                if (y == toY)
                {
                    break;
                }

                y += yStep;
                error -= dx;
            }
        }

        // Running off the end of the image while still inside the final black run is treated as
        // a measurement to the edge rather than a failure.
        return state == 2 ? MathUtils.Distance(toX + xStep, toY, fromX, fromY) : float.NaN;
    }

    private QrFinderPattern? FindAlignmentInRegion(float overallEstModuleSize, int estAlignmentX, int estAlignmentY, int allowanceFactor)
    {
        var allowance = (int)(allowanceFactor * overallEstModuleSize);
        var left = Math.Max(0, estAlignmentX - allowance);
        var right = Math.Min(_image.Width - 1, estAlignmentX + allowance);
        if (right - left < overallEstModuleSize * 3)
        {
            return null;
        }

        var top = Math.Max(0, estAlignmentY - allowance);
        var bottom = Math.Min(_image.Height - 1, estAlignmentY + allowance);
        if (bottom - top < overallEstModuleSize * 3)
        {
            return null;
        }

        return new QrAlignmentPatternFinder(_image, left, top, right - left, bottom - top, overallEstModuleSize).Find();
    }
}
