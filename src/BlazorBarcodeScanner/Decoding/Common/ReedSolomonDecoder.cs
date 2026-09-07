namespace BlazorBarcodeScanner.Decoding.Common;

/// <summary>
/// Reed-Solomon error correction over a <see cref="GenericGF"/>.
/// </summary>
/// <remarks>
/// Errors are located with the Euclidean algorithm (equivalent to Berlekamp-Massey but easier
/// to keep correct) and then evaluated with Forney's formula. Failure is reported as a
/// <see langword="false"/> return value rather than an exception because a failed correction is
/// the normal outcome for most camera frames, and exceptions on that path would dominate the
/// cost of continuous scanning.
/// </remarks>
public sealed class ReedSolomonDecoder
{
    private readonly GenericGF _field;

    /// <summary>Creates a decoder for the given field.</summary>
    /// <param name="field">The field the codewords live in.</param>
    public ReedSolomonDecoder(GenericGF field)
    {
        ArgumentNullException.ThrowIfNull(field);
        _field = field;
    }

    /// <summary>A decoder for the QR Code field.</summary>
    public static ReedSolomonDecoder QrCode { get; } = new(GenericGF.QrCodeField256);

    /// <summary>A decoder for the Data Matrix field.</summary>
    public static ReedSolomonDecoder DataMatrix { get; } = new(GenericGF.DataMatrixField256);

    /// <summary>
    /// Repairs <paramref name="received"/> in place.
    /// </summary>
    /// <param name="received">Data codewords followed by error correction codewords.</param>
    /// <param name="twoS">Number of error correction codewords.</param>
    /// <param name="errorsCorrected">Number of codewords that had to be changed.</param>
    /// <returns><see langword="false"/> when the block carries more errors than it can correct.</returns>
    public bool TryDecode(int[] received, int twoS, out int errorsCorrected)
    {
        ArgumentNullException.ThrowIfNull(received);
        errorsCorrected = 0;

        var poly = new GfPoly(_field, received);
        var syndromeCoefficients = new int[twoS];
        var noError = true;
        for (var i = 0; i < twoS; i++)
        {
            var evaluation = poly.EvaluateAt(_field.Exp(i + _field.GeneratorBase));
            syndromeCoefficients[syndromeCoefficients.Length - 1 - i] = evaluation;
            if (evaluation != 0)
            {
                noError = false;
            }
        }

        if (noError)
        {
            return true;
        }

        var syndrome = new GfPoly(_field, syndromeCoefficients);
        if (!TryRunEuclideanAlgorithm(_field.BuildMonomial(twoS, 1), syndrome, twoS, out var sigma, out var omega))
        {
            return false;
        }

        if (!TryFindErrorLocations(sigma, out var errorLocations))
        {
            return false;
        }

        var errorMagnitudes = FindErrorMagnitudes(omega, errorLocations);
        for (var i = 0; i < errorLocations.Length; i++)
        {
            var position = received.Length - 1 - _field.Log(errorLocations[i]);
            if (position < 0)
            {
                return false;
            }

            received[position] = GenericGF.AddOrSubtract(received[position], errorMagnitudes[i]);
        }

        errorsCorrected = errorLocations.Length;
        return true;
    }

    private bool TryRunEuclideanAlgorithm(GfPoly a, GfPoly b, int r, out GfPoly sigma, out GfPoly omega)
    {
        sigma = _field.Zero;
        omega = _field.Zero;

        if (a.Degree < b.Degree)
        {
            (a, b) = (b, a);
        }

        var rLast = a;
        var rCurrent = b;
        var tLast = _field.Zero;
        var tCurrent = _field.One;

        // Iterate until the remainder degree drops below half the error correction capacity.
        while (rCurrent.Degree >= r / 2)
        {
            var rLastLast = rLast;
            var tLastLast = tLast;
            rLast = rCurrent;
            tLast = tCurrent;

            if (rLast.IsZero)
            {
                return false;
            }

            rCurrent = rLastLast;
            var q = _field.Zero;
            var denominatorLeadingTerm = rLast.GetCoefficient(rLast.Degree);
            var dltInverse = _field.Inverse(denominatorLeadingTerm);
            while (rCurrent.Degree >= rLast.Degree && !rCurrent.IsZero)
            {
                var degreeDiff = rCurrent.Degree - rLast.Degree;
                var scale = _field.Multiply(rCurrent.GetCoefficient(rCurrent.Degree), dltInverse);
                q = q.AddOrSubtract(_field.BuildMonomial(degreeDiff, scale));
                rCurrent = rCurrent.AddOrSubtract(rLast.MultiplyByMonomial(degreeDiff, scale));
            }

            tCurrent = q.Multiply(tLast).AddOrSubtract(tLastLast);

            if (rCurrent.Degree >= rLast.Degree)
            {
                return false;
            }
        }

        var sigmaTildeAtZero = tCurrent.GetCoefficient(0);
        if (sigmaTildeAtZero == 0)
        {
            return false;
        }

        var inverse = _field.Inverse(sigmaTildeAtZero);
        sigma = tCurrent.Multiply(inverse);
        omega = rCurrent.Multiply(inverse);
        return true;
    }

    private bool TryFindErrorLocations(GfPoly errorLocator, out int[] locations)
    {
        var errorCount = errorLocator.Degree;
        if (errorCount == 1)
        {
            locations = [errorLocator.GetCoefficient(1)];
            return true;
        }

        locations = new int[errorCount];
        var found = 0;
        for (var i = 1; i < _field.Size && found < errorCount; i++)
        {
            if (errorLocator.EvaluateAt(i) == 0)
            {
                locations[found++] = _field.Inverse(i);
            }
        }

        return found == errorCount;
    }

    private int[] FindErrorMagnitudes(GfPoly errorEvaluator, int[] errorLocations)
    {
        var count = errorLocations.Length;
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var xiInverse = _field.Inverse(errorLocations[i]);
            var denominator = 1;
            for (var j = 0; j < count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                // Written as 1 + term rather than the direct product so that the intermediate
                // never becomes zero for a valid locator set.
                var term = _field.Multiply(errorLocations[j], xiInverse);
                var termPlus1 = (term & 1) == 0 ? term | 1 : term & ~1;
                denominator = _field.Multiply(denominator, termPlus1);
            }

            result[i] = _field.Multiply(errorEvaluator.EvaluateAt(xiInverse), _field.Inverse(denominator));
            if (_field.GeneratorBase != 0)
            {
                result[i] = _field.Multiply(result[i], xiInverse);
            }
        }

        return result;
    }
}
