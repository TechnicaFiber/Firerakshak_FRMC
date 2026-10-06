//using FireRakshak.FRMC.Core.EventAggregator;
//using FireRakshak.FRMC.Core.Models;
//using FireRakshak.FRMC.Host;
//using FireRakshak.FRMC.Infrastructure.Logging;
//using FireRakshak.FRMC.Managers.AlarmManager;
//using FireRakshak.FRMC.Managers.ChannelsManager;
//using FireRakshak.FRMC.Managers.DtsManager;
//using FireRakshak.FRMC.Managers.ExternalCommunicationManager;
//using FireRakshak.FRMC.Managers.RS232Manager;
//using FireRakshak.FRMC.Managers.SystemConfigurationManager;
//using FireRakshak.FRMC.Managers.SystemHealthManager;
//using FireRakshak.FRMC.Managers.ServiceManager;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Hosting;
//using Microsoft.Extensions.Logging;
//using Serilog.Core;
//using System;
//using System.Threading.Tasks;

//var builder = Host.CreateDefaultBuilder(args);

//// ------------------------------
//// Logging (Serilog with file output)
//// ------------------------------
//// Logs are saved to: {AppDir}/Logs/frmc-YYYYMMDD.log
//// Errors also saved to: {AppDir}/Logs/frmc-errors-YYYYMMDD.log
//// Filter relay logs: grep "[RELAY]" Logs/frmc-*.log
//builder.ConfigureSerilog();

//// ------------------------------
//// Dependency Injection
//// ------------------------------
//builder.ConfigureServices((hostContext, services) =>
//{
//    services.AddLogging();

//    // Core / Infra / Managers
//    services.AddCoreServices();
//    services.AddInfrastructureServices();
//    services.AddManagerServices(hostContext.Configuration);
//    services.AddBackgroundServices();

//    // ------------------------------
//    // REQUIRED SINGLETONS
//    // ------------------------------
//    services.AddSingleton<IEventAggregator, EventAggregator>();
//    services.AddSingleton<IConfigurationManager, ConfigurationManager>();

//    services.AddSingleton<IAlarmManager, AlarmManager>();
//    services.AddSingleton<AlarmLogger>();

//    services.AddSingleton<
//        FireRakshak.FRMC.Managers.RelayManager.IRelayControlManager,
//        FireRakshak.FRMC.Managers.RelayManager.RelayControlManager>();

//    // 🔴 IMPORTANT: RS232 MANAGER REGISTRATION
//    services.AddSingleton<IRS232Manager, RS232Manager>();

//    services.AddHostedService<SystemHealthBroadcastService>();

//    // FRMC Config
//    //services.AddSingleton(new FrmcConfiguration("FRMC01", "192.168.1.100", 5000, true));

//    // WebSocket - Control Server (port 6164)
//    services.AddSingleton<WebSocketServer>();

//    // WebSocket - Data Stream Server (port 6165) for live temperature streaming
//    // DUAL WEBSOCKET ARCHITECTURE: Separates data streaming from control commands
//    // to prevent streaming delays from affecting health status and command processing
//    services.AddSingleton<IDataStreamServer, DataStreamServer>();
//    services.AddSingleton<DtsManager>();
//    services.AddSingleton<IDtsManager>(sp => sp.GetRequiredService<DtsManager>());

//    // Network Interface Service
//    services.AddSingleton<NetworkInterfaceService>();
//});

//var host = builder.Build();
//var logger = host.Services.GetRequiredService<ILogger<Program>>();

//// ------------------------------
//// Load configuration FIRST
//// ------------------------------
//var configManager = host.Services.GetRequiredService<IConfigurationManager>();
//await configManager.LoadConfigurationsAsync();

//// ------------------------------
//// Relay initialization
//// ------------------------------
//var relayManager = host.Services.GetRequiredService<
//    FireRakshak.FRMC.Managers.RelayManager.IRelayControlManager>();

//var systemConfig = await configManager.GetSystemConfigurationAsync();
////relayManager.InitializeRelays(systemConfig?.NumberOfRelays ?? 64);
//if (systemConfig == null)
//{
//    throw new InvalidOperationException(
//        "System configuration not loaded. Cannot initialize relays.");
//}
//relayManager.InitializeRelays(systemConfig.NumberOfRelays);

//// Clear ALL relays on startup (sends RS232 OFF to hardware for every relay except Relay 1).
//// Alarms are not persisted, so relay states from the previous session are invalid.
//// The alarm evaluation loop will re-activate relays as conditions warrant.
//await relayManager.ClearAllRelaysOnStartupAsync();

//// Turn on System Running relay (Relay 1) to indicate FRMC is running
//await relayManager.SetSystemRunningRelayAsync(true);

//// ------------------------------
//// Channels initialization
//// ------------------------------
//var channelsManager = host.Services.GetRequiredService<IChannelsManager>();
//await channelsManager.InitializeChannelsAsync();

//// ------------------------------
//// INITIAL FIBER BREAK CHECK (BOOT-TIME)
//// ------------------------------
//try
//{
//    logger.LogInformation("Startup: Performing initial DTS fiber break check");

//    var dtsManager = host.Services.GetRequiredService<DtsManager>();
//    await dtsManager.ReadFiberBreakAlarmAsync();


//    logger.LogInformation("Startup: Fiber break check completed");
//}
//catch (Exception ex)
//{
//    logger.LogWarning(
//        ex,
//        "Startup: Fiber break check failed (DTS not ready). Will retry via periodic poll.");
//}
//// Start critical services with proper error handling
//// FIXED: Removed fire-and-forget pattern, added proper exception handling
//// ------------------------------
//var wsServer = host.Services.GetRequiredService<WebSocketServer>();
//var dataStreamServer = host.Services.GetRequiredService<IDataStreamServer>();
//var modbusServer = host.Services.GetRequiredService<ModbusTcpServer>();
//var rs232Manager = host.Services.GetRequiredService<IRS232Manager>();

//try
//{
//    logger.LogInformation("Starting critical services...");

//    // Start all services and await their completion
//    // DUAL WEBSOCKET: Control (6164) + Data Stream (6165)
//    var wsTask = wsServer.StartAsync();
//    var dataStreamTask = dataStreamServer.StartAsync();
//    var modbusTask = modbusServer.StartAsync("0.0.0.0", 502);
//   var rs232Task = rs232Manager.StartAsync();

//    //// Wait for all to complete or first to fail
//    //await Task.WhenAll(wsTask, dataStreamTask); //modbusTask);//rs232Task);

//    logger.LogInformation("All critical services started successfully");
//    logger.LogInformation("  - WebSocket Control server: RUNNING on port 6164");
//    logger.LogInformation("  - WebSocket Data Stream server: RUNNING on port 6165");
//    logger.LogInformation("  - Modbus TCP server: RUNNING on 0.0.0.0:502");
//    logger.LogInformation("  - RS232 Manager: RUNNING");
//}
//catch (Exception ex)
//{
//    logger.LogCritical(ex, "CRITICAL: Failed to start one or more services");
//    logger.LogCritical("Application cannot continue without all services. Shutting down...");

//    // Attempt graceful shutdown
//    try
//    {
//        await host.StopAsync(TimeSpan.FromSeconds(5));
//    }
//    catch (Exception shutdownEx)
//    {
//        logger.LogError(shutdownEx, "Error during emergency shutdown");
//    }

//    Environment.Exit(1);
//    return;
//}

//// ------------------------------
//// Graceful shutdown (IMPORTANT)
//// FIXED: Proper exception handling, no bare catch
//// ------------------------------
//var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
//lifetime.ApplicationStopping.Register(() =>
//{
//    logger.LogInformation("Application stopping - shutting down services...");

//    // Stop Data Stream Server
//    try
//    {
//        logger.LogInformation("Stopping Data Stream Server...");
//        var dataStreamStopTask = dataStreamServer.StopAsync();
//        if (!dataStreamStopTask.Wait(TimeSpan.FromSeconds(3)))
//        {
//            logger.LogWarning("Data Stream Server did not stop within timeout");
//        }
//        else
//        {
//            logger.LogInformation("Data Stream Server stopped successfully");
//        }
//    }
//    catch (Exception ex)
//    {
//        logger.LogError(ex, "Error stopping Data Stream Server during shutdown");
//    }

//    // Stop RS232 Manager
//    try
//    {
//        logger.LogInformation("Stopping RS232 Manager...");
//        var stopTask = rs232Manager.StopAsync();
//        if (!stopTask.Wait(TimeSpan.FromSeconds(5)))
//        {
//            logger.LogWarning("RS232 Manager did not stop within timeout");
//        }
//        else
//        {
//            logger.LogInformation("RS232 Manager stopped successfully");
//        }
//    }
//    catch (Exception ex)
//    {
//        logger.LogError(ex, "Error stopping RS232 Manager during shutdown");
//    }
//});
//var lifetime2 = host.Services.GetRequiredService<IHostApplicationLifetime>();

//// ------------------------------
//// Run host
//// ------------------------------
//host.Run();
//using FireRakshak.FRMC.Core.EventAggregator;
//using FireRakshak.FRMC.Core.Models;
//using FireRakshak.FRMC.Host;
//using FireRakshak.FRMC.Infrastructure.Logging;
//using FireRakshak.FRMC.Managers.AlarmManager;
//using FireRakshak.FRMC.Managers.ChannelsManager;
//using FireRakshak.FRMC.Managers.DtsManager;
//using FireRakshak.FRMC.Managers.ExternalCommunicationManager;
//using FireRakshak.FRMC.Managers.RS232Manager;
//using FireRakshak.FRMC.Managers.SystemConfigurationManager;
//using FireRakshak.FRMC.Managers.SystemHealthManager;
//using FireRakshak.FRMC.Managers.ServiceManager;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Hosting;
//using Microsoft.Extensions.Logging;
//using Serilog.Core;
//using System;
//using System.Threading.Tasks;

//var builder = Host.CreateDefaultBuilder(args);

//// ------------------------------
//// Logging (Serilog with file output)
//// ------------------------------
//builder.ConfigureSerilog();

//// ------------------------------
//// Dependency Injection
//// ------------------------------
//builder.ConfigureServices((hostContext, services) =>
//{
//    services.AddLogging();

//    // Core / Infra / Managers
//    services.AddCoreServices();
//    services.AddInfrastructureServices();
//    services.AddManagerServices(hostContext.Configuration);
//    services.AddBackgroundServices();

//    // REQUIRED SINGLETONS
//    services.AddSingleton<IEventAggregator, EventAggregator>();
//    services.AddSingleton<IConfigurationManager, ConfigurationManager>();

//    services.AddSingleton<IAlarmManager, AlarmManager>();
//    services.AddSingleton<AlarmLogger>();

//    services.AddSingleton<
//        FireRakshak.FRMC.Managers.RelayManager.IRelayControlManager,
//        FireRakshak.FRMC.Managers.RelayManager.RelayControlManager>();

//    // RS232
//    services.AddSingleton<IRS232Manager, RS232Manager>();

//    services.AddHostedService<SystemHealthBroadcastService>();

//    // WebSockets
//    services.AddSingleton<WebSocketServer>();
//    services.AddSingleton<IDataStreamServer, DataStreamServer>();

//    services.AddSingleton<DtsManager>();
//    services.AddSingleton<IDtsManager>(sp => sp.GetRequiredService<DtsManager>());

//    services.AddSingleton<NetworkInterfaceService>();
//});

//var host = builder.Build();
//var logger = host.Services.GetRequiredService<ILogger<Program>>();

//// ------------------------------
//// Load configuration FIRST
//// ------------------------------
//var configManager = host.Services.GetRequiredService<IConfigurationManager>();
//await configManager.LoadConfigurationsAsync();

//// ------------------------------
//// START RS232 FIRST (HARD DEPENDENCY)
//// ------------------------------
//var rs232Manager = host.Services.GetRequiredService<IRS232Manager>();

//logger.LogInformation("Starting RS232 Manager FIRST...");
//await rs232Manager.StartAsync();
//logger.LogInformation("RS232 Manager started successfully");

//// ------------------------------
//// Relay initialization
//// ------------------------------
//var relayManager = host.Services.GetRequiredService<
//    FireRakshak.FRMC.Managers.RelayManager.IRelayControlManager>();

//var systemConfig = await configManager.GetSystemConfigurationAsync();
//if (systemConfig == null)
//{
//    throw new InvalidOperationException(
//        "System configuration not loaded. Cannot initialize relays.");
//}

//relayManager.InitializeRelays(systemConfig.NumberOfRelays);

//// Clear ALL relays on startup
//await relayManager.ClearAllRelaysOnStartupAsync();

//// Turn on System Running relay
//await relayManager.SetSystemRunningRelayAsync(true);

//// ------------------------------
//// Channels initialization
//// ------------------------------
//var channelsManager = host.Services.GetRequiredService<IChannelsManager>();
//await channelsManager.InitializeChannelsAsync();

//// ------------------------------
//// INITIAL FIBER BREAK CHECK
//// ------------------------------
//try
//{
//    logger.LogInformation("Startup: Performing initial DTS fiber break check");
//    var dtsManager = host.Services.GetRequiredService<DtsManager>();
//    await dtsManager.ReadFiberBreakAlarmAsync();
//    logger.LogInformation("Startup: Fiber break check completed");
//}
//catch (Exception ex)
//{
//    logger.LogWarning(ex,
//        "Startup: Fiber break check failed (DTS not ready). Will retry via periodic poll.");
//}

//// ------------------------------
//// START REMAINING CRITICAL SERVICES
//// ------------------------------
//var wsServer = host.Services.GetRequiredService<WebSocketServer>();
//var dataStreamServer = host.Services.GetRequiredService<IDataStreamServer>();
//var modbusServer = host.Services.GetRequiredService<ModbusTcpServer>();

//try
//{
//    logger.LogInformation("Starting remaining critical services...");

//    await wsServer.StartAsync();
//    await dataStreamServer.StartAsync();
//    await modbusServer.StartAsync("0.0.0.0", 502);

//    logger.LogInformation("All critical services started successfully");
//    logger.LogInformation("  - RS232 Manager: RUNNING");
//    logger.LogInformation("  - WebSocket Control server: RUNNING on port 6164");
//    logger.LogInformation("  - WebSocket Data Stream server: RUNNING on port 6165");
//    logger.LogInformation("  - Modbus TCP server: RUNNING on 0.0.0.0:502");
//}
//catch (Exception ex)
//{
//    logger.LogCritical(ex, "CRITICAL: Failed to start one or more services");

//    try
//    {
//        await host.StopAsync(TimeSpan.FromSeconds(5));
//    }
//    catch { }

//    Environment.Exit(1);
//}

//// ------------------------------
//// Graceful shutdown
//// ------------------------------
//var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
//lifetime.ApplicationStopping.Register(() =>
//{
//    logger.LogInformation("Application stopping - shutting down services...");

//    try
//    {
//        dataStreamServer.StopAsync().Wait(TimeSpan.FromSeconds(3));
//    }
//    catch { }

//    try
//    {
//        rs232Manager.StopAsync().Wait(TimeSpan.FromSeconds(5));
//    }
//    catch { }
//});

//// ------------------------------
//// Run host
//// ------------------------------
//host.Run();


using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Host;
using FireRakshak.FRMC.Infrastructure.Logging;
using FireRakshak.FRMC.Managers.AlarmManager;
using FireRakshak.FRMC.Managers.ChannelsManager;
using FireRakshak.FRMC.Managers.DtsManager;
using FireRakshak.FRMC.Managers.ExternalCommunicationManager;
using FireRakshak.FRMC.Managers.RS232Manager;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using FireRakshak.FRMC.Managers.SystemHealthManager;
using FireRakshak.FRMC.Managers.ServiceManager;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using System;
using System.Threading.Tasks;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService();

// ------------------------------
// Logging (Serilog with file output)
// ------------------------------
// Logs are saved to: {AppDir}/Logs/frmc-YYYYMMDD.log
// Errors also saved to: {AppDir}/Logs/frmc-errors-YYYYMMDD.log
// Filter relay logs: grep "[RELAY]" Logs/frmc-*.log
builder.ConfigureSerilog();

// ------------------------------
// Dependency Injection
// ------------------------------
builder.ConfigureServices((hostContext, services) =>
{
    services.AddLogging();

    // Core / Infra / Managers
    services.AddCoreServices();
    services.AddInfrastructureServices();
    services.AddManagerServices(hostContext.Configuration);
    services.AddBackgroundServices();

    // ------------------------------
    // REQUIRED SINGLETONS
    // ------------------------------
    services.AddSingleton<IEventAggregator, EventAggregator>();
    services.AddSingleton<IConfigurationManager, ConfigurationManager>();

    services.AddSingleton<IAlarmManager, AlarmManager>();
    services.AddSingleton<AlarmLogger>();

    services.AddSingleton<
        FireRakshak.FRMC.Managers.RelayManager.IRelayControlManager,
        FireRakshak.FRMC.Managers.RelayManager.RelayControlManager>();

    // 🔴 IMPORTANT: RS232 MANAGER REGISTRATION
    services.AddSingleton<IRS232Manager, RS232Manager>();

    services.AddHostedService<SystemHealthBroadcastService>();

    // FRMC Config
    //services.AddSingleton(new FrmcConfiguration("FRMC01", "192.168.1.100", 5000, true));

    // WebSocket - Control Server (port 6164)
    services.AddSingleton<WebSocketServer>();

    // WebSocket - Data Stream Server (port 6165) for live temperature streaming
    // DUAL WEBSOCKET ARCHITECTURE: Separates data streaming from control commands
    // to prevent streaming delays from affecting health status and command processing
    services.AddSingleton<IDataStreamServer, DataStreamServer>();
    services.AddSingleton<DtsManager>();
    services.AddSingleton<IDtsManager>(sp => sp.GetRequiredService<DtsManager>());

    // Network Interface Service
    services.AddSingleton<NetworkInterfaceService>();
});

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

// ------------------------------
// Load configuration FIRST
// ------------------------------
var configManager = host.Services.GetRequiredService<IConfigurationManager>();
await configManager.LoadConfigurationsAsync();

// ------------------------------
// START RS232 FIRST (HARD DEPENDENCY)
// ------------------------------
var rs232Manager = host.Services.GetRequiredService<IRS232Manager>();
logger.LogInformation("Starting RS232 Manager...");
await rs232Manager.StartAsync();

// ------------------------------
// Relay initialization
// ------------------------------
var relayManager = host.Services.GetRequiredService<
    FireRakshak.FRMC.Managers.RelayManager.IRelayControlManager>();

var systemConfig = await configManager.GetSystemConfigurationAsync();
//relayManager.InitializeRelays(systemConfig?.NumberOfRelays ?? 64);
if (systemConfig == null)
{
    throw new InvalidOperationException(
        "System configuration not loaded. Cannot initialize relays.");
}
relayManager.InitializeRelays(systemConfig.NumberOfRelays);

// Clear ALL relays on startup (sends RS232 OFF to hardware for every relay except Relay 1).
// Alarms are not persisted, so relay states from the previous session are invalid.
// The alarm evaluation loop will re-activate relays as conditions warrant.
await relayManager.ClearAllRelaysOnStartupAsync();

// Turn on System Running relay (Relay 1) to indicate FRMC is running
await relayManager.SetSystemRunningRelayAsync(true);

// ------------------------------
// Channels initialization
// ------------------------------
var channelsManager = host.Services.GetRequiredService<IChannelsManager>();
await channelsManager.InitializeChannelsAsync();

// ------------------------------
// INITIAL FIBER BREAK CHECK (BOOT-TIME) - Asynchronous to prevent blocking server startup
// ------------------------------
_ = Task.Run(async () =>
{
    try
    {
        logger.LogInformation("Startup: Performing initial DTS fiber break check in background");

        var dtsManager = host.Services.GetRequiredService<DtsManager>();
        await dtsManager.ReadFiberBreakAlarmAsync();

        logger.LogInformation("Startup: Fiber break check completed");
    }
    catch (Exception ex)
    {
        logger.LogWarning(
            ex,
            "Startup: Fiber break check failed (DTS not ready). Will retry via periodic poll.");
    }
});
// Start critical services with proper error handling
// FIXED: Removed fire-and-forget pattern, added proper exception handling
// ------------------------------
var wsServer = host.Services.GetRequiredService<WebSocketServer>();
var dataStreamServer = host.Services.GetRequiredService<IDataStreamServer>();
var modbusServer = host.Services.GetRequiredService<ModbusTcpServer>();

try
{
    logger.LogInformation("Starting critical services...");

    // Start all services and await their completion
    // DUAL WEBSOCKET: Control (6164) + Data Stream (6165)
    var wsTask = wsServer.StartAsync();
    var dataStreamTask = dataStreamServer.StartAsync();
    var modbusTask = modbusServer.StartAsync("0.0.0.0", 502);

    //// Wait for all to complete or first to fail
    //await Task.WhenAll(wsTask, dataStreamTask); //modbusTask);

    logger.LogInformation("All critical services started successfully");
    logger.LogInformation("  - WebSocket Control server: RUNNING on port 6164");
    logger.LogInformation("  - WebSocket Data Stream server: RUNNING on port 6165");
    logger.LogInformation("  - Modbus TCP server: RUNNING on 0.0.0.0:502");
    logger.LogInformation("  - RS232 Manager: RUNNING");
}
catch (Exception ex)
{
    logger.LogCritical(ex, "CRITICAL: Failed to start one or more services");
    logger.LogCritical("Application cannot continue without all services. Shutting down...");

    // Attempt graceful shutdown
    try
    {
        await host.StopAsync(TimeSpan.FromSeconds(5));
    }
    catch (Exception shutdownEx)
    {
        logger.LogError(shutdownEx, "Error during emergency shutdown");
    }

    Environment.Exit(1);
    return;
}

// ------------------------------
// Graceful shutdown (IMPORTANT)
// FIXED: Proper exception handling, no bare catch
// ------------------------------
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    logger.LogInformation("Application stopping - shutting down services...");

    // Stop Data Stream Server
    try
    {
        logger.LogInformation("Stopping Data Stream Server...");
        var dataStreamStopTask = dataStreamServer.StopAsync();
        if (!dataStreamStopTask.Wait(TimeSpan.FromSeconds(3)))
        {
            logger.LogWarning("Data Stream Server did not stop within timeout");
        }
        else
        {
            logger.LogInformation("Data Stream Server stopped successfully");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error stopping Data Stream Server during shutdown");
    }

    // Stop RS232 Manager
    try
    {
        logger.LogInformation("Stopping RS232 Manager...");
        var stopTask = rs232Manager.StopAsync();
        if (!stopTask.Wait(TimeSpan.FromSeconds(5)))
        {
            logger.LogWarning("RS232 Manager did not stop within timeout");
        }
        else
        {
            logger.LogInformation("RS232 Manager stopped successfully");
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error stopping RS232 Manager during shutdown");
    }
});
var lifetime2 = host.Services.GetRequiredService<IHostApplicationLifetime>();

// ------------------------------
// Run host
// ------------------------------
host.Run();

