using FireRakshak.FRMC.Managers.ChannelsManager; // Added for IChannelsManager
using FireRakshak.FRMC.Managers.SystemConfigurationManager;
//using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Host
{
    public class ConfigurationLoaderService : IHostedService
    {
        private readonly ILogger<ConfigurationLoaderService> _logger;
        private readonly IConfigurationManager _configurationManager;
        private readonly IChannelsManager _channelsManager; 

        public ConfigurationLoaderService(ILogger<ConfigurationLoaderService> logger,
                                          IConfigurationManager configurationManager,
                                          IChannelsManager channelsManager) // Updated constructor
        {
            _logger = logger;
            _configurationManager = configurationManager;
            _channelsManager = channelsManager;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("ConfigurationLoaderService starting: Loading configurations...");
            await _configurationManager.LoadConfigurationsAsync();
            _logger.LogInformation("ConfigurationLoaderService started: Configurations loaded.");

            _logger.LogInformation("Initializing ChannelsManager with loaded configurations.");
            await _channelsManager.InitializeChannelsAsync();
            _logger.LogInformation("ChannelsManager initialized.");
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("ConfigurationLoaderService stopping.");
            return Task.CompletedTask;
        }
    }
}
