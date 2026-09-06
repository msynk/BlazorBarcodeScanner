namespace BlazorScanner.Decoding.QrCode;

/// <summary>
/// The four QR Code error correction levels, in the numeric order the format information
/// field uses, which is deliberately not the order of increasing capability.
/// </summary>
public enum QrErrorCorrectionLevel
{
    /// <summary>Recovers roughly 7% of the codewords.</summary>
    M = 0,

    /// <summary>Recovers roughly 15% of the codewords.</summary>
    L = 1,

    /// <summary>Recovers roughly 25% of the codewords.</summary>
    H = 2,

    /// <summary>Recovers roughly 30% of the codewords.</summary>
    Q = 3,
}

/// <summary>Helpers for <see cref="QrErrorCorrectionLevel"/>.</summary>
public static class QrErrorCorrectionLevelExtensions
{
    /// <summary>Returns the single letter name used by the specification and by tooling.</summary>
    /// <param name="level">The level.</param>
    public static string ToLetter(this QrErrorCorrectionLevel level) => level switch
    {
        QrErrorCorrectionLevel.L => "L",
        QrErrorCorrectionLevel.M => "M",
        QrErrorCorrectionLevel.Q => "Q",
        QrErrorCorrectionLevel.H => "H",
        _ => "?",
    };

    /// <summary>
    /// Returns the index of the level in the order the block tables are laid out,
    /// which is L, M, Q, H.
    /// </summary>
    /// <param name="level">The level.</param>
    public static int ToTableIndex(this QrErrorCorrectionLevel level) => level switch
    {
        QrErrorCorrectionLevel.L => 0,
        QrErrorCorrectionLevel.M => 1,
        QrErrorCorrectionLevel.Q => 2,
        _ => 3,
    };
}
