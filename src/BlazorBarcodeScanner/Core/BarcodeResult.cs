using System.Diagnostics.CodeAnalysis;

namespace BlazorBarcodeScanner;

/// <summary>
/// A single successfully decoded barcode.
/// </summary>
public sealed class BarcodeResult
{
    private static readonly ScanPoint[] NoPoints = [];

    internal BarcodeResult(
        string text,
        byte[] rawBytes,
        BarcodeFormat format,
        ScanPoint[]? corners,
        BarcodeResultMetadata? metadata,
        DateTimeOffset timestamp)
    {
        Text = text;
        RawBytes = rawBytes;
        Format = format;
        var points = corners ?? NoPoints;
        Corners = points;
        Metadata = metadata;
        Timestamp = timestamp;
        BoundingBox = ScanRect.FromPoints(points);
    }

    /// <summary>The decoded value, interpreted using the character set declared by the symbol.</summary>
    public string Text { get; }

    /// <summary>The raw payload bytes before character set interpretation.</summary>
    public ReadOnlyMemory<byte> RawBytes { get; }

    /// <summary>The symbology the value was read from.</summary>
    public BarcodeFormat Format { get; }

    /// <summary>When the frame that produced this result was decoded.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Corner or locator points in frame pixel coordinates. Ordering is symbology specific:
    /// matrix codes report corners clockwise from the top-left, linear codes report the two
    /// ends of the scanned line. Empty when the decoder could not supply positions.
    /// </summary>
    public IReadOnlyList<ScanPoint> Corners { get; }

    /// <summary>The axis aligned bounding box of <see cref="Corners"/>, in frame pixel coordinates.</summary>
    public ScanRect BoundingBox { get; }

    /// <summary>Symbology specific extras, or <see langword="null"/> when the symbology reports none.</summary>
    public BarcodeResultMetadata? Metadata { get; }

    /// <summary>Pixel dimensions of the frame the value was decoded from.</summary>
    public int FrameWidth { get; internal set; }

    /// <inheritdoc cref="FrameWidth" />
    public int FrameHeight { get; internal set; }

    /// <inheritdoc />
    [ExcludeFromCodeCoverage]
    public override string ToString() => $"{Format}: {Text}";
}
