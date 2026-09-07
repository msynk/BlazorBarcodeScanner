using BlazorBarcodeScanner.Decoding;
using BlazorBarcodeScanner.Decoding.DataMatrix;
using BlazorBarcodeScanner.Decoding.OneD;
using BlazorBarcodeScanner.Decoding.Pdf417;
using BlazorBarcodeScanner.Decoding.QrCode;
using BlazorBarcodeScanner.Imaging;

namespace BlazorBarcodeScanner.Pipeline;

/// <summary>
/// The decoding pipeline: takes a frame of pixels and returns the barcode it contains.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole library minus the browser. It has no dependency on Blazor, JavaScript or a
/// camera, which is what lets it be unit tested, benchmarked and reused from a server or a
/// desktop application.
/// </para>
/// <para>
/// One instance owns all the scratch state a frame needs: the luminance plane, the packed scan
/// line and the per-symbology decoders. After the first frame, decoding a frame that contains no
/// barcode allocates nothing at all, which is what keeps continuous scanning free of garbage
/// collection pauses. An instance is therefore not thread safe; use one per scanning session.
/// </para>
/// </remarks>
public sealed class BarcodeDecoder : IDisposable
{
    private readonly ScannerOptions _options;
    private readonly List<IRowDecoder> _rowDecoders = [];
    private readonly List<IMatrixDecoder> _matrixDecoders = [];
    private readonly HybridBinarizer _matrixBinarizer = new();
    private readonly GlobalHistogramBinarizer _rowBinarizer = new();
    private readonly DuplicateFilter _duplicateFilter;

    private LuminanceBuffer? _grayscale;
    private BitRow? _scanLine;
    private bool _disposed;

    /// <summary>Creates a decoder.</summary>
    /// <param name="options">Tuning; a copy is taken, so later edits to the instance are ignored.</param>
    public BarcodeDecoder(ScannerOptions? options = null)
    {
        options ??= new ScannerOptions();
        options.Validate();
        _options = options.Clone();
        _duplicateFilter = new DuplicateFilter(_options.DuplicateSuppressionWindow);

        // Only the decoders that can produce a requested format are instantiated, so a scanner
        // configured for a single symbology carries no cost for the others.
        if ((_options.Formats & BarcodeFormat.QrCode) != 0)
        {
            _matrixDecoders.Add(new QrCodeReader());
        }

        if ((_options.Formats & BarcodeFormat.DataMatrix) != 0)
        {
            _matrixDecoders.Add(new DataMatrixReader());
        }

        // PDF417 is only wired up once a specification symbol character table has been
        // installed; see Pdf417SymbolTable for why the shipped table cannot be conformant.
        if ((_options.Formats & BarcodeFormat.Pdf417) != 0 && Pdf417SymbolTable.Current.IsSpecificationTable)
        {
            _matrixDecoders.Add(new Pdf417Reader());
        }

        if ((_options.Formats & BarcodeFormat.Code128) != 0)
        {
            _rowDecoders.Add(new Code128Reader());
        }

        if ((_options.Formats & BarcodeFormat.Code39) != 0)
        {
            _rowDecoders.Add(new Code39Reader());
        }

        if ((_options.Formats & (BarcodeFormat.Ean13 | BarcodeFormat.Ean8 | BarcodeFormat.UpcA | BarcodeFormat.UpcE)) != 0)
        {
            _rowDecoders.Add(new EanUpcReader());
        }

        if ((_options.Formats & BarcodeFormat.Itf) != 0)
        {
            _rowDecoders.Add(new ItfReader());
        }
    }

    /// <summary>The options this decoder was created with.</summary>
    public ScannerOptions Options => _options;

    /// <summary>
    /// How long the same value is suppressed after being reported. Can be changed while running.
    /// </summary>
    public TimeSpan DuplicateSuppressionWindow
    {
        get => _duplicateFilter.Window;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value.Ticks);
            _options.DuplicateSuppressionWindow = value;
            _duplicateFilter.Window = value;
        }
    }

    /// <summary>Timings from the most recently decoded frame.</summary>
    public ScanDiagnostics LastFrame { get; private set; }

    /// <summary>Number of frames decoded since the decoder was created.</summary>
    public long FramesDecoded { get; private set; }

    /// <summary>Number of frames that produced a result, before duplicate suppression.</summary>
    public long FramesWithSymbol { get; private set; }

    /// <summary>Forgets the last reported value so that scanning it again reports immediately.</summary>
    public void ResetDuplicateFilter() => _duplicateFilter.Reset();

    /// <summary>
    /// Decodes a frame of packed pixels.
    /// </summary>
    /// <param name="pixels">The frame, row major and tightly packed.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="format">Layout of <paramref name="pixels"/>.</param>
    /// <param name="suppressDuplicates">When <see langword="false"/>, the duplicate filter is bypassed.</param>
    /// <returns>The decoded barcode, or <see langword="null"/>.</returns>
    public BarcodeResult? Decode(
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        PixelFormat format,
        bool suppressDuplicates = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var start = Stopwatch.GetTimestamp();

        var step = _options.DownsampleFactor;
        var targetWidth = width / step;
        var targetHeight = height / step;
        if (targetWidth < 8 || targetHeight < 8)
        {
            throw new ArgumentException(
                "The frame is too small to decode after downsampling.", nameof(pixels));
        }

        if (_grayscale is null)
        {
            _grayscale = LuminanceBuffer.Rent(targetWidth, targetHeight);
        }
        else
        {
            _grayscale.Resize(targetWidth, targetHeight);
        }

        PixelConverter.ToGrayscaleDownsampled(pixels, _grayscale.Pixels, width, height, format, step);
        var afterGrayscale = Stopwatch.GetTimestamp();

        var result = DecodeCore(_grayscale.View, start, afterGrayscale, width, height, step, suppressDuplicates);
        return result;
    }

    /// <summary>
    /// Decodes a frame that is already 8 bit grayscale.
    /// </summary>
    /// <param name="view">The grayscale frame.</param>
    /// <param name="suppressDuplicates">When <see langword="false"/>, the duplicate filter is bypassed.</param>
    public BarcodeResult? Decode(in LuminanceView view, bool suppressDuplicates = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var start = Stopwatch.GetTimestamp();
        return DecodeCore(view, start, start, view.Width, view.Height, 1, suppressDuplicates);
    }

    private BarcodeResult? DecodeCore(
        in LuminanceView source,
        long start,
        long afterGrayscale,
        int frameWidth,
        int frameHeight,
        int step,
        bool suppressDuplicates)
    {
        FramesDecoded++;

        var region = _options.Region;
        var view = region.IsFull ? source : source.Crop(region);
        var offsetX = ((view.Offset - source.Offset) % source.Stride) * step;
        var offsetY = ((view.Offset - source.Offset) / source.Stride) * step;

        var binarizeTicks = 0L;
        var matrixTicks = 0L;
        var linearTicks = 0L;
        var rowsScanned = 0;

        SymbolDecodeResult? symbol = null;

        if (_matrixDecoders.Count > 0 && view.Width >= 16 && view.Height >= 16)
        {
            var beforeBinarize = Stopwatch.GetTimestamp();
            using var binary = _matrixBinarizer.GetBlackMatrix(view);
            var afterBinarize = Stopwatch.GetTimestamp();
            binarizeTicks = afterBinarize - beforeBinarize;

            if (binary is not null)
            {
                var matrixOptions = new MatrixDecodeOptions(_options.TryHarder, _options.AllowInverted);
                foreach (var decoder in _matrixDecoders)
                {
                    symbol = decoder.Decode(binary, matrixOptions);
                    if (symbol is not null)
                    {
                        break;
                    }
                }
            }

            matrixTicks = Stopwatch.GetTimestamp() - afterBinarize;
        }

        if (symbol is null && _rowDecoders.Count > 0)
        {
            var beforeLinear = Stopwatch.GetTimestamp();
            symbol = ScanRows(view, out rowsScanned);
            linearTicks = Stopwatch.GetTimestamp() - beforeLinear;
        }

        var total = Stopwatch.GetTimestamp() - start;
        LastFrame = new ScanDiagnostics
        {
            DecodedWidth = view.Width,
            DecodedHeight = view.Height,
            Total = Stopwatch.GetElapsedTime(0, total),
            Grayscale = Stopwatch.GetElapsedTime(start, afterGrayscale),
            Binarize = Stopwatch.GetElapsedTime(0, binarizeTicks),
            Matrix = Stopwatch.GetElapsedTime(0, matrixTicks),
            Linear = Stopwatch.GetElapsedTime(0, linearTicks),
            RowsScanned = rowsScanned,
            Decoded = symbol is not null,
        };

        if (symbol is null)
        {
            return null;
        }

        FramesWithSymbol++;

        var timestamp = Environment.TickCount64;
        if (suppressDuplicates && !_duplicateFilter.ShouldReport(symbol.Payload.Text, symbol.Format, timestamp))
        {
            return null;
        }

        // Decoder coordinates are relative to the cropped, downsampled image; map them back to
        // the coordinates of the frame the caller handed in.
        var points = new ScanPoint[symbol.Points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new ScanPoint(
                (symbol.Points[i].X * step) + offsetX,
                (symbol.Points[i].Y * step) + offsetY);
        }

        return new BarcodeResult(
            symbol.Payload.Text,
            symbol.Payload.RawBytes,
            symbol.Format,
            points,
            symbol.Payload.ToMetadata(),
            DateTimeOffset.UtcNow)
        {
            FrameWidth = frameWidth,
            FrameHeight = frameHeight,
        };
    }

    /// <summary>
    /// Scans a spread of rows for linear symbols, working outwards from the middle of the region
    /// because that is where an aiming user puts the symbol.
    /// </summary>
    private SymbolDecodeResult? ScanRows(in LuminanceView view, out int rowsScanned)
    {
        rowsScanned = 0;

        var height = view.Height;
        var maxLines = Math.Min(_options.MaxScanLines, height);
        var middle = height / 2;
        var rowStep = Math.Max(1, height / (maxLines + 1));

        _scanLine ??= new BitRow(view.Width);

        for (var attempt = 0; attempt < maxLines; attempt++)
        {
            // 0, +1, -1, +2, -2 ... in units of rowStep.
            var delta = ((attempt + 1) / 2) * rowStep;
            var row = (attempt & 1) == 0 ? middle + delta : middle - delta;
            if ((uint)row >= (uint)height)
            {
                continue;
            }

            rowsScanned++;

            if (!_rowBinarizer.TryGetBlackRow(view, row, _scanLine))
            {
                continue;
            }

            foreach (var decoder in _rowDecoders)
            {
                var result = decoder.DecodeRow(row, _scanLine, _options.Formats);
                if (result is not null)
                {
                    return result;
                }
            }

            if (!_options.TryReversedRows)
            {
                continue;
            }

            // Retry the same line read right to left; several symbologies are only recognisable
            // in one direction and this costs nothing compared to binarising another row.
            _scanLine.Reverse();
            foreach (var decoder in _rowDecoders)
            {
                var result = decoder.DecodeRow(row, _scanLine, _options.Formats);
                if (result is null)
                {
                    continue;
                }

                var mirrored = new ScanPoint[result.Points.Length];
                for (var i = 0; i < mirrored.Length; i++)
                {
                    mirrored[i] = new ScanPoint(view.Width - result.Points[i].X - 1, result.Points[i].Y);
                }

                return result with { Points = mirrored };
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _grayscale?.Dispose();
        _grayscale = null;
        _scanLine = null;
    }
}
