using System;
using System.Buffers;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Core.Memory
{
    /// <summary>
    /// Provides pooled buffers for temperature data to reduce GC pressure.
    /// MEMORY OPTIMIZATION: Uses ArrayPool to rent/return arrays instead of allocating new ones.
    ///
    /// Typical savings for 8 channels × 10km:
    /// - Before: 8 × 1.2 MB = 9.6 MB allocation per scan cycle
    /// - After:  ~0 MB allocation (reuses pooled arrays)
    ///
    /// Usage:
    ///   using var buffer = TemperatureDataBuffer.Rent(pointCount);
    ///   // Fill buffer.Array with data...
    ///   // Use buffer.AsSpan() or buffer.AsArray() to access data
    ///   // Buffer is automatically returned to pool on dispose
    /// </summary>
    public sealed class TemperatureDataBuffer : IDisposable
    {
        private static readonly ArrayPool<TemperatureDataPoint> Pool = ArrayPool<TemperatureDataPoint>.Shared;

        private TemperatureDataPoint[]? _array;
        private readonly int _length;
        private bool _disposed;

        /// <summary>
        /// Gets the actual length of valid data (may be less than array length).
        /// </summary>
        public int Length => _length;

        /// <summary>
        /// Gets the underlying array. May be larger than Length.
        /// </summary>
        public TemperatureDataPoint[] Array => _array ?? throw new ObjectDisposedException(nameof(TemperatureDataBuffer));

        private TemperatureDataBuffer(TemperatureDataPoint[] array, int length)
        {
            _array = array;
            _length = length;
        }

        /// <summary>
        /// Rents a buffer from the pool with at least the specified capacity.
        /// </summary>
        /// <param name="minimumLength">Minimum number of elements needed.</param>
        /// <returns>A pooled buffer that should be disposed when done.</returns>
        public static TemperatureDataBuffer Rent(int minimumLength)
        {
            if (minimumLength <= 0)
                throw new ArgumentOutOfRangeException(nameof(minimumLength));

            var array = Pool.Rent(minimumLength);
            return new TemperatureDataBuffer(array, minimumLength);
        }

        /// <summary>
        /// Gets a span over the valid portion of the buffer.
        /// </summary>
        public Span<TemperatureDataPoint> AsSpan()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TemperatureDataBuffer));
            return _array.AsSpan(0, _length);
        }

        /// <summary>
        /// Gets a read-only span over the valid portion of the buffer.
        /// </summary>
        public ReadOnlySpan<TemperatureDataPoint> AsReadOnlySpan()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TemperatureDataBuffer));
            return _array.AsSpan(0, _length);
        }

        /// <summary>
        /// Creates a new array with just the valid data. Use only when you need a standalone array.
        /// For most operations, use AsSpan() instead to avoid allocation.
        /// </summary>
        public TemperatureDataPoint[] ToArray()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(TemperatureDataBuffer));

            var result = new TemperatureDataPoint[_length];
            System.Array.Copy(_array!, result, _length);
            return result;
        }

        /// <summary>
        /// Returns the buffer to the pool.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (_array != null)
            {
                Pool.Return(_array, clearArray: false); // No need to clear structs
                _array = null;
            }
        }
    }

    /// <summary>
    /// Provides static helper methods for memory-efficient operations.
    /// </summary>
    public static class MemoryOptimization
    {
        /// <summary>
        /// Shared ArrayPool for byte buffers (WebSocket, protocol handling).
        /// </summary>
        public static ArrayPool<byte> BytePool { get; } = ArrayPool<byte>.Shared;

        /// <summary>
        /// Shared ArrayPool for temperature data points.
        /// </summary>
        public static ArrayPool<TemperatureDataPoint> TemperaturePool { get; } = ArrayPool<TemperatureDataPoint>.Shared;

        /// <summary>
        /// Rents a byte buffer from the pool.
        /// Remember to return it with BytePool.Return() when done.
        /// </summary>
        /// <param name="minimumLength">Minimum buffer size needed.</param>
        /// <returns>A pooled byte array (may be larger than requested).</returns>
        public static byte[] RentBytes(int minimumLength)
        {
            return BytePool.Rent(minimumLength);
        }

        /// <summary>
        /// Returns a rented byte buffer to the pool.
        /// </summary>
        /// <param name="buffer">The buffer to return.</param>
        /// <param name="clearArray">Whether to clear the array before returning (default: false for performance).</param>
        public static void ReturnBytes(byte[] buffer, bool clearArray = false)
        {
            if (buffer != null)
                BytePool.Return(buffer, clearArray);
        }

        /// <summary>
        /// Estimates memory usage for temperature data.
        /// </summary>
        /// <param name="channelLengthKm">Channel length in kilometers.</param>
        /// <param name="resolutionMeters">Point resolution in meters (default 0.4m).</param>
        /// <returns>Estimated bytes per scan.</returns>
        public static long EstimateMemoryUsage(double channelLengthKm, double resolutionMeters = 0.4)
        {
            int pointsPerChannel = (int)(channelLengthKm * 1000 / resolutionMeters);
            // TemperatureDataPoint is a struct with 2 doubles = 16 bytes
            return pointsPerChannel * 16;
        }
    }
}
