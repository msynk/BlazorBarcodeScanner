using BlazorScanner.Decoding.Common;
using BlazorScanner.Imaging;

namespace BlazorScanner.Decoding.Pdf417;

/// <summary>
/// The PDF417 entry point: row detection, row indicator interpretation, error correction and
/// decoding.
/// </summary>
/// <remarks>
/// <para>
/// The structure of a PDF417 symbol is carried by its row indicators rather than by a header: the
/// first three rows' left indicators between them encode the row count, the security level and
/// the column count. Everything else follows from those.
/// </para>
/// <para>
/// <b>Conformance.</b> This reader is only as correct as the symbol character table it is given.
/// The table that ships with the library is a generated placeholder, so
/// <see cref="Pipeline.BarcodeDecoder"/> leaves PDF417 disabled until a specification table is
/// installed through <see cref="Pdf417SymbolTable.Register"/>. See
/// <see cref="Pdf417SymbolTable"/> for why the table cannot be derived.
/// </para>
/// </remarks>
public sealed class Pdf417Reader : IMatrixDecoder
{
    private readonly Pdf417Detector _detector = new();
    private readonly Pdf417ErrorCorrection _errorCorrection = new();

    /// <inheritdoc />
    public BarcodeFormat Formats => BarcodeFormat.Pdf417;

    /// <inheritdoc />
    public SymbolDecodeResult? Decode(BitMatrix image, MatrixDecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);

        var result = TryDecode(image);
        if (result is not null || !options.AllowInverted)
        {
            return result;
        }

        image.Invert();
        try
        {
            return TryDecode(image);
        }
        finally
        {
            image.Invert();
        }
    }

    private SymbolDecodeResult? TryDecode(BitMatrix image)
    {
        var table = Pdf417SymbolTable.Current;
        var rows = _detector.Detect(image, table);

        // Three rows is the smallest legal symbol and is also the minimum needed to read the
        // three pieces of structure the row indicators carry.
        if (rows.Count < 3)
        {
            return null;
        }

        if (!TryReadStructure(rows, out var rowCount, out var columnCount, out var errorCorrectionLevel))
        {
            return null;
        }

        if (rows.Count != rowCount)
        {
            return null;
        }

        var codewords = new int[rowCount * columnCount];
        var offset = 0;
        foreach (var row in rows)
        {
            if (row.Codewords.Length != columnCount + 2)
            {
                return null;
            }

            for (var i = 1; i <= columnCount; i++)
            {
                codewords[offset++] = row.Codewords[i];
            }
        }

        var numEcCodewords = Pdf417ErrorCorrection.GetErrorCorrectionCodewordCount(errorCorrectionLevel);
        if (numEcCodewords >= codewords.Length)
        {
            return null;
        }

        if (!_errorCorrection.TryDecode(codewords, numEcCodewords, out var errorsCorrected))
        {
            return null;
        }

        var dataCodewords = codewords[..(codewords.Length - numEcCodewords)];
        var payload = Pdf417BitStreamParser.Decode(
            dataCodewords, rowCount, columnCount, errorCorrectionLevel, errorsCorrected);

        if (payload is null)
        {
            return null;
        }

        var first = rows[0];
        var last = rows[^1];
        ScanPoint[] points =
        [
            new(first.Left, first.Top),
            new(first.Right, first.Top),
            new(last.Right, last.Bottom),
            new(last.Left, last.Bottom),
        ];

        return new SymbolDecodeResult(payload, BarcodeFormat.Pdf417, points);
    }

    /// <summary>
    /// Reads the row count, column count and security level out of the row indicators.
    /// </summary>
    /// <remarks>
    /// Each row indicator carries one third of the structure, chosen by the row number modulo
    /// three, and the left and right indicators of a row carry different thirds. Reading both
    /// sides of the first three rows gives every value twice, which is what makes this robust on
    /// a symbol whose edge is partly damaged.
    /// </remarks>
    private static bool TryReadStructure(
        IReadOnlyList<Pdf417Detector.SymbolRow> rows, out int rowCount, out int columnCount, out int errorCorrectionLevel)
    {
        rowCount = 0;
        columnCount = 0;
        errorCorrectionLevel = -1;

        int? rowsDiv3 = null;
        int? rowsMod3 = null;
        int? level = null;
        int? columns = null;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var left = row.Codewords[0] % 30;
            var right = row.Codewords[^1] % 30;

            // The cluster the codeword was read from gives the row number modulo three directly,
            // without having to trust that detection started on row zero.
            var phase = row.Clusters[0] / 3;
            if (phase is < 0 or > 2)
            {
                return false;
            }

            switch (phase)
            {
                case 0:
                    rowsDiv3 ??= left;
                    columns ??= right;
                    break;
                case 1:
                    level ??= left / 3;
                    rowsMod3 ??= left % 3;
                    rowsDiv3 ??= right;
                    break;
                default:
                    columns ??= left;
                    level ??= right / 3;
                    rowsMod3 ??= right % 3;
                    break;
            }

            if (rowsDiv3 is not null && rowsMod3 is not null && level is not null && columns is not null)
            {
                break;
            }
        }

        if (rowsDiv3 is null || rowsMod3 is null || level is null || columns is null)
        {
            return false;
        }

        rowCount = (3 * rowsDiv3.Value) + rowsMod3.Value + 1;
        columnCount = columns.Value + 1;
        errorCorrectionLevel = level.Value;

        return rowCount is >= 3 and <= 90
            && columnCount is >= 1 and <= 30
            && errorCorrectionLevel is >= 0 and <= 8;
    }
}
