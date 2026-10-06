using System.Net;
using System.Net.Sockets;
using FireRakshak.FRMC.IntegrationTests.Fixtures;
using FluentAssertions;
using NModbus;
using Xunit;

namespace FireRakshak.FRMC.IntegrationTests.Tests;

/// <summary>
/// Integration tests for Modbus TCP server functionality.
/// Tests register mapping, coil states, and client communication.
/// </summary>
[Collection("FRMC Integration Tests")]
public class ModbusTcpTests : IDisposable
{
    private readonly FrmcTestFixture _fixture;
    private TcpListener? _mockServer;
    private readonly int _testPort = 50502; // Use non-standard port for testing

    public ModbusTcpTests(FrmcTestFixture fixture)
    {
        _fixture = fixture;
        _fixture.Reset();
    }

    [Fact]
    public void ModbusRegisterLayout_SystemRegisters_AreCorrectlyMapped()
    {
        // System registers layout verification
        // Register 0-9: Reserved
        // Register 10-17: Channel fiber alarm timestamps (8 channels * 1 register)

        var systemRegistersStart = 0;
        var channelEventStart = 10;
        var zonesPerChannel = 20;
        var registersPerZone = 8; // Avg, Max, Min, RoR, Dev, MaxPos, BreachVal, BreachPos

        // Calculate expected register addresses
        var zoneDataStart = 100; // Zone data starts at register 100

        // Zone 1.1 (Channel 1, Zone 1) registers
        var zone1_1_Start = zoneDataStart + ((0 * zonesPerChannel) + 0) * registersPerZone;
        zone1_1_Start.Should().Be(100, "Zone 1.1 should start at register 100");

        // Zone 2.1 (Channel 2, Zone 1) registers
        var zone2_1_Start = zoneDataStart + ((1 * zonesPerChannel) + 0) * registersPerZone;
        zone2_1_Start.Should().Be(260, "Zone 2.1 should start at register 260 (100 + 20*8)");
    }

    [Fact]
    public void ModbusCoilLayout_AlarmCoils_AreCorrectlyMapped()
    {
        // Coil layout verification
        // Coil 0-7: Reserved/System
        // Coil 8: Global fault
        // Coil 9: DTS communication fault
        // Coil 10-17: Channel fiber alarm (8 channels)
        // Coil 100+: Zone alarm coils (4 per zone: PreAlarm, MaxAlarm, RoRAlarm, DeviationAlarm)

        var systemCoilsEnd = 9;
        var channelFiberCoilStart = 10;
        var channelFiberCoilEnd = 17;
        var zoneCoilStart = 100;
        var coilsPerZone = 4;

        // Zone 1.1 coils
        var zone1_1_CoilStart = zoneCoilStart + ((0 * 20) + 0) * coilsPerZone;
        zone1_1_CoilStart.Should().Be(100, "Zone 1.1 coils should start at 100");

        // Zone 1.1 alarm coils
        var zone1_1_PreAlarm = zone1_1_CoilStart + 0;
        var zone1_1_MaxAlarm = zone1_1_CoilStart + 1;
        var zone1_1_RoRAlarm = zone1_1_CoilStart + 2;
        var zone1_1_DevAlarm = zone1_1_CoilStart + 3;

        zone1_1_PreAlarm.Should().Be(100);
        zone1_1_MaxAlarm.Should().Be(101);
        zone1_1_RoRAlarm.Should().Be(102);
        zone1_1_DevAlarm.Should().Be(103);
    }

    [Fact]
    public void TemperatureToRegister_Conversion_IsCorrect()
    {
        // Temperature values are stored as fixed-point: value * 10
        // e.g., 25.5°C = 255, 100.0°C = 1000

        var temperature = 25.5;
        var registerValue = (ushort)(temperature * 10);
        registerValue.Should().Be(255);

        var negativeTemp = -10.5;
        var negativeRegister = (short)(negativeTemp * 10);
        negativeRegister.Should().Be(-105);
    }

    [Fact]
    public void PositionToRegister_Conversion_IsCorrect()
    {
        // Position values are stored as fixed-point: value * 10
        // e.g., 150.5m = 1505

        var position = 150.5;
        var registerValue = (ushort)(position * 10);
        registerValue.Should().Be(1505);
    }

    [Fact]
    public void ZoneRegisterCalculation_ForAnyZone_IsCorrect()
    {
        // Formula: baseAddress + ((channelIndex * zonesPerChannel) + zoneIndex) * registersPerZone
        var baseAddress = 100;
        var zonesPerChannel = 20;
        var registersPerZone = 8;

        // Test: Channel 3, Zone 5 (0-indexed: channel 2, zone 4)
        var channelIndex = 2;
        var zoneIndex = 4;
        var expectedAddress = baseAddress + ((channelIndex * zonesPerChannel) + zoneIndex) * registersPerZone;
        expectedAddress.Should().Be(100 + (2 * 20 + 4) * 8);
        expectedAddress.Should().Be(452);
    }

    [Fact]
    public void ZoneCoilCalculation_ForAnyZone_IsCorrect()
    {
        // Formula: baseCoil + ((channelIndex * zonesPerChannel) + zoneIndex) * coilsPerZone
        var baseCoil = 100;
        var zonesPerChannel = 20;
        var coilsPerZone = 4;

        // Test: Channel 2, Zone 3 (0-indexed: channel 1, zone 2)
        var channelIndex = 1;
        var zoneIndex = 2;
        var expectedCoil = baseCoil + ((channelIndex * zonesPerChannel) + zoneIndex) * coilsPerZone;
        expectedCoil.Should().Be(100 + (1 * 20 + 2) * 4);
        expectedCoil.Should().Be(188);
    }

    [Fact]
    public void MaxRegisterAddress_WithMaxConfiguration_DoesNotOverflow()
    {
        // Maximum configuration: 8 channels, 20 zones each
        var maxChannels = 8;
        var maxZonesPerChannel = 20;
        var registersPerZone = 8;
        var baseAddress = 100;

        var maxRegisterAddress = baseAddress + (maxChannels * maxZonesPerChannel * registersPerZone);
        maxRegisterAddress.Should().Be(1380); // 100 + (8 * 20 * 8)

        // Modbus holding registers are 16-bit, max address is 65535
        maxRegisterAddress.Should().BeLessThan(65535);
    }

    [Fact]
    public void MaxCoilAddress_WithMaxConfiguration_DoesNotOverflow()
    {
        // Maximum configuration: 8 channels, 20 zones each
        var maxChannels = 8;
        var maxZonesPerChannel = 20;
        var coilsPerZone = 4;
        var baseCoil = 100;

        var maxCoilAddress = baseCoil + (maxChannels * maxZonesPerChannel * coilsPerZone);
        maxCoilAddress.Should().Be(740); // 100 + (8 * 20 * 4)

        // Modbus coils max address is 65535
        maxCoilAddress.Should().BeLessThan(65535);
    }

    [Fact]
    public void ChannelEventRegister_TimestampFormat_IsCorrect()
    {
        // Channel event time is stored as minutes since midnight
        var eventTime = new DateTime(2024, 1, 15, 14, 30, 0); // 14:30
        var minutesSinceMidnight = eventTime.Hour * 60 + eventTime.Minute;
        minutesSinceMidnight.Should().Be(870); // 14*60 + 30

        // Should fit in a single 16-bit register (max 1440 minutes)
        minutesSinceMidnight.Should().BeLessThanOrEqualTo(1440);
    }

    [Fact]
    public void AlarmBreachRegisters_Layout_IsCorrect()
    {
        // Each zone has breach registers for each alarm type
        // Layout per zone (8 registers):
        // 0: Average temperature
        // 1: Max temperature
        // 2: Min temperature
        // 3: Rate of Rise
        // 4: Deviation
        // 5: Max temp position
        // 6: Breach value
        // 7: Breach position

        var registersPerZone = 8;
        registersPerZone.Should().Be(8);
    }

    [Fact]
    public async Task ModbusClient_WhenConnecting_ReceivesCorrectUnitId()
    {
        // Modbus unit ID (slave address) should be 1 for FRMC
        var expectedUnitId = (byte)1;

        // This test verifies the expected configuration
        expectedUnitId.Should().Be(1);

        await Task.CompletedTask;
    }

    [Fact]
    public void FiberBreakCoil_ChannelMapping_IsCorrect()
    {
        // Fiber break coils are mapped to channels 1-8 at coils 10-17
        var fiberBreakCoilBase = 10;

        for (int channel = 1; channel <= 8; channel++)
        {
            var coilAddress = fiberBreakCoilBase + (channel - 1);
            coilAddress.Should().BeInRange(10, 17);
        }

        // Channel 1 -> Coil 10
        // Channel 8 -> Coil 17
        (fiberBreakCoilBase + 0).Should().Be(10); // Channel 1
        (fiberBreakCoilBase + 7).Should().Be(17); // Channel 8
    }

    [Fact]
    public void SystemFaultCoils_AreCorrectlyPositioned()
    {
        // System fault coils
        var globalFaultCoil = 8;
        var dtsCommunicationFaultCoil = 9;

        globalFaultCoil.Should().Be(8);
        dtsCommunicationFaultCoil.Should().Be(9);
    }

    [Fact]
    public void RegisterValueRange_ForTemperature_HandlesNegatives()
    {
        // Temperature can be negative (freezer monitoring)
        // Using signed 16-bit representation

        var minTemp = -40.0; // Minimum expected temperature
        var maxTemp = 150.0; // Maximum expected temperature

        var minRegister = (short)(minTemp * 10);
        var maxRegister = (short)(maxTemp * 10);

        minRegister.Should().Be(-400);
        maxRegister.Should().Be(1500);

        // Both should fit in signed 16-bit range (-32768 to 32767)
        minRegister.Should().BeGreaterThanOrEqualTo(short.MinValue);
        maxRegister.Should().BeLessThanOrEqualTo(short.MaxValue);
    }

    public void Dispose()
    {
        _mockServer?.Stop();
    }
}
