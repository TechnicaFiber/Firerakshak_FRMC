using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;

namespace FireRakshak.FRMC.Managers.DtsManager;

/// <summary>
/// Defines the contract for managing DTS (Distributed Temperature Sensing) operations.
/// This interface abstracts the low-level details of communicating with a DTS device.
/// </summary>
public interface IDtsManager
{
    /// <summary>
    /// Gets whether the DTS manager is currently connected to the DTS device.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Asynchronously reads temperature data from a specific channel of the DTS device.
    /// </summary>
    /// <param name="channel">The ID of the channel to read from.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains an array of <see cref="TemperatureDataPoint"/>.</returns>
    Task<TemperatureDataPoint[]> ReadChannelTemperatureAsync(byte channel);

    /// <summary>
    /// Asynchronously reads raw temperature data bytes from a specific channel without parsing.
    /// Used by the pipeline for parallel processing - TCP read is sequential, parsing is parallel.
    /// </summary>
    /// <param name="channel">The channel ID (0-based DTS protocol channel).</param>
    /// <returns>Raw response bytes, or null if read fails.</returns>
    Task<byte[]?> ReadChannelRawDataAsync(byte channel);
    void ForceDisconnect();

    /// <summary>
    /// Asynchronously reads the health parameters of the DTS system.
    /// Health parameters may include internal temperature, laser power, etc.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation. The task result contains the <see cref="HealthParameters"/> of the DTS system.</returns>
    Task<HealthParameters> ReadHealthParametersAsync();

    /// <summary>
    /// Asynchronously configures the length of a specific DTS channel.
    /// This is important for accurate temperature measurements along the fiber optic cable.
    /// </summary>
    /// <param name="channel">The ID of the channel to configure.</param>
    /// <param name="length">The length of the fiber optic cable connected to the channel.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ConfigureChannelLengthAsync(byte channel, double length);

    /// <summary>
    /// Asynchronously configures various parameters of the DTS device.
    /// </summary>
    /// <param name="config">The <see cref="DtsConfiguration"/> object containing the parameters to apply.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task<bool> ConfigureDtsParametersAsync(DtsConfiguration config);

    //Task<bool> ConfigureFromJsonAsync(); // Will be used in future version of Software
    void ResetFiberBreakState();
    /// <summary>
    /// Asynchronously connects to the DTS device.
    /// </summary>
    /// <returns>A task that represents the asynchronous connection operation.</returns>
    Task ConnectAsync();

    /// <summary>
    /// Asynchronously disconnects from the DTS device.
    /// </summary>
    /// <returns>A task that represents the asynchronous disconnection operation.</returns>
    Task DisconnectAsync();

    /// <summary>
    /// Updates the connection configuration details for the DTS.
    /// </summary>
    /// <param name="ip">The DTS IP address.</param>
    /// <param name="port">The DTS connection port.</param>
    void UpdateDtsConnection(string ip, int port);

    /// <summary>
    /// Asynchronously reads the complete ASCII INI configuration from the DTS hardware using CMD 0x02.
    /// </summary>
    /// <returns>A tuple containing success status, raw ASCII INI payload, and error message if any.</returns>
    Task<(bool Success, string? IniPayload, string? ErrorMessage)> ReadConfigurationIniAsync();

    /// <summary>
    /// Asynchronously writes the complete modified ASCII INI configuration to the DTS hardware using CMD 0x01.
    /// </summary>
    /// <param name="iniPayload">The full ASCII INI configuration text.</param>
    /// <returns>A tuple containing success status and error message if any.</returns>
    Task<(bool Success, string? ErrorMessage)> WriteConfigurationIniAsync(string iniPayload);

    /// <summary>
    /// Asynchronously performs the complete DTS calibration write-and-readback transaction:
    /// builds and transmits CMD 0x01 WRITE with the complete ASCII INI, optionally transmits CMD 0x23 to trigger
    /// hardware coefficient calculation for the specified channel, and issues CMD 0x02 READ query to retrieve
    /// and persist the updated configuration with DTS-calculated coefficients.
    /// </summary>
    /// <param name="iniPayload">The full ASCII INI configuration text.</param>
    /// <param name="channelIndex">Optional 0-based channel index to trigger CMD 0x23 calibration calculation.</param>
    /// <returns>A tuple containing success status, updated ASCII INI payload, and error message if any.</returns>
    Task<(bool Success, string? IniPayload, string? ErrorMessage)> ApplyCalibrationAndReadBackAsync(string iniPayload, byte? channelIndex = null);
}
