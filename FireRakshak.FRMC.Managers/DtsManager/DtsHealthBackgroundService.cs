using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.DtsManager;

/// <summary>
/// Background service for periodically monitoring DTS health parameters.
/// </summary>
public class DtsHealthBackgroundService : BackgroundService
{
    private readonly IDtsManager _dtsManager;
    private readonly ILogger<DtsHealthBackgroundService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(30); // Monitor every 30 seconds

    /// <summary>
    /// Initializes a new instance of the <see cref="DtsHealthBackgroundService"/> class.
    /// </summary>
    /// <param name="dtsManager">The DTS manager for reading health parameters.</param>
    /// <param name="logger">The logger for recording information.</param>
    public DtsHealthBackgroundService(IDtsManager dtsManager, ILogger<DtsHealthBackgroundService> logger)
    {
        _dtsManager = dtsManager;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DTS Health Background Service running.");

        // Run first check immediately on startup
        try
        {
            _logger.LogInformation("Performing initial DTS health check...");
            await _dtsManager.ReadHealthParametersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading DTS health parameters on startup.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, stoppingToken);

            _logger.LogDebug("DTS Health Background Service performing work.");

            try
            {
                await _dtsManager.ReadHealthParametersAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading DTS health parameters.");
            }
        }

        _logger.LogInformation("DTS Health Background Service stopping.");
    }
}
