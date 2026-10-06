using System;

namespace FireRakshak.FRMC.Managers.DtsManager;

/// <summary>
/// Provides static methods to build command bytes for various DTS operations.
/// </summary>
public static class DtsCommandBuilder
{
    /// <summary>
    /// Builds the command to read all raw temperature data for a specific channel.
    /// </summary>
    /// <param name="channelId">The channel ID (0-based).</param>
    /// <returns>The command byte array.</returns>
    public static byte[] BuildReadTemperatureCommand(byte channelId)
    {
        var command = new byte[10];
        command[0] = 0xCC; // FLAG1
        command[1] = 0x55; // FLAG1
        command[2] = 0x00; // LEN
        command[3] = 0x00; // LEN
        command[4] = 0x00; // LEN
        command[5] = 0x04; // LEN (ADR + CMD + DATA + SUM)
        command[6] = 0xFF; // ADR
        command[7] = 0x07; // CMD
        command[8] = channelId; // DATA

        command[9] = CalculateChecksum(command, 9);

        return command;
    }

    /// <summary>
    /// Builds the command to read the complete DTS configuration INI (CMD 0x02).
    /// Exact 10-byte Frame: CC 55 00 00 00 04 FF 02 00 26
    /// </summary>
    /// <returns>The 10-byte read command array.</returns>
    public static byte[] BuildReadConfigurationCommand()
    {
        var command = new byte[10];
        command[0] = 0xCC; // FLAG1
        command[1] = 0x55; // FLAG1
        command[2] = 0x00; // LEN
        command[3] = 0x00; // LEN
        command[4] = 0x00; // LEN
        command[5] = 0x04; // LEN (ADR + CMD + DATA + SUM)
        command[6] = 0xFF; // ADR (Broadcast)
        command[7] = 0x02; // CMD (Read Configuration)
        command[8] = 0x00; // DATA
        command[9] = CalculateChecksum(command, 9); // 0x26

        return command;
    }

    /// <summary>
    /// Builds the command to write the complete DTS configuration INI (CMD 0x01).
    /// Protocol frame format:
    /// Bytes 0-1: 0xCC 0x55 (Header)
    /// Bytes 2-5: 4-byte big-endian LEN = ADR(1) + CMD(1) + DATA(N) + SUM(1) = N + 3
    /// Byte 6: 0xFF (Broadcast ADR)
    /// Byte 7: 0x01 (CMD Write Configuration)
    /// Bytes 8..(8+N-1): ASCII INI payload
    /// Last byte: Modulo-256 Checksum of all preceding bytes (0 to total_len-2)
    /// </summary>
    /// <param name="iniPayload">The raw ASCII INI configuration content to write.</param>
    /// <returns>Complete write command byte array.</returns>
    public static byte[] BuildWriteConfigurationCommand(string iniPayload)
    {
        if (iniPayload == null)
        {
            throw new ArgumentNullException(nameof(iniPayload));
        }

        byte[] payloadBytes = System.Text.Encoding.ASCII.GetBytes(iniPayload);
        int payloadLen = payloadBytes.Length;
        int declaredLen = payloadLen + 3; // ADR + CMD + DATA + SUM
        int totalFrameLen = 2 + 4 + declaredLen; // Header(2) + LenBytes(4) + declaredLen

        var command = new byte[totalFrameLen];

        // Header: CC 55
        command[0] = 0xCC;
        command[1] = 0x55;

        // Declared Length (4 bytes big-endian)
        command[2] = (byte)((declaredLen >> 24) & 0xFF);
        command[3] = (byte)((declaredLen >> 16) & 0xFF);
        command[4] = (byte)((declaredLen >> 8) & 0xFF);
        command[5] = (byte)(declaredLen & 0xFF);

        // Address & Command
        command[6] = 0xFF; // ADR
        command[7] = 0x01; // CMD (Write Configuration)

        // Payload (ASCII INI)
        Array.Copy(payloadBytes, 0, command, 8, payloadLen);

        // Checksum (modulo-256 sum of all bytes from index 0 to totalFrameLen - 2)
        command[totalFrameLen - 1] = CalculateChecksum(command, totalFrameLen - 1);

        return command;
    }

    private static byte CalculateChecksum(byte[] data, int length)
    {
        byte sum = 0;
        for (int i = 0; i < length; i++)
        {
            sum += data[i];
        }
        return sum;
    }

    /// <summary>
    /// Builds command bytes for DTS command 0x07 (e.g., Read Health Parameters).
    /// </summary>
    /// <returns>Command bytes.</returns>
    public static byte[] BuildReadHealthCommand()
    {
        // TODO: Implement actual command building logic
        return new byte[] { 0x07 };
    }

    /// <summary>
    /// Builds command bytes for DTS command 0x08 (e.g., Configure Channel Length).
    /// </summary>
    /// <param name="channelId">The channel ID.</param>
    /// <param name="length">The length to configure.</param>
    /// <returns>Command bytes.</returns>
    public static byte[] BuildConfigureChannelLengthCommand(byte channelId, double length)
    {
        // TODO: Implement actual command building logic
        return new byte[] { 0x08, channelId, (byte)length };
    }

    /// <summary>
    /// Builds the command to read fiber break alarm status for all channels.
    /// Command 0x0D: Read Fiber Break Alarm.
    /// </summary>
    /// <returns>The command byte array.</returns>
    public static byte[] BuildReadFiberBreakAlarmCommand()
    {
        var command = new byte[10];
        command[0] = 0xCC; // FLAG1
        command[1] = 0x55; // FLAG1
        command[2] = 0x00; // LEN
        command[3] = 0x00; // LEN
        command[4] = 0x00; // LEN
        command[5] = 0x04; // LEN (ADR + CMD + DATA + SUM)
        command[6] = 0xFF; // ADR
        command[7] = 0x0D; // CMD (Fiber Break Alarm)
        command[8] = 0x00; // DATA (0x00 for all channels)

        command[9] = CalculateChecksum(command, 9);

        return command;
    }

    /// <summary>
    /// Builds the command to read DTS module temperature (internal command 0x03 -> 0x03).
    /// Command: CC 55 00 00 00 09 FF 03 AA 55 03 FF 03 [inner_checksum] [outer_checksum]
    /// </summary>
    /// <returns>The command byte array.</returns>
    public static byte[] BuildReadModuleTemperatureCommand()
    {
        var command = new byte[15];
        command[0] = 0xCC; // FLAG1
        command[1] = 0x55; // FLAG1
        command[2] = 0x00; // LEN
        command[3] = 0x00; // LEN
        command[4] = 0x00; // LEN
        command[5] = 0x09; // LEN (9 bytes from ADR to SUM)
        command[6] = 0xFF; // ADR
        command[7] = 0x03; // CMD (internal command)
        command[8] = 0xAA; // FLAG3 (inner header)
        command[9] = 0x55; // FLAG3 (inner header)
        command[10] = 0x03; // Inner length
        command[11] = 0xFF; // Inner address
        command[12] = 0x03; // Inner CMD (module temperature)

        // Calculate inner checksum (FLAG3 + inner_len + inner_adr + inner_cmd)
        command[13] = (byte)(command[8] + command[9] + command[10] + command[11] + command[12]);

        // Calculate outer checksum
        command[14] = CalculateChecksum(command, 14);

        return command;
    }

    /// <summary>
    /// Builds the command to read DFB laser parameters (internal command 0x03 -> 0xDE).
    /// Returns DFB laser current, temperature, and status.
    /// </summary>
    /// <returns>The command byte array.</returns>
    public static byte[] BuildReadDfbLaserParametersCommand()
    {
        var command = new byte[15];
        command[0] = 0xCC; // FLAG1
        command[1] = 0x55; // FLAG1
        command[2] = 0x00; // LEN
        command[3] = 0x00; // LEN
        command[4] = 0x00; // LEN
        command[5] = 0x09; // LEN
        command[6] = 0xFF; // ADR
        command[7] = 0x03; // CMD (internal command)
        command[8] = 0xAA; // FLAG3
        command[9] = 0x55; // FLAG3
        command[10] = 0x03; // Inner length
        command[11] = 0xFF; // Inner address
        command[12] = 0xDE; // Inner CMD (DFB laser parameters)

        // Calculate inner checksum
        command[13] = (byte)(command[8] + command[9] + command[10] + command[11] + command[12]);

        // Calculate outer checksum
        command[14] = CalculateChecksum(command, 14);

        return command;
    }

    /// <summary>
    /// Builds the command to read overall DTS parameters (internal command 0x03 -> 0xDD).
    /// Returns pump current and APD voltage.
    /// </summary>
    /// <returns>The command byte array.</returns>
    public static byte[] BuildReadOverallParametersCommand()
    {
        var command = new byte[15];
        command[0] = 0xCC; // FLAG1
        command[1] = 0x55; // FLAG1
        command[2] = 0x00; // LEN
        command[3] = 0x00; // LEN
        command[4] = 0x00; // LEN
        command[5] = 0x09; // LEN
        command[6] = 0xFF; // ADR
        command[7] = 0x03; // CMD (internal command)
        command[8] = 0xAA; // FLAG3
        command[9] = 0x55; // FLAG3
        command[10] = 0x03; // Inner length
        command[11] = 0xFF; // Inner address
        command[12] = 0xDD; // Inner CMD (overall parameters)

        // Calculate inner checksum
        command[13] = (byte)(command[8] + command[9] + command[10] + command[11] + command[12]);

        // Calculate outer checksum
        command[14] = CalculateChecksum(command, 14);

        return command;
    }

    /// <summary>
    /// Builds command bytes for DTS command 0x0D (e.g., Configure DTS Parameters).
    /// </summary>
    /// <param name="pulseWidth">Pulse width.</param>
    /// <param name="pumpCurrent">Pump current.</param>
    /// <param name="channelLengths">Channel lengths.</param>
    /// <returns>Command bytes.</returns>
    public static byte[] BuildConfigureDtsParametersCommand(byte pulseWidth, ushort pumpCurrent, byte[] channelLengths)
    {
        // TODO: Implement actual command building logic
        return new byte[] { 0x0D, pulseWidth, (byte)(pumpCurrent >> 8), (byte)(pumpCurrent & 0xFF) };
    }

    /// <summary>
    /// Builds the command to set EDFA pump current (internal command 0x03 -> 0x1E).
    /// </summary>
    /// <param name="currentMa">Pump current in milliamps (mA).</param>
    /// <returns>The command byte array.</returns>
    public static byte[] BuildSetPumpCurrentCommand(double currentMa)
    {
        // Convert mA to raw value: Pump_Iop = currentMa * 10
        ushort pumpIop = (ushort)(currentMa * 10);
        byte pumpIopHigh = (byte)(pumpIop >> 8);
        byte pumpIopLow = (byte)(pumpIop & 0xFF);

        var command = new byte[17];
        command[0] = 0xCC; // FLAG1
        command[1] = 0x55; // FLAG1
        command[2] = 0x00; // LEN
        command[3] = 0x00; // LEN
        command[4] = 0x00; // LEN
        command[5] = 0x0B; // LEN (11 bytes from ADR to SUM)
        command[6] = 0xFF; // ADR
        command[7] = 0x03; // CMD (internal command)
        command[8] = 0xAA; // FLAG3 (inner header)
        command[9] = 0x55; // FLAG3 (inner header)
        command[10] = 0x05; // Inner length
        command[11] = 0xFF; // Inner address
        command[12] = 0x1E; // Inner CMD (Set pump current)
        command[13] = pumpIopHigh; // Pump_Iop high byte
        command[14] = pumpIopLow; // Pump_Iop low byte

        // Calculate inner checksum
        command[15] = (byte)(command[8] + command[9] + command[10] +
                             command[11] + command[12] + command[13] + command[14]);

        // Calculate outer checksum
        command[16] = CalculateChecksum(command, 16);

        return command;
    }

    /// <summary>
    /// Builds the command to set pulse width (internal command 0x03 -> 0xC5).
    /// </summary>
    /// <param name="pulseWidthNs">Pulse width in nanoseconds (ns). Range: 0-200.</param>
    /// <returns>The command byte array.</returns>
    public static byte[] BuildSetPulseWidthCommand(byte pulseWidthNs)
    {
        if (pulseWidthNs > 200)
        {
            throw new ArgumentException("Pulse width must be in range 0-200 ns", nameof(pulseWidthNs));
        }

        var command = new byte[16];
        command[0] = 0xCC; // FLAG1
        command[1] = 0x55; // FLAG1
        command[2] = 0x00; // LEN
        command[3] = 0x00; // LEN
        command[4] = 0x00; // LEN
        command[5] = 0x0A; // LEN (10 bytes from ADR to SUM)
        command[6] = 0xFF; // ADR
        command[7] = 0x03; // CMD (internal command)
        command[8] = 0xAA; // FLAG3 (inner header)
        command[9] = 0x55; // FLAG3 (inner header)
        command[10] = 0x04; // Inner length
        command[11] = 0xFF; // Inner address
        command[12] = 0xC5; // Inner CMD (Set pulse width)
        command[13] = pulseWidthNs; // Pulse width value

        // Calculate inner checksum
        command[14] = (byte)(command[8] + command[9] + command[10] +
                             command[11] + command[12] + command[13]);

        // Calculate outer checksum
        command[15] = CalculateChecksum(command, 15);

        return command;
    }

    // TODO: Add methods for other DTS commands: 0x13, 0x50, 0x51
}
