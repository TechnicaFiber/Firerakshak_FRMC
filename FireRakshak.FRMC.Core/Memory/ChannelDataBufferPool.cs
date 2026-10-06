using System;
using System.Buffers;
using System.Collections.Concurrent;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Core.Memory
{
    /// <summary>
    /// Manages pooled temperature data arrays for each DTS channel.
    ///
    /// MEMORY OPTIMIZATION: Uses ArrayPool to eliminate per-scan allocations.
    ///
    /// Lifecycle:
    /// 1. On each scan, call GetBuffer() to get a pooled array for parsing
    /// 2. The PREVIOUS scan's array is returned to the pool automatically
    /// 3. Current array remains valid until next scan starts
    ///
    /// This ensures:
    /// - Event handlers have valid data during processing
    /// - Arrays are reused across scans (zero allocation after warmup)
    /// - Automatic cleanup when channels are removed
    ///
    /// Thread Safety:
    /// - Safe for concurrent access from multiple channels
    /// - Each channel's buffer is independent
    /// </summary>
    public sealed class ChannelDataBufferPool : IDisposable
    {
        private static readonly ArrayPool<TemperatureDataPoint> Pool = ArrayPool<TemperatureDataPoint>.Shared;

        // Tracks current buffer for each channel (channelId -> buffer info)
        private readonly ConcurrentDictionary<int, BufferInfo> _channelBuffers = new();

        // Track statistics for monitoring
        private long _totalRentals = 0;
        private long _totalReturns = 0;
        private long _currentActiveBuffers = 0;

        private bool _disposed = false;

        /// <summary>
        /// Gets a pooled buffer for the specified channel.
        /// The previous buffer (if any) is automatically returned to the pool.
        /// </summary>
        /// <param name="channelId">The channel ID (1-based)</param>
        /// <param name="requiredSize">Minimum number of elements needed</param>
        /// <returns>A pooled array (may be larger than requiredSize)</returns>
        public TemperatureDataPoint[] GetBuffer(int channelId, int requiredSize)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ChannelDataBufferPool));

            if (requiredSize <= 0)
                return Array.Empty<TemperatureDataPoint>();

            // Get or create buffer info for this channel
            var newBuffer = Pool.Rent(requiredSize);
            System.Threading.Interlocked.Increment(ref _totalRentals);

            var newInfo = new BufferInfo(newBuffer, requiredSize);

            // Atomically swap buffers and get the old one
            var oldInfo = _channelBuffers.AddOrUpdate(
                channelId,
                newInfo,
                (key, existing) =>
                {
                    // Return old buffer to pool
                    if (existing.Buffer != null && existing.Buffer.Length > 0)
                    {
                        Pool.Return(existing.Buffer, clearArray: false);
                        System.Threading.Interlocked.Increment(ref _totalReturns);
                    }
                    return newInfo;
                });

            // Track active buffers
            if (oldInfo.Buffer == null)
            {
                System.Threading.Interlocked.Increment(ref _currentActiveBuffers);
            }

            return newBuffer;
        }

        /// <summary>
        /// Gets the current buffer for a channel without allocating a new one.
        /// Returns null if no buffer exists for this channel.
        /// </summary>
        public TemperatureDataPoint[]? GetCurrentBuffer(int channelId)
        {
            if (_channelBuffers.TryGetValue(channelId, out var info))
            {
                return info.Buffer;
            }
            return null;
        }

        /// <summary>
        /// Returns all buffers to the pool and clears the channel tracking.
        /// Call this when shutting down or when channels are reconfigured.
        /// </summary>
        public void ReleaseAllBuffers()
        {
            foreach (var kvp in _channelBuffers)
            {
                if (kvp.Value.Buffer != null && kvp.Value.Buffer.Length > 0)
                {
                    Pool.Return(kvp.Value.Buffer, clearArray: false);
                    System.Threading.Interlocked.Increment(ref _totalReturns);
                }
            }
            _channelBuffers.Clear();
            System.Threading.Interlocked.Exchange(ref _currentActiveBuffers, 0);
        }

        /// <summary>
        /// Releases the buffer for a specific channel.
        /// </summary>
        public void ReleaseChannelBuffer(int channelId)
        {
            if (_channelBuffers.TryRemove(channelId, out var info))
            {
                if (info.Buffer != null && info.Buffer.Length > 0)
                {
                    Pool.Return(info.Buffer, clearArray: false);
                    System.Threading.Interlocked.Increment(ref _totalReturns);
                    System.Threading.Interlocked.Decrement(ref _currentActiveBuffers);
                }
            }
        }

        /// <summary>
        /// Gets pool statistics for monitoring.
        /// </summary>
        public (long TotalRentals, long TotalReturns, long ActiveBuffers) GetStatistics()
        {
            return (_totalRentals, _totalReturns, _currentActiveBuffers);
        }

        /// <summary>
        /// Estimates memory saved by pooling based on current statistics.
        /// </summary>
        /// <param name="averagePointsPerChannel">Average data points per channel</param>
        /// <returns>Estimated bytes saved</returns>
        public long EstimateMemorySaved(int averagePointsPerChannel = 25000)
        {
            // Each rental that was returned saved an allocation
            // TemperatureDataPoint is 16 bytes (struct)
            long savedAllocations = _totalReturns;
            return savedAllocations * averagePointsPerChannel * 16;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseAllBuffers();
        }

        private readonly struct BufferInfo
        {
            public readonly TemperatureDataPoint[] Buffer;
            public readonly int RequestedSize;

            public BufferInfo(TemperatureDataPoint[] buffer, int requestedSize)
            {
                Buffer = buffer;
                RequestedSize = requestedSize;
            }
        }
    }
}
