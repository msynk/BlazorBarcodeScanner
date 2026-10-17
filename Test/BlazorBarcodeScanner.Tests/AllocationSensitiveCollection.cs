using Xunit;

namespace BlazorBarcodeScanner.Tests;

/// <summary>
/// Runs the classes that measure per-frame allocation on their own, after the parallel tests.
/// </summary>
/// <remarks>
/// The decoder rents its frame buffers from the shared array pools, and the runtime trims those
/// pools on every full collection. While hundreds of render-heavy round trips run alongside, such
/// collections happen often enough that a buffer the decoder returned is sometimes gone when it
/// is rented again, and the refill shows up as an allocation on the measuring thread. That is a
/// property of the test run, not of the decoder, so the measurements get a quiet process.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationSensitiveCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "allocation-sensitive";
}
