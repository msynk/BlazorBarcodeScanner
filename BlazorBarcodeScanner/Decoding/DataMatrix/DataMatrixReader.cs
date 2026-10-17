using BlazorBarcodeScanner.Decoding.Common;
using BlazorBarcodeScanner.Imaging;

namespace BlazorBarcodeScanner.Decoding.DataMatrix;

/// <summary>
/// The Data Matrix entry point: detection followed by decoding.
/// </summary>
public sealed class DataMatrixReader : IMatrixDecoder
{
    private readonly ReedSolomonDecoder _reedSolomon = new(GenericGF.DataMatrixField256);
    private readonly DarkRegionFinder _regionFinder = new();
    private readonly DetectorScratch _scratch = new();
    private readonly Func<DetectorResult, bool> _accept;
    private DataMatrixDetector? _detector;
    private DecoderResult? _payload;

    /// <summary>Creates a reader.</summary>
    public DataMatrixReader()
    {
        // Cached once: a lambda here would allocate a closure and a delegate on every frame.
        _accept = AcceptCandidate;
    }

    private bool AcceptCandidate(DetectorResult candidate)
    {
        _payload = DecodeMatrix(candidate.Bits);
        return _payload is not null;
    }

    /// <inheritdoc />
    public BarcodeFormat Formats => BarcodeFormat.DataMatrix;

    /// <inheritdoc />
    public SymbolDecodeResult? Decode(BitMatrix image, MatrixDecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);

        var result = TryDecode(image, options.TryHarder);
        if (result is not null || !options.AllowInverted)
        {
            return result;
        }

        image.Invert();
        try
        {
            return TryDecode(image, options.TryHarder);
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

        if (!HasPlausibleBorders(matrix))
        {
            return null;
        }

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

    /// <summary>
    /// Checks the finder pattern of a sampled grid: the left column and bottom row must be
    /// solid, the top row and right column must alternate.
    /// </summary>
    /// <remarks>
    /// Reed-Solomon accepts roughly one in a million random blocks, and the detector can offer
    /// several candidate grids per frame, so a cheap structural check in front of it is what
    /// keeps noise from ever turning into a "read". A correctly sampled symbol passes with a
    /// wide margin; a few modules are allowed to be wrong for damaged or badly sampled ones.
    /// </remarks>
    public static bool HasPlausibleBorders(BitMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        var width = matrix.Width;
        var height = matrix.Height;
        if (width < 8 || height < 8)
        {
            return false;
        }

        var solidErrors = 0;
        var timingErrors = 0;
        for (var y = 0; y < height; y++)
        {
            if (!matrix[0, y])
            {
                solidErrors++;
            }

            if (matrix[width - 1, y] != ((y & 1) == 1))
            {
                timingErrors++;
            }
        }

        for (var x = 0; x < width; x++)
        {
            if (!matrix[x, height - 1])
            {
                solidErrors++;
            }

            if (matrix[x, 0] != ((x & 1) == 0))
            {
                timingErrors++;
            }
        }

        var perimeter = 2 * (width + height);
        return solidErrors * 10 <= perimeter && timingErrors * 5 <= perimeter;
    }

    private SymbolDecodeResult? TryDecode(BitMatrix image, bool tryHarder)
    {
        if (_detector is null)
        {
            _detector = new DataMatrixDetector(image, _regionFinder, _scratch);
        }
        else
        {
            _detector.Reset(image);
        }

        _payload = null;
        using var detected = _detector.Detect(tryHarder, _accept);

        return detected is null || _payload is null
            ? null
            : new SymbolDecodeResult(_payload, BarcodeFormat.DataMatrix, detected.Points);
    }
}
