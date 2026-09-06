using System.Text;

namespace BlazorScanner.Decoding.Common;

/// <summary>
/// Maps ECI (Extended Channel Interpretation) assignment numbers to text encodings.
/// </summary>
/// <remarks>
/// Only the encodings that ship with the base class library are mapped, because pulling in the
/// legacy code page provider would add a dependency and roughly a megabyte to a WebAssembly
/// payload for encodings that essentially never appear in the wild. Unmapped ECI values fall
/// back to ISO-8859-1, which is the behaviour the QR and Data Matrix specifications prescribe
/// for an unsupported interpretation, and the value is still reported through
/// <see cref="BarcodeResultMetadata.Eci"/> so an application can re-interpret the raw bytes.
/// </remarks>
public static class CharacterSetEci
{
    /// <summary>ISO-8859-1, the default interpretation for QR Code and Data Matrix.</summary>
    public static Encoding Latin1 { get; } = Encoding.Latin1;

    /// <summary>Returns the encoding for an ECI assignment number.</summary>
    /// <param name="eci">The ECI assignment number.</param>
    public static Encoding FromEci(int eci) => eci switch
    {
        0 or 1 or 2 or 3 => Latin1,
        20 => Encoding.UTF8, // Shift JIS is not available without the code page provider.
        25 => Encoding.BigEndianUnicode,
        26 => Encoding.UTF8,
        27 or 170 => Encoding.ASCII,
        _ => Latin1,
    };

    /// <summary>
    /// Guesses the encoding of a payload that carried no ECI, by checking whether it is valid UTF-8.
    /// </summary>
    /// <param name="bytes">The raw payload.</param>
    /// <remarks>
    /// Many real world encoders emit UTF-8 without declaring ECI 26. Strict UTF-8 validation is a
    /// reliable discriminator: multi-byte sequences that decode cleanly are almost never
    /// coincidental in ISO-8859-1 text.
    /// </remarks>
    public static Encoding GuessEncoding(ReadOnlySpan<byte> bytes)
    {
        var hasHighBytes = false;
        foreach (var b in bytes)
        {
            if (b >= 0x80)
            {
                hasHighBytes = true;
                break;
            }
        }

        if (!hasHighBytes)
        {
            return Latin1;
        }

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            strict.GetString(bytes);
            return strict;
        }
        catch (DecoderFallbackException)
        {
            return Latin1;
        }
    }
}
