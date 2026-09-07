using BlazorBarcodeScanner.Decoding.Common;
using BlazorBarcodeScanner.Imaging;

namespace BlazorBarcodeScanner.Decoding.DataMatrix;

/// <summary>
/// The Data Matrix entry point: detection followed by decoding.
/// </summary>
public sealed class DataMatrixReader : IMatrixDecoder
{
    private readonly ReedSolomonDecoder _reedSolomon = new(GenericGF.DataMatrixField256);

    /// <inheritdoc />
    public BarcodeFormat Formats => BarcodeFormat.DataMatrix;

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

    /// <summary>Decodes a sampled module grid that already has its region borders.</summary>
    /// <param name="matrix">The sampled grid.</param>
    /// <returns>The payload, or <see langword="null"/> when the grid does not decode.</returns>
    public DecoderResult? DecodeMatrix(BitMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        using var parser = DataMatrixBitMatrixParser.Create(matrix);
        if (parser is null)
        {
            return null;
        }

        var codewords = parser.ReadCodewords();
        if (codewords is null)
        {
            return null;
        }

        var blocks = DataMatrixDataBlocks.Split(codewords, parser.Version);
        if (blocks is null)
        {
            return null;
        }

        var data = new byte[parser.Version.TotalDataCodewords];
        var errorsCorrected = 0;

        // Blocks are laid out so that block j owns every j-th data codeword; reassembling them
        // in the same interleaved order restores the original stream.
        foreach (var block in blocks)
        {
            var window = new int[block.Codewords.Length];
            for (var i = 0; i < window.Length; i++)
            {
                window[i] = block.Codewords[i];
            }

            if (!_reedSolomon.TryDecode(window, window.Length - block.NumDataCodewords, out var corrected))
            {
                return null;
            }

            errorsCorrected += corrected;
            for (var i = 0; i < block.NumDataCodewords; i++)
            {
                block.Codewords[i] = (byte)window[i];
            }
        }

        var offset = 0;
        var maxData = 0;
        foreach (var block in blocks)
        {
            maxData = Math.Max(maxData, block.NumDataCodewords);
        }

        for (var i = 0; i < maxData; i++)
        {
            foreach (var block in blocks)
            {
                if (i < block.NumDataCodewords)
                {
                    data[offset++] = block.Codewords[i];
                }
            }
        }

        return offset != data.Length
            ? null
            : DataMatrixBitStreamParser.Decode(data, parser.Version, errorsCorrected);
    }

    private SymbolDecodeResult? TryDecode(BitMatrix image)
    {
        using var detected = new DataMatrixDetector(image).Detect();
        if (detected is null)
        {
            return null;
        }

        var payload = DecodeMatrix(detected.Bits);
        return payload is null ? null : new SymbolDecodeResult(payload, BarcodeFormat.DataMatrix, detected.Points);
    }
}
