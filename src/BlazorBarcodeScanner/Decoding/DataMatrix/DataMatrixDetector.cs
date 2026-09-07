using BlazorBarcodeScanner.Decoding.Common;
using BlazorBarcodeScanner.Imaging;

namespace BlazorBarcodeScanner.Decoding.DataMatrix;

/// <summary>
/// Finds a Data Matrix symbol in a binarised image and samples its module grid.
/// </summary>
/// <remarks>
/// <para>
/// Data Matrix is bounded on two adjacent sides by a solid "L" and on the other two by a dashed
/// timing pattern. The detector exploits exactly that: after isolating the symbol with a
/// <see cref="WhiteRectangleDetector"/>, it counts colour transitions along each edge of the
/// enclosing quadrilateral. The two edges with the fewest transitions are the solid ones, which
/// fixes the orientation of the symbol without any assumption about how it is rotated.
/// </para>
/// <para>
/// The number of transitions along the dashed edges then gives the module count directly, which
/// is how a symbol of unknown size is measured.
/// </para>
/// </remarks>
public sealed class DataMatrixDetector
{
    private readonly BitMatrix _image;

    /// <summary>Creates a detector over a binarised image.</summary>
    /// <param name="image">The binarised image.</param>
    public DataMatrixDetector(BitMatrix image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _image = image;
    }

    /// <summary>Detects and samples a symbol.</summary>
    /// <returns>The sampled grid and its corner points, or <see langword="null"/> when no symbol is found.</returns>
    public DetectorResult? Detect()
    {
        var rectangleDetector = WhiteRectangleDetector.Create(_image);
        var cornerPoints = rectangleDetector?.Detect();
        if (cornerPoints is null)
        {
            return null;
        }

        var points = DetectSolid1(cornerPoints);
        points = DetectSolid2(points);

        var topRight = CorrectTopRight(points);
        if (topRight is null)
        {
            return null;
        }

        points[3] = topRight.Value;
        points = ShiftToModuleCenter(points);

        var topLeft = points[0];
        var bottomLeft = points[1];
        var bottomRight = points[2];
        topRight = points[3];

        var dimensionTop = TransitionsBetween(topLeft, topRight.Value) + 1;
        var dimensionRight = TransitionsBetween(bottomRight, topRight.Value) + 1;

        // Every Data Matrix dimension is even.
        if ((dimensionTop & 0x01) == 1)
        {
            dimensionTop++;
        }

        if ((dimensionRight & 0x01) == 1)
        {
            dimensionRight++;
        }

        // Aspect ratios between 2:3 and 3:2 can only come from a square symbol measured
        // imprecisely, because no rectangular shape is that close to square.
        if (4 * dimensionTop < 6 * dimensionRight && 4 * dimensionRight < 6 * dimensionTop)
        {
            dimensionTop = dimensionRight = Math.Max(dimensionTop, dimensionRight);
        }

        if (dimensionTop < 8 || dimensionRight < 8 || dimensionTop > 144 || dimensionRight > 144)
        {
            return null;
        }

        var transform = PerspectiveTransform.QuadrilateralToQuadrilateral(
            0.5f, 0.5f,
            dimensionTop - 0.5f, 0.5f,
            dimensionTop - 0.5f, dimensionRight - 0.5f,
            0.5f, dimensionRight - 0.5f,
            topLeft.X, topLeft.Y,
            topRight.Value.X, topRight.Value.Y,
            bottomRight.X, bottomRight.Y,
            bottomLeft.X, bottomLeft.Y);

        var bits = GridSampler.Sample(_image, dimensionTop, dimensionRight, transform);
        return bits is null
            ? null
            : new DetectorResult(bits, [topLeft, bottomLeft, bottomRight, topRight.Value]);
    }

    /// <summary>Finds the edge with the fewest colour transitions: one arm of the solid finder.</summary>
    private ScanPoint[] DetectSolid1(ScanPoint[] cornerPoints)
    {
        // The rectangle detector reports its points in the order 0, 2 across the top and 1, 3
        // across the bottom, so they are re-labelled into a cycle first.
        var a = cornerPoints[0];
        var b = cornerPoints[1];
        var c = cornerPoints[3];
        var d = cornerPoints[2];

        var trAb = TransitionsBetween(a, b);
        var trBc = TransitionsBetween(b, c);
        var trCd = TransitionsBetween(c, d);
        var trDa = TransitionsBetween(d, a);

        var min = trAb;
        ScanPoint[] points = [d, a, b, c];

        if (min > trBc)
        {
            min = trBc;
            points = [a, b, c, d];
        }

        if (min > trCd)
        {
            min = trCd;
            points = [b, c, d, a];
        }

        if (min > trDa)
        {
            points = [c, d, a, b];
        }

        return points;
    }

    /// <summary>Identifies which of the two edges adjacent to the first solid edge is also solid.</summary>
    private ScanPoint[] DetectSolid2(ScanPoint[] points)
    {
        var a = points[0];
        var b = points[1];
        var c = points[2];
        var d = points[3];

        // Transitions measured exactly on an edge are unstable, so the sample line is nudged a
        // fraction of a module inwards first.
        var tr = TransitionsBetween(a, d);
        var bs = ShiftPoint(b, c, (tr + 1) * 4);
        var cs = ShiftPoint(c, b, (tr + 1) * 4);

        var trBa = TransitionsBetween(bs, a);
        var trCd = TransitionsBetween(cs, d);

        return trBa < trCd ? points : [b, c, d, a];
    }

    /// <summary>
    /// Estimates the corner diagonally opposite the solid finder, which carries no ink of its
    /// own and therefore cannot be found directly.
    /// </summary>
    private ScanPoint? CorrectTopRight(ScanPoint[] points)
    {
        var a = points[0];
        var b = points[1];
        var c = points[2];
        var d = points[3];

        var trTop = TransitionsBetween(a, d);
        var trRight = TransitionsBetween(b, d);
        var aShifted = ShiftPoint(a, b, (trRight + 1) * 4);
        var cShifted = ShiftPoint(c, b, (trTop + 1) * 4);

        trTop = TransitionsBetween(aShifted, d);
        trRight = TransitionsBetween(cShifted, d);

        var candidate1 = new ScanPoint(
            d.X + ((c.X - b.X) / (trTop + 1)),
            d.Y + ((c.Y - b.Y) / (trTop + 1)));
        var candidate2 = new ScanPoint(
            d.X + ((a.X - b.X) / (trRight + 1)),
            d.Y + ((a.Y - b.Y) / (trRight + 1)));

        var valid1 = IsValid(candidate1);
        var valid2 = IsValid(candidate2);

        if (!valid1)
        {
            return valid2 ? candidate2 : null;
        }

        if (!valid2)
        {
            return candidate1;
        }

        // Prefer the candidate that sees more of the dashed timing pattern.
        var sum1 = TransitionsBetween(aShifted, candidate1) + TransitionsBetween(cShifted, candidate1);
        var sum2 = TransitionsBetween(aShifted, candidate2) + TransitionsBetween(cShifted, candidate2);
        return sum1 > sum2 ? candidate1 : candidate2;
    }

    /// <summary>Moves the four corners from the symbol edges to the centres of the corner modules.</summary>
    private ScanPoint[] ShiftToModuleCenter(ScanPoint[] points)
    {
        var a = points[0];
        var b = points[1];
        var c = points[2];
        var d = points[3];

        var dimH = TransitionsBetween(a, d) + 1;
        var dimV = TransitionsBetween(c, d) + 1;

        var aShifted = ShiftPoint(a, b, dimV * 4);
        var cShifted = ShiftPoint(c, b, dimH * 4);

        dimH = TransitionsBetween(aShifted, d) + 1;
        dimV = TransitionsBetween(cShifted, d) + 1;

        if ((dimH & 0x01) == 1)
        {
            dimH++;
        }

        if ((dimV & 0x01) == 1)
        {
            dimV++;
        }

        // The rectangle detector reports points just inside the symbol; push them back out so
        // the half module shift below lands on module centres.
        var centerX = (a.X + b.X + c.X + d.X) / 4;
        var centerY = (a.Y + b.Y + c.Y + d.Y) / 4;
        a = MoveAway(a, centerX, centerY);
        b = MoveAway(b, centerX, centerY);
        c = MoveAway(c, centerX, centerY);
        d = MoveAway(d, centerX, centerY);

        var finalA = ShiftPoint(ShiftPoint(a, b, dimV * 4), d, dimH * 4);
        var finalB = ShiftPoint(ShiftPoint(b, a, dimV * 4), c, dimH * 4);
        var finalC = ShiftPoint(ShiftPoint(c, d, dimV * 4), b, dimH * 4);
        var finalD = ShiftPoint(ShiftPoint(d, c, dimV * 4), a, dimH * 4);

        return [finalA, finalB, finalC, finalD];
    }

    private static ScanPoint ShiftPoint(ScanPoint point, ScanPoint to, int divisor) => new(
        point.X + ((to.X - point.X) / (divisor + 1)),
        point.Y + ((to.Y - point.Y) / (divisor + 1)));

    private static ScanPoint MoveAway(ScanPoint point, float fromX, float fromY) => new(
        point.X < fromX ? point.X - 1 : point.X + 1,
        point.Y < fromY ? point.Y - 1 : point.Y + 1);

    private bool IsValid(ScanPoint point) =>
        point.X >= 0 && point.X <= _image.Width - 1 && point.Y > 0 && point.Y <= _image.Height - 1;

    /// <summary>Counts colour changes along the line between two points.</summary>
    private int TransitionsBetween(ScanPoint from, ScanPoint to)
    {
        var fromX = (int)from.X;
        var fromY = (int)from.Y;
        var toX = (int)to.X;
        var toY = Math.Min(_image.Height - 1, (int)to.Y);

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

        var transitions = 0;
        var inBlack = _image.GetSafe(steep ? fromY : fromX, steep ? fromX : fromY);

        var y = fromY;
        for (var x = fromX; x != toX; x += xStep)
        {
            var isBlack = _image.GetSafe(steep ? y : x, steep ? x : y);
            if (isBlack != inBlack)
            {
                transitions++;
                inBlack = isBlack;
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

        return transitions;
    }
}
