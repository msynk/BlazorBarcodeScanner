using System.Text;
using BlazorScanner.Decoding.Common;

namespace BlazorScanner.Decoding.DataMatrix;

/// <summary>
/// Turns the corrected codewords of a Data Matrix symbol into the payload it encodes.
/// </summary>
/// <remarks>
/// Data Matrix has six compaction modes and a symbol switches between them with latch and
/// unlatch codewords. ASCII is the base mode and the only one that can start a symbol; the
/// others compress specific alphabets, so a mixed payload alternates between them repeatedly.
/// </remarks>
public static class DataMatrixBitStreamParser
{
    private const int PadCodeword = 129;
    private const int LatchC40 = 230;
    private const int LatchBase256 = 231;
    private const int Fnc1 = 232;
    private const int StructuredAppend = 233;
    private const int ReaderProgramming = 234;
    private const int UpperShift = 235;
    private const int Macro05 = 236;
    private const int Macro06 = 237;
    private const int LatchX12 = 238;
    private const int LatchText = 239;
    private const int LatchEdifact = 240;
    private const int Eci = 241;

    private static readonly char[] C40BasicSet =
    [
        '*', '*', '*', ' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M',
        'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z',
    ];

    private static readonly char[] C40Shift2Set =
    [
        '!', '"', '#', '$', '%', '&', '\'', '(', ')', '*', '+', ',', '-', '.',
        '/', ':', ';', '<', '=', '>', '?', '@', '[', '\\', ']', '^', '_',
    ];

    private static readonly char[] TextBasicSet =
    [
        '*', '*', '*', ' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm',
        'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z',
    ];

    private static readonly char[] TextShift3Set =
    [
        '`', 'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M',
        'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z',
        '{', '|', '}', '~', (char)127,
    ];

    private enum Mode
    {
        Ascii,
        C40,
        Text,
        X12,
        Edifact,
        Base256,
        Pad,
    }

    /// <summary>Decodes the payload.</summary>
    /// <param name="bytes">Corrected data codewords, concatenated across blocks.</param>
    /// <param name="version">The symbol shape, reported back in the result.</param>
    /// <param name="errorsCorrected">Number of codewords the error correction stage repaired.</param>
    /// <returns>The payload, or <see langword="null"/> when the codeword stream is malformed.</returns>
    public static DecoderResult? Decode(byte[] bytes, DataMatrixVersion version, int errorsCorrected)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(version);

        var bits = new BitSource(bytes);
        var result = new StringBuilder(bytes.Length * 2);
        var trailer = new StringBuilder(0);

        var mode = Mode.Ascii;
        var isGs1 = false;
        int? structuredAppendIndex = null;
        int? structuredAppendCount = null;
        int? structuredAppendParity = null;
        int? eci = null;

        try
        {
            do
            {
                if (mode == Mode.Ascii)
                {
                    mode = DecodeAscii(
                        ref bits, result, trailer,
                        ref isGs1, ref structuredAppendIndex, ref structuredAppendCount,
                        ref structuredAppendParity, ref eci);
                    if (mode == Mode.Pad)
                    {
                        break;
                    }

                    continue;
                }

                var ok = mode switch
                {
                    Mode.C40 => DecodeC40OrText(ref bits, result, text: false),
                    Mode.Text => DecodeC40OrText(ref bits, result, text: true),
                    Mode.X12 => DecodeX12(ref bits, result),
                    Mode.Edifact => DecodeEdifact(ref bits, result),
                    Mode.Base256 => DecodeBase256(ref bits, result),
                    _ => false,
                };

                if (!ok)
                {
                    return null;
                }

                mode = Mode.Ascii;
            }
            while (bits.Available > 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A truncated bit stream is a decode failure, not an error condition worth throwing.
            return null;
        }

        if (trailer.Length > 0)
        {
            result.Append(trailer);
        }

        var text = result.ToString();
        return new DecoderResult(CharacterSetEci.Latin1.GetBytes(text), text)
        {
            SymbolVersion = version.ToString(),
            ErrorsCorrected = errorsCorrected,
            Rows = version.SymbolSizeRows,
            Columns = version.SymbolSizeColumns,
            IsGs1 = isGs1,
            Eci = eci,
            StructuredAppendIndex = structuredAppendIndex,
            StructuredAppendCount = structuredAppendCount,
            StructuredAppendParity = structuredAppendParity,
        };
    }

    private static Mode DecodeAscii(
        ref BitSource bits,
        StringBuilder result,
        StringBuilder trailer,
        ref bool isGs1,
        ref int? structuredAppendIndex,
        ref int? structuredAppendCount,
        ref int? structuredAppendParity,
        ref int? eci)
    {
        var upperShift = false;

        do
        {
            var value = bits.ReadBits(8);
            if (value == 0)
            {
                return Mode.Pad;
            }

            if (value <= 128)
            {
                // Plain ASCII, stored as the character value plus one.
                if (upperShift)
                {
                    value += 128;
                    upperShift = false;
                }

                result.Append((char)(value - 1));
                return Mode.Ascii;
            }

            if (value == PadCodeword)
            {
                return Mode.Pad;
            }

            if (value <= 229)
            {
                // A single codeword carries two digits.
                var digits = value - 130;
                if (digits < 10)
                {
                    result.Append('0');
                }

                result.Append(digits);
                continue;
            }

            switch (value)
            {
                case LatchC40:
                    return Mode.C40;
                case LatchBase256:
                    return Mode.Base256;
                case LatchX12:
                    return Mode.X12;
                case LatchText:
                    return Mode.Text;
                case LatchEdifact:
                    return Mode.Edifact;
                case Fnc1:
                    isGs1 = true;
                    if (result.Length > 0)
                    {
                        result.Append((char)29);
                    }

                    break;
                case StructuredAppend:
                    if (bits.Available >= 24)
                    {
                        var sequence = bits.ReadBits(8);
                        structuredAppendIndex = (sequence >> 4) + 1;
                        structuredAppendCount = (sequence & 0x0F) + 2;
                        structuredAppendParity = bits.ReadBits(16);
                    }

                    break;
                case ReaderProgramming:
                    break;
                case UpperShift:
                    upperShift = true;
                    break;
                case Macro05:
                case Macro06:
                    // The macro codewords stand for a fixed header and trailer around the payload.
                    result.Append(value == Macro05 ? "[)>05" : "[)>06");
                    trailer.Insert(0, "");
                    break;
                case Eci:
                    if (bits.Available >= 8)
                    {
                        eci = bits.ReadBits(8) - 1;
                    }

                    break;
                default:
                    // 254 is the unlatch codeword and is only legal as the very last one.
                    if (value != 254 || bits.Available != 0)
                    {
                        return Mode.Pad;
                    }

                    break;
            }
        }
        while (bits.Available > 0);

        return Mode.Ascii;
    }

    private static bool DecodeC40OrText(ref BitSource bits, StringBuilder result, bool text)
    {
        var upperShift = false;
        var shift = 0;
        Span<int> values = stackalloc int[3];

        var basicSet = text ? TextBasicSet : C40BasicSet;

        do
        {
            if (bits.Available == 8)
            {
                return true;
            }

            var firstByte = bits.ReadBits(8);
            if (firstByte == 254)
            {
                return true;
            }

            if (bits.Available < 8)
            {
                return false;
            }

            ParseTwoBytes(firstByte, bits.ReadBits(8), values);

            for (var i = 0; i < 3; i++)
            {
                var value = values[i];
                switch (shift)
                {
                    case 0:
                        if (value < 3)
                        {
                            shift = value + 1;
                        }
                        else if (value < basicSet.Length)
                        {
                            var c = basicSet[value];
                            result.Append(upperShift ? (char)(c + 128) : c);
                            upperShift = false;
                        }
                        else
                        {
                            return false;
                        }

                        break;

                    case 1:
                        result.Append(upperShift ? (char)(value + 128) : (char)value);
                        upperShift = false;
                        shift = 0;
                        break;

                    case 2:
                        if (value < C40Shift2Set.Length)
                        {
                            var c = C40Shift2Set[value];
                            result.Append(upperShift ? (char)(c + 128) : c);
                            upperShift = false;
                        }
                        else if (value == 27)
                        {
                            result.Append((char)29);
                        }
                        else if (value == 30)
                        {
                            upperShift = true;
                        }
                        else
                        {
                            return false;
                        }

                        shift = 0;
                        break;

                    case 3:
                        if (text)
                        {
                            if (value >= TextShift3Set.Length)
                            {
                                return false;
                            }

                            var c = TextShift3Set[value];
                            result.Append(upperShift ? (char)(c + 128) : c);
                        }
                        else
                        {
                            result.Append(upperShift ? (char)(value + 224) : (char)(value + 96));
                        }

                        upperShift = false;
                        shift = 0;
                        break;

                    default:
                        return false;
                }
            }
        }
        while (bits.Available > 0);

        return true;
    }

    private static bool DecodeX12(ref BitSource bits, StringBuilder result)
    {
        Span<int> values = stackalloc int[3];

        do
        {
            if (bits.Available == 8)
            {
                return true;
            }

            var firstByte = bits.ReadBits(8);
            if (firstByte == 254)
            {
                return true;
            }

            if (bits.Available < 8)
            {
                return false;
            }

            ParseTwoBytes(firstByte, bits.ReadBits(8), values);

            for (var i = 0; i < 3; i++)
            {
                var value = values[i];
                switch (value)
                {
                    case 0:
                        result.Append('\r');
                        break;
                    case 1:
                        result.Append('*');
                        break;
                    case 2:
                        result.Append('>');
                        break;
                    case 3:
                        result.Append(' ');
                        break;
                    case < 14:
                        result.Append((char)(value + 44));
                        break;
                    case < 40:
                        result.Append((char)(value + 51));
                        break;
                    default:
                        return false;
                }
            }
        }
        while (bits.Available > 0);

        return true;
    }

    private static bool DecodeEdifact(ref BitSource bits, StringBuilder result)
    {
        do
        {
            // Fewer than two bytes left means the remainder is padding, not a value.
            if (bits.Available <= 16)
            {
                return true;
            }

            for (var i = 0; i < 4; i++)
            {
                var value = bits.ReadBits(6);
                if (value == 0x1F)
                {
                    // Unlatch, then discard the rest of the current byte.
                    var bitsLeft = 8 - bits.BitOffset;
                    if (bitsLeft != 8)
                    {
                        bits.ReadBits(bitsLeft);
                    }

                    return true;
                }

                // Six bit values map onto ASCII 32 to 94, with the top bit reconstructed.
                if ((value & 0x20) == 0)
                {
                    value |= 0x40;
                }

                result.Append((char)value);
            }
        }
        while (bits.Available > 0);

        return true;
    }

    private static bool DecodeBase256(ref BitSource bits, StringBuilder result)
    {
        // Codeword positions are one based in the de-randomisation formula.
        var position = 1 + bits.ByteOffset;
        var d1 = Unrandomize(bits.ReadBits(8), position++);

        int count;
        if (d1 == 0)
        {
            count = bits.Available / 8;
        }
        else if (d1 < 250)
        {
            count = d1;
        }
        else
        {
            if (bits.Available < 8)
            {
                return false;
            }

            count = (250 * (d1 - 249)) + Unrandomize(bits.ReadBits(8), position++);
        }

        if (count < 0 || count * 8 > bits.Available)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            result.Append((char)Unrandomize(bits.ReadBits(8), position++));
        }

        return true;
    }

    /// <summary>
    /// Reverses the 255-state randomisation the specification applies to Base 256 data so that
    /// long runs of the same byte do not produce large blocks of identical modules.
    /// </summary>
    private static int Unrandomize(int codeword, int position)
    {
        var pseudoRandom = ((149 * position) % 255) + 1;
        var value = codeword - pseudoRandom;
        return value >= 0 ? value : value + 256;
    }

    private static void ParseTwoBytes(int firstByte, int secondByte, Span<int> values)
    {
        var full = (firstByte << 8) + secondByte - 1;
        var temp = full / 1600;
        values[0] = temp;
        full -= temp * 1600;
        temp = full / 40;
        values[1] = temp;
        values[2] = full - (temp * 40);
    }
}
