using System;
using System.Runtime.InteropServices;

namespace FireRakshak.FRMC.Core.Models
{
    /// <summary>
    /// Represents a single temperature reading at a specific position along the DTS fiber.
    /// OPTIMIZED: Changed from class to readonly struct to reduce memory overhead.
    /// - Class overhead: ~48 bytes per point (16 bytes object header + 8+8+8 bytes data + padding)
    /// - Struct overhead: ~16 bytes per point (8+8 bytes data, no header)
    /// For 25,000 points per channel: Saves ~800 KB per channel scan.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct TemperatureDataPoint
    {
        /// <summary>
        /// Temperature reading in degrees Celsius.
        /// </summary>
        public double Temperature { get; }

        /// <summary>
        /// Position along the fiber in meters from the start.
        /// </summary>
        public double Position { get; }

        /// <summary>
        /// Creates a new temperature data point.
        /// </summary>
        /// <param name="temperature">Temperature in degrees Celsius.</param>
        /// <param name="position">Position in meters.</param>
        public TemperatureDataPoint(double temperature, double position)
        {
            Temperature = temperature;
            Position = position;
        }

        /// <summary>
        /// Creates a default (empty) temperature data point.
        /// </summary>
        public static TemperatureDataPoint Empty => new(0, 0);

        public override string ToString() => $"[{Position:F1}m: {Temperature:F1}°C]";
    }
}
