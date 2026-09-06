using BlazorScanner.Imaging;

namespace BlazorScanner.Decoding.QrCode;

/// <summary>
/// The QR Code entry point: detection followed by decoding.
/// </summary>
public sealed class QrCodeReader : IMatrixDecoder
{
    private readonly QrDecoder _decoder = new();

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
        using var detected = new QrDetector(image).Detect(tryHarder);
        if (detected is null)
        {
            return null;
        }

        var payload = _decoder.Decode(detected.Bits);
        return payload is null ? null : new SymbolDecodeResult(payload, BarcodeFormat.QrCode, detected.Points);
    }
}
