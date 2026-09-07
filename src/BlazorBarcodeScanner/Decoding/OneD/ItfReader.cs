using System.Text;
using BlazorBarcodeScanner.Decoding.Common;
using BlazorBarcodeScanner.Imaging;

namespace BlazorBarcodeScanner.Decoding.OneD;

/// <summary>
/// Decodes Interleaved 2 of 5 (ITF).
/// </summary>
/// <remarks>
/// <para>
/// ITF encodes digits in pairs: the first digit of a pair is carried by five bars and the second
/// by the five spaces between them. A pair is therefore read as ten runs which are then split
/// into two five element patterns.
/// </para>
/// <para>
/// ITF has no check character and no self-checking structure beyond the digit patterns
/// themselves, so a partial scan can produce a shorter but internally valid value. The reader
/// defends against that the way the symbology's users do, by restricting results to a set of
/// accepted lengths.
/// </para>
/// </remarks>
public sealed class ItfReader : IRowDecoder
{
    private const float MaxAvgVariance = 0.38f;
    private const float MaxIndividualVariance = 0.5f;

    private static readonly int[] StartPattern = [1, 1, 1, 1];

    /// <summary>The two accepted end patterns, measured on the reversed row.</summary>
    private static readonly int[][] EndPatternsReversed = [[1, 1, 2], [1, 1, 3]];

    /// <summary>
    /// Digit patterns. The first ten use a 2:1 wide to narrow ratio and the second ten a 3:1
    /// ratio; both are legal and printers use both, so a match is taken modulo ten.
    /// </summary>
    private static readonly int[][] Patterns =
    [
        [1, 1, 2, 2, 1], [2, 1, 1, 1, 2], [1, 2, 1, 1, 2], [2, 2, 1, 1, 1], [1, 1, 2, 1, 2],
        [2, 1, 2, 1, 1], [1, 2, 2, 1, 1], [1, 1, 1, 2, 2], [2, 1, 1, 2, 1], [1, 2, 1, 2, 1],
        [1, 1, 3, 3, 1], [3, 1, 1, 1, 3], [1, 3, 1, 1, 3], [3, 3, 1, 1, 1], [1, 1, 3, 1, 3],
        [3, 1, 3, 1, 1], [1, 3, 3, 1, 1], [1, 1, 1, 3, 3], [3, 1, 1, 3, 1], [1, 3, 1, 3, 1],
    ];

    private readonly int[] _digitPair = new int[10];
    private readonly int[] _guardCounters = new int[10];
    private readonly StringBuilder _builder = new(20);

    private int _narrowLineWidth = -1;

    /// <summary>Creates a reader.</summary>
    /// <param name="allowedLengths">
    /// Digit counts a result is allowed to have. Pass an empty collection to accept any even
    /// length, which is faster but far more prone to reporting a truncated value.
    /// </param>
    public ItfReader(IReadOnlyCollection<int>? allowedLengths = null)
    {
        AllowedLengths = allowedLengths ?? [6, 8, 10, 12, 14, 16, 18, 20];
    }

    /// <summary>Digit counts a result is allowed to have.</summary>
    public IReadOnlyCollection<int> AllowedLengths { get; }

    /// <inheritdoc />
    public BarcodeFormat Formats => BarcodeFormat.Itf;

    /// <inheritdoc />
    public SymbolDecodeResult? DecodeRow(int rowNumber, BitRow row, BarcodeFormat enabledFormats)
    {
        if ((enabledFormats & BarcodeFormat.Itf) == 0)
        {
            return null;
        }

        if (!TryDecodeStart(row, out var startEnd))
        {
            return null;
        }

        if (!TryDecodeEnd(row, out var endBegin))
        {
            return null;
        }

        var result = _builder;
        result.Clear();

        if (!TryDecodeMiddle(row, startEnd, endBegin, result))
        {
            return null;
        }

        if (AllowedLengths.Count > 0 && !AllowedLengths.Contains(result.Length))
        {
            return null;
        }

        if (result.Length == 0)
        {
            return null;
        }

        var text = result.ToString();
        var payload = new DecoderResult(Encoding.ASCII.GetBytes(text), text);
        return new SymbolDecodeResult(
            payload,
            BarcodeFormat.Itf,
            [new ScanPoint(startEnd, rowNumber), new ScanPoint(endBegin, rowNumber)]);
    }

    private bool TryDecodeMiddle(BitRow row, int payloadStart, int payloadEnd, StringBuilder result)
    {
        var digitPair = _digitPair;
        Span<int> black = stackalloc int[5];
        Span<int> white = stackalloc int[5];

        while (payloadStart < payloadEnd)
        {
            if (!LinearPatterns.RecordPattern(row, payloadStart, digitPair))
            {
                return false;
            }

            for (var k = 0; k < 5; k++)
            {
                black[k] = digitPair[k * 2];
                white[k] = digitPair[(k * 2) + 1];
            }

            if (!TryDecodeDigit(black, out var first) || !TryDecodeDigit(white, out var second))
            {
                return false;
            }

            result.Append((char)('0' + first));
            result.Append((char)('0' + second));

            foreach (var counter in digitPair)
            {
                payloadStart += counter;
            }

            if (result.Length > 40)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecodeDigit(ReadOnlySpan<int> counters, out int digit)
    {
        digit = -1;
        var bestVariance = MaxAvgVariance;
        for (var i = 0; i < Patterns.Length; i++)
        {
            var variance = LinearPatterns.PatternMatchVariance(counters, Patterns[i], MaxIndividualVariance);
            if (variance < bestVariance)
            {
                bestVariance = variance;
                digit = i % 10;
            }
        }

        return digit >= 0;
    }

    private bool TryDecodeStart(BitRow row, out int startEnd)
    {
        startEnd = 0;

        var offset = SkipWhiteSpace(row);
        if (offset < 0)
        {
            return false;
        }

        if (!LinearPatterns.FindGuardPattern(
                row, offset, false, StartPattern, _guardCounters, out var start, out var end,
                MaxAvgVariance, MaxIndividualVariance))
        {
            return false;
        }

        // Every element of the start pattern is one module wide, which gives the module width.
        _narrowLineWidth = (end - start) / 4;
        if (_narrowLineWidth <= 0 || !ValidateQuietZone(row, start))
        {
            return false;
        }

        startEnd = end;
        return true;
    }

    private bool TryDecodeEnd(BitRow row, out int endBegin)
    {
        endBegin = 0;

        // The end pattern is only recognisable when read right to left, so the row is reversed,
        // searched, and reversed back. The reversal is in place and allocation free.
        row.Reverse();
        try
        {
            var offset = SkipWhiteSpace(row);
            if (offset < 0)
            {
                return false;
            }

            var found = LinearPatterns.FindGuardPattern(
                row, offset, false, EndPatternsReversed[0], _guardCounters, out var start, out var end,
                MaxAvgVariance, MaxIndividualVariance);

            if (!found)
            {
                found = LinearPatterns.FindGuardPattern(
                    row, offset, false, EndPatternsReversed[1], _guardCounters, out start, out end,
                    MaxAvgVariance, MaxIndividualVariance);
            }

            if (!found || !ValidateQuietZone(row, start))
            {
                return false;
            }

            endBegin = row.Size - end;
            return true;
        }
        finally
        {
            row.Reverse();
        }
    }

    /// <summary>
    /// Requires ten narrow modules of white before the symbol, which is the quiet zone the
    /// specification mandates and the main defence against reading a fragment of a larger image.
    /// </summary>
    private bool ValidateQuietZone(BitRow row, int startPattern)
    {
        var quietCount = _narrowLineWidth * 10;
        quietCount = Math.Min(quietCount, startPattern);

        for (var i = startPattern - 1; quietCount > 0 && i >= 0; i--)
        {
            if (row[i])
            {
                break;
            }

            quietCount--;
        }

        return quietCount == 0;
    }

    private static int SkipWhiteSpace(BitRow row)
    {
        var endStart = row.GetNextSet(0);
        return endStart == row.Size ? -1 : endStart;
    }
}
