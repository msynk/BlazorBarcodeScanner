using BlazorBarcodeScanner.Decoding.Common;

namespace BlazorBarcodeScanner.Decoding;

/// <summary>
/// What a single symbology decoder returns when it recognises a symbol.
/// </summary>
/// <param name="Payload">The recovered payload and symbol metadata.</param>
/// <param name="Format">The symbology that produced the payload.</param>
/// <param name="Points">Corner or endpoint locations in image coordinates.</param>
public sealed record SymbolDecodeResult(DecoderResult Payload, BarcodeFormat Format, ScanPoint[] Points);
