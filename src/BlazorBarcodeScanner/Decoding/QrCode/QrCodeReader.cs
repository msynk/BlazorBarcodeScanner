using BlazorBarcodeScanner.Decoding.Common;
using BlazorBarcodeScanner.Imaging;

namespace BlazorBarcodeScanner.Decoding.QrCode;

/// <summary>
/// The QR Code entry point: detection followed by decoding.
/// </summary>
public sealed class QrCodeReader : IMatrixDecoder
{
    private readonly QrDecoder _decoder = new();
    private readonly Func<DetectorResult, bool> _accept;
    private QrDetector? _detector;
    private DecoderResult? _payload;

    /// <summary>Creates a reader.</summary>
    public QrCodeReader()
    {
        // Cached once: a lambda here would allocate a closure and a delegate on every frame.
        _accept = AcceptCandidate;
    }

    private bool AcceptCandidate(DetectorResult candidate)
    {
        _payload = _decoder.Decode(candidate.Bits);
        return _payload is not null;
    }

    /// <inheritdoc />
    public BarcodeFormat Formats => BarcodeFormat.QrCode;

    /// <inheritdoc />
    public SymbolDecodeResult? Decode(BitMatrix image, MatrixDecodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);

        var result = TryDecode(image, options.TryHarder);
        if (result is not null)
        {
            return result;
        }

        if (!options.AllowInverted)
        {
            return null;
        }

        // A light symbol on a dark background is common on screens and on some packaging. The
        // detector only understands dark on light, so the image is inverted and retried.
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

    private SymbolDecodeResult? TryDecode(BitMatrix image, bool tryHarder)
    {
        if (_detector is null)
        {
            _detector = new QrDetector(image);
        }
        else
        {
            _detector.Reset(image);
        }

        _payload = null;
        using var detected = _detector.Detect(tryHarder, _accept);

        return detected is null || _payload is null
            ? null
            : new SymbolDecodeResult(_payload, BarcodeFormat.QrCode, detected.Points);
    }
}
