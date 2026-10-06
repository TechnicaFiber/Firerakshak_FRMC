using FireRakshak.FRMC.Core.EventAggregator;
using FireRakshak.FRMC.Core.Models;
using FireRakshak.FRMC.Infrastructure.Configuration;
using FireRakshak.FRMC.Infrastructure.Logging;
using FireRakshak.FRMC.Infrastructure.Persistence;
using FireRakshak.FRMC.Infrastructure.Security;
using FireRakshak.FRMC.Infrastructure.Services;
using FireRakshak.FRMC.Managers.AlarmManager;
using FireRakshak.FRMC.Managers.ChannelsManager;
using FireRakshak.FRMC.Managers.DtsManager;
using FireRakshak.FRMC.Managers.ExternalCommunicationManager;
using FireRakshak.FRMC.Managers.ExternalInterfacesManager;
using FireRakshak.FRMC.Managers.RS232Manager;
using FireRakshak.FRMC.Managers.ServiceManager;
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
using FireRakshak.FRMC.Managers.SystemHealthManager;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Generic;

namespace FireRakshak.FRMC.Host
{
    /// <summary>
    /// Provides extension methods for IServiceCollection to register application services.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddCoreServices(this IServiceCollection services)
        {
            services.AddSingleton<IEventAggregator, EventAggregator>();
            return services;
        }

        public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
        {
            services.AddSingleton<ConfigurationLoader>();
            services.AddSingleton<ConfigurationValidator>();
            services.AddSingleton<FileSystemPersistence>();
            services.AddSingleton<JwtTokenHandler>();
            services.AddSingleton<PasswordHasher>();
            services.AddSingleton<UserStore>();
            services.AddSingleton<ClockService>();
            return services;
        }

        /// <summary>
        /// Registers manager services and wires up implementations.
        /// NOTE: Fully-qualified names are used in a few places to avoid ambiguity with Microsoft.Extensions.Configuration.IConfigurationManager.
        /// </summary>
        public static IServiceCollection AddManagerServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Options
            services.Configure<DtsOptions>(configuration.GetSection("Dts"));
            services.Configure<NetworkAdapterOptions>(configuration.GetSection("NetworkAdapters"));
            services.Configure<List<ChannelConfiguration>>(configuration.GetSection("ChannelConfigurations"));

            // DTS
            services.AddSingleton<IDtsManager, DtsManager>();
            services.AddSingleton<DtsProtocolHandler>();
            services.AddSingleton<DtsDataPipeline>(); // Producer-consumer pipeline for parallel processing

            // RS232
            services.AddSingleton<IRS232Manager, RS232Manager>();

            // Alarm (registered before ChannelsManager since ChannelsManager depends on IAlarmManager)
            services.AddSingleton<IAlarmManager, AlarmManager>();
            services.AddSingleton<AlarmLogger>();

            // Channel/Zone services
            services.AddSingleton<IChannelsManager, ChannelsManager>();
            services.AddSingleton<ZoneConfigurationService>();
            services.AddSingleton<ThresholdComparator>();

            // System health
            services.AddSingleton<ISystemHealthManager, SystemHealthManager>();
            services.AddSingleton<HealthMonitoringService>();

            // ----- SYSTEM / CHANNEL CONFIGURATION MANAGERS -----
            // Register your "channel + zone" ConfigurationManager implementation for:
            //   FireRakshak.FRMC.Managers.SystemConfigurationManager.IConfigurationManager
            services.AddSingleton<FireRakshak.FRMC.Managers.SystemConfigurationManager.IConfigurationManager,
                                  FireRakshak.FRMC.Managers.SystemConfigurationManager.ConfigurationManager>();

            // Register the separate ISystemConfigurationManager (if you have a distinct implementation)
            // If your SystemConfigurationManager class name is "SystemConfigurationManager", register it here.
            // If you implemented system config methods in the same ConfigurationManager, you can still register a wrapper
            // or the same class for the ISystemConfigurationManager interface as appropriate.
            services.AddSingleton<FireRakshak.FRMC.Managers.SystemConfigurationManager.ISystemConfigurationManager,
                                  FireRakshak.FRMC.Managers.SystemConfigurationManager.SystemConfigurationManager>();

            // Audit / persistence used by configuration managers
            services.AddSingleton<AuditTrailService>();
            services.AddSingleton<EventHistoryService>();
            services.AddSingleton<SystemHealthFaultLogger>();
            services.AddSingleton<ConfigurationPersistence>();

            // External Interfaces Manager
            // This factory ensures the right configuration-manager instance (the project's IConfigurationManager) is injected.
            services.AddSingleton<IExternalInterfacesManager, ExternalInterfacesManager>(provider =>
                new ExternalInterfacesManager(
                    provider.GetRequiredService<IAlarmManager>(),
                    provider.GetRequiredService<ISystemHealthManager>(),
                    // IMPORTANT: resolve the project's IConfigurationManager (fully-qualified)
                    provider.GetRequiredService<FireRakshak.FRMC.Managers.SystemConfigurationManager.IConfigurationManager>(),
                    provider.GetRequiredService<ClockService>(),
                    provider.GetRequiredService<ILogger<ExternalInterfacesManager>>(),
                    provider.GetRequiredService<IOptions<NetworkAdapterOptions>>(),
                    configuration,
                    provider.GetRequiredService<IEventAggregator>(),
                    provider.GetRequiredService<NetworkInterfaceService>()
                ));

            // ServiceManager and related
            services.AddSingleton<IServiceManager, ServiceManager>();
            services.AddSingleton<WebSocketAuthenticationHandler>();
            services.AddSingleton<LiveDataStreamingService>();

            // External communication stack
            services.AddSingleton<IExternalCommunicationManager, ExternalCommunicationManager>();
            services.AddSingleton<ModbusTcpServer>();
            services.AddSingleton<ModbusRegisterMapper>();
            services.AddSingleton<WebSocketPublicServer>();

            return services;
        }

        public static IServiceCollection AddBackgroundServices(this IServiceCollection services)
        {
            services.AddHostedService<Worker>();
            services.AddHostedService<DtsBackgroundService>();
            services.AddHostedService<DtsHealthBackgroundService>();
            services.AddHostedService<AlarmPublisherService>();
            services.AddHostedService<ZipArchivingRetentionService>();
            services.AddHostedService<LiveDataStreamingService>(provider => provider.GetRequiredService<LiveDataStreamingService>());
            return services;
        }
    }
}
