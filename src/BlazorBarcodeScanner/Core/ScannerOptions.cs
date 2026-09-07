namespace BlazorBarcodeScanner;

/// <summary>
/// Tuning for the decoding pipeline.
/// </summary>
/// <remarks>
/// Every setting here trades latency against the range of symbols that can be read. The defaults
/// are chosen for live camera scanning on a mid-range phone: they keep a frame under a few
/// milliseconds so that the pipeline never becomes the bottleneck, at the cost of needing the
/// symbol to be reasonably presented. <see cref="ForStillImages"/> flips those trade-offs for
/// one-shot decoding, where spending fifty times as long on a single image is free.
/// </remarks>
public sealed class ScannerOptions
{
    /// <summary>
    /// The symbologies to look for. Narrowing this is the single most effective way to make
    /// scanning faster and to avoid misreads between similar linear symbologies.
    /// </summary>
    public BarcodeFormat Formats { get; set; } = BarcodeFormat.All;

    /// <summary>
    /// The part of the frame to search, in normalized coordinates. Restricting the region
    /// reduces the work of every stage proportionally and lets the user aim.
    /// </summary>
    public ScanRegion Region { get; set; } = ScanRegion.Full;

    /// <summary>
    /// How many scan lines to try per frame when looking for linear symbologies.
    /// </summary>
    /// <remarks>
    /// Lines are spread outwards from the middle of the region, so a low value still finds a
    /// symbol the user has aimed at, while a high value finds one anywhere in frame.
    /// </remarks>
    public int MaxScanLines { get; set; } = 15;

    /// <summary>
    /// Subsampling applied while converting a frame to grayscale. A value of 2 quarters the
    /// number of pixels every later stage touches, at the cost of the smallest readable symbol.
    /// </summary>
    public int DownsampleFactor { get; set; } = 1;

    /// <summary>
    /// Lets the matrix decoders spend significantly more time per frame, for example by scanning
    /// every row for finder patterns rather than a strided subset.
    /// </summary>
    public bool TryHarder { get; set; }

    /// <summary>
    /// Also try a light-on-dark reading of matrix symbologies. Roughly doubles the cost of a
    /// frame that contains no symbol.
    /// </summary>
    public bool AllowInverted { get; set; }

    /// <summary>
    /// Also try reading linear symbols right to left. Cheap, and required for symbols that are
    /// presented upside down.
    /// </summary>
    public bool TryReversedRows { get; set; } = true;

    /// <summary>
    /// How long the same value from the same symbology is suppressed after being reported.
    /// Set to <see cref="TimeSpan.Zero"/> to report every successful frame.
    /// </summary>
    public TimeSpan DuplicateSuppressionWindow { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Creates a copy, so that a running scanner is not affected by later edits.</summary>
    public ScannerOptions Clone() => new()
    {
        Formats = Formats,
        Region = Region,
        MaxScanLines = MaxScanLines,
        DownsampleFactor = DownsampleFactor,
        TryHarder = TryHarder,
        AllowInverted = AllowInverted,
        TryReversedRows = TryReversedRows,
        DuplicateSuppressionWindow = DuplicateSuppressionWindow,
    };

    /// <summary>Defaults tuned for decoding a single still image, where latency does not matter.</summary>
    public static ScannerOptions ForStillImages() => new()
    {
        MaxScanLines = 60,
        TryHarder = true,
        AllowInverted = true,
        DuplicateSuppressionWindow = TimeSpan.Zero,
    };

    /// <summary>Throws when a value is outside its supported range.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxScanLines, nameof(MaxScanLines));
        ArgumentOutOfRangeException.ThrowIfLessThan(DownsampleFactor, 1, nameof(DownsampleFactor));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(DownsampleFactor, 8, nameof(DownsampleFactor));
        ArgumentOutOfRangeException.ThrowIfNegative(DuplicateSuppressionWindow.Ticks, nameof(DuplicateSuppressionWindow));
    }
}
