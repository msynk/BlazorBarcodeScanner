using System.Globalization;
using System.Numerics;
using System.Text;
using BlazorBarcodeScanner.Decoding.Common;

namespace BlazorBarcodeScanner.Decoding.Pdf417;

/// <summary>
/// Turns the corrected codewords of a PDF417 symbol into the payload it encodes.
/// </summary>
/// <remarks>
/// PDF417 has three compaction modes and the payload latches between them. Text compaction packs
/// two characters into every codeword through four sub-alphabets, byte compaction packs six bytes
/// into every five codewords, and numeric compaction treats runs of digits as one large base 900
/// number.
/// </remarks>
public static class Pdf417BitStreamParser
{
    private const int TextCompaction = 900;
    private const int ByteCompaction = 901;
    private const int NumericCompaction = 902;
    private const int ShiftToByte = 913;
    private const int MacroTerminator = 922;
    private const int MacroOptionalField = 923;
    private const int ByteCompactionSixes = 924;
    private const int EciUserDefined = 925;
    private const int EciGeneralPurpose = 926;
    private const int EciCharset = 927;
    private const int MacroControlBlock = 928;

    private static readonly char[] PunctuationChars =
    [
        ';', '<', '>', '@', '[', '\\', ']', '_', '`', '~', '!', '\r', '\t', ',', ':',
        '\n', '-', '.', '$', '/', '"', '|', '*', '(', ')', '?', '{', '}', '\'',
    ];

    private static readonly char[] MixedChars =
    [
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '&', '\r', '\t', ',', ':',
        '#', '-', '.', '$', '/', '+', '%', '*', '=', '^',
    ];

    private enum TextMode
    {
        Alpha,
        Lower,
        Mixed,
        Punctuation,
        AlphaShift,
        PunctuationShift,
    }

    /// <summary>Decodes the payload.</summary>
    /// <param name="codewords">Corrected codewords; index 0 is the symbol length descriptor.</param>
    /// <param name="rows">Number of symbol rows, reported back in the result.</param>
    /// <param name="columns">Number of data columns, reported back in the result.</param>
    /// <param name="errorCorrectionLevel">Security level, reported back in the result.</param>
    /// <param name="errorsCorrected">Number of codewords the error correction stage repaired.</param>
    /// <returns>The payload, or <see langword="null"/> when the codeword stream is malformed.</returns>
    public static DecoderResult? Decode(
        int[] codewords, int rows, int columns, int errorCorrectionLevel, int errorsCorrected)
    {
        ArgumentNullException.ThrowIfNull(codewords);

        if (codewords.Length == 0)
        {
            return null;
        }

        var length = codewords[0];
        if (length < 1 || length > codewords.Length)
        {
            return null;
        }

        var result = new StringBuilder(length * 2);
        var raw = new List<byte>(length * 2);

        int? eci = null;
        int? segmentIndex = null;
        int? segmentCount = null;

        var index = 1;
        while (index < length)
        {
            var codeword = codewords[index++];

            switch (codeword)
            {
                case TextCompaction:
                    index = DecodeText(codewords, index, length, result, raw);
                    break;

                case ByteCompaction:
                case ByteCompactionSixes:
                    index = DecodeBytes(codewords, index, length, result, raw, codeword == ByteCompactionSixes);
                    break;

                case NumericCompaction:
                    index = DecodeNumeric(codewords, index, length, result, raw);
                    break;

                case ShiftToByte:
                    if (index >= length)
                    {
                        return null;
                    }

                    Append(result, raw, (char)(codewords[index++] & 0xFF));
                    break;

                case EciCharset:
                case EciUserDefined:
                    if (index >= length)
                    {
                        return null;
                    }

                    eci = codewords[index++];
                    break;

                case EciGeneralPurpose:
                    if (index + 1 >= length)
                    {
                        return null;
                    }

                    index += 2;
                    break;

                case MacroControlBlock:
                    index = DecodeMacro(codewords, index, length, ref segmentIndex, ref segmentCount);
                    break;

                case MacroOptionalField:
                case MacroTerminator:
                    // Optional macro fields carry metadata that is not part of the payload.
                    index = length;
                    break;

                default:
                    if (codeword >= TextCompaction)
                    {
                        // An unknown high codeword means the stream is not something this parser
                        // understands; stopping is safer than emitting rubbish.
                        return null;
                    }

                    // A stream that starts without an explicit latch is in text compaction.
                    index = DecodeText(codewords, index - 1, length, result, raw);
                    break;
            }

            if (index < 0)
            {
                return null;
            }
        }

        var text = result.ToString();
        return new DecoderResult(raw.ToArray(), text)
        {
            ErrorCorrectionLevel = errorCorrectionLevel.ToString(CultureInfo.InvariantCulture),
            SymbolVersion = $"{rows}x{columns}",
            ErrorsCorrected = errorsCorrected,
            Rows = rows,
            Columns = columns,
            Eci = eci,
            StructuredAppendIndex = segmentIndex,
            StructuredAppendCount = segmentCount,
        };
    }

    private static int DecodeText(int[] codewords, int index, int length, StringBuilder result, List<byte> raw)
    {
        var mode = TextMode.Alpha;
        var priorToShift = TextMode.Alpha;

        Span<int> values = stackalloc int[2];

        while (index < length)
        {
            var codeword = codewords[index];
            if (codeword >= TextCompaction)
            {
                break;
            }

            index++;
            values[0] = codeword / 30;
            values[1] = codeword % 30;

            foreach (var value in values)
            {
                switch (mode)
                {
                    case TextMode.Alpha:
                    case TextMode.AlphaShift:
                        if (value < 26)
                        {
                            Append(result, raw, (char)('A' + value));
                        }
                        else if (value == 26)
                        {
                            Append(result, raw, ' ');
                        }
                        else if (value == 27)
                        {
                            mode = mode == TextMode.AlphaShift ? priorToShift : TextMode.Lower;
                            continue;
                        }
                        else if (value == 28)
                        {
                            mode = TextMode.Mixed;
                            continue;
                        }
                        else if (value == 29)
                        {
                            priorToShift = mode == TextMode.AlphaShift ? priorToShift : mode;
                            mode = TextMode.PunctuationShift;
                            continue;
                        }

                        if (mode == TextMode.AlphaShift)
                        {
                            mode = priorToShift;
                        }

                        break;

                    case TextMode.Lower:
                        if (value < 26)
                        {
                            Append(result, raw, (char)('a' + value));
                        }
                        else if (value == 26)
                        {
                            Append(result, raw, ' ');
                        }
                        else if (value == 27)
                        {
                            priorToShift = mode;
                            mode = TextMode.AlphaShift;
                        }
                        else if (value == 28)
                        {
                            mode = TextMode.Mixed;
                        }
                        else if (value == 29)
                        {
                            priorToShift = mode;
                            mode = TextMode.PunctuationShift;
                        }

                        break;

                    case TextMode.Mixed:
                        if (value < MixedChars.Length)
                        {
                            Append(result, raw, MixedChars[value]);
                        }
                        else if (value == 25)
                        {
                            mode = TextMode.Punctuation;
                        }
                        else if (value == 26)
                        {
                            Append(result, raw, ' ');
                        }
                        else if (value == 27)
                        {
                            mode = TextMode.Lower;
                        }
                        else if (value == 28)
                        {
                            mode = TextMode.Alpha;
                        }
                        else if (value == 29)
                        {
                            priorToShift = mode;
                            mode = TextMode.PunctuationShift;
                        }

                        break;

                    case TextMode.Punctuation:
                        if (value < PunctuationChars.Length)
                        {
                            Append(result, raw, PunctuationChars[value]);
                        }
                        else if (value == 29)
                        {
                            mode = TextMode.Alpha;
                        }

                        break;

                    case TextMode.PunctuationShift:
                        if (value < PunctuationChars.Length)
                        {
                            Append(result, raw, PunctuationChars[value]);
                        }
                        else if (value == 29)
                        {
                            mode = TextMode.Alpha;
                            continue;
                        }

                        mode = priorToShift;
                        break;
                }
            }
        }

        return index;
    }

    private static int DecodeBytes(
        int[] codewords, int index, int length, StringBuilder result, List<byte> raw, bool sixes)
    {
        var group = new List<int>(5);

        while (index < length)
        {
            var codeword = codewords[index];
            if (codeword >= TextCompaction)
            {
                break;
            }

            index++;
            group.Add(codeword);

            if (group.Count != 5)
            {
                continue;
            }

            // Five codewords carry six bytes as one base 900 number.
            long value = 0;
            foreach (var item in group)
            {
                value = (value * 900) + item;
            }

            for (var i = 5; i >= 0; i--)
            {
                Append(result, raw, (char)((value >> (8 * i)) & 0xFF));
            }

            group.Clear();
        }

        // A trailing partial group carries one byte per codeword. Mode 924 promises a multiple of
        // six bytes and therefore should never leave one behind.
        if (group.Count > 0)
        {
            if (sixes)
            {
                return -1;
            }

            foreach (var item in group)
            {
                Append(result, raw, (char)(item & 0xFF));
            }
        }

        return index;
    }

    private static int DecodeNumeric(int[] codewords, int index, int length, StringBuilder result, List<byte> raw)
    {
        var group = new List<int>(15);

        while (index < length)
        {
            var codeword = codewords[index];
            if (codeword >= TextCompaction)
            {
                break;
            }

            index++;
            group.Add(codeword);

            if (group.Count == 15)
            {
                if (!EmitNumericGroup(group, result, raw))
                {
                    return -1;
                }

                group.Clear();
            }
        }

        if (group.Count > 0 && !EmitNumericGroup(group, result, raw))
        {
            return -1;
        }

        return index;
    }

    /// <summary>
    /// Converts a group of base 900 codewords into digits.
    /// </summary>
    /// <remarks>
    /// The encoder prefixes the digits with a 1 so that leading zeros survive the conversion to a
    /// number, so that digit is dropped again here.
    /// </remarks>
    private static bool EmitNumericGroup(List<int> group, StringBuilder result, List<byte> raw)
    {
        BigInteger value = 0;
        foreach (var codeword in group)
        {
            value = (value * 900) + codeword;
        }

        var digits = value.ToString(CultureInfo.InvariantCulture);
        if (digits.Length < 2 || digits[0] != '1')
        {
            return false;
        }

        foreach (var digit in digits.AsSpan(1))
        {
            Append(result, raw, digit);
        }

        return true;
    }

    private static int DecodeMacro(int[] codewords, int index, int length, ref int? segmentIndex, ref int? segmentCount)
    {
        // The control block starts with a five codeword numeric segment index.
        if (index + 5 > length)
        {
            return length;
        }

        BigInteger value = 0;
        for (var i = 0; i < 5; i++)
        {
            value = (value * 900) + codewords[index + i];
        }

        var digits = value.ToString(CultureInfo.InvariantCulture);
        if (digits.Length > 1 && digits[0] == '1' && int.TryParse(digits.AsSpan(1), out var parsed))
        {
            segmentIndex = parsed;
        }

        index += 5;

        // The file identifier and optional fields follow; they are metadata, not payload, so the
        // remainder of the stream is skipped.
        _ = segmentCount;
        return length;
    }

    private static void Append(StringBuilder result, List<byte> raw, char value)
    {
        result.Append(value);
        raw.Add((byte)value);
    }
}
