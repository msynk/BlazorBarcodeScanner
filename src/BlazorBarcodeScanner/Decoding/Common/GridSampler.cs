using System.Buffers;
using BlazorBarcodeScanner.Imaging;

namespace BlazorBarcodeScanner.Decoding.Common;

/// <summary>
/// Samples a matrix symbol out of a binarised image through a <see cref="PerspectiveTransform"/>.
/// </summary>
public static class GridSampler
{
    /// <summary>
    /// Reads a <paramref name="dimensionX"/> by <paramref name="dimensionY"/> module grid from
    /// <paramref name="image"/>.
    /// </summary>
    /// <param name="image">The binarised image.</param>
    /// <param name="dimensionX">Number of modules across.</param>
    /// <param name="dimensionY">Number of modules down.</param>
    /// <param name="transform">Maps module coordinates to image coordinates.</param>
    /// <returns>The sampled matrix, or <see langword="null"/> when the grid falls outside the image.</returns>
    public static BitMatrix? Sample(BitMatrix image, int dimensionX, int dimensionY, PerspectiveTransform transform)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensionX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensionY);

        var buffer = ArrayPool<float>.Shared.Rent(dimensionX * 2);
        try
        {
            var points = buffer.AsSpan(0, dimensionX * 2);
            var result = new BitMatrix(dimensionX, dimensionY);
            var succeeded = true;

            for (var y = 0; y < dimensionY; y++)
            {
                var moduleCenterY = y + 0.5f;
                for (var x = 0; x < dimensionX; x++)
                {
                    points[x * 2] = x + 0.5f;
                    points[(x * 2) + 1] = moduleCenterY;
                }

                transform.TransformPoints(points);

                // A single out of bounds sample means the detector produced a quadrilateral that
                // does not fit the image, so the whole attempt is abandoned rather than silently
                // producing a matrix with fabricated modules.
                for (var x = 0; x < dimensionX; x++)
                {
                    var px = (int)points[x * 2];
                    var py = (int)points[(x * 2) + 1];
                    if ((uint)px >= (uint)image.Width || (uint)py >= (uint)image.Height)
                    {
                        succeeded = false;
                        break;
                    }

                    if (image[px, py])
                    {
                        result[x, y] = true;
                    }
                }

                if (!succeeded)
                {
                    break;
                }
            }

            if (!succeeded)
            {
                result.Dispose();
                return null;
            }

            return result;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }
}
