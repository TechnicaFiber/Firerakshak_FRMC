using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Host
{
    /// <summary>
    /// A background service that runs for the lifetime of the application.
    /// This worker can be used for general host-level tasks or simply to ensure the host remains active.
    /// </summary>
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="Worker"/> class.
        /// </summary>
        /// <param name="logger">The logger instance for this worker.</param>
        public Worker(ILogger<Worker> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// This method is called when the <see cref="IHostedService"/> starts.
        /// The implementation should be long-running and asynchronous.
        /// </summary>
        /// <param name="stoppingToken">Triggered when the application host is stopping.</param>
        /// <returns>A <see cref="Task"/> that represents the long running operations.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            while (!stoppingToken.IsCancellationRequested)
            {
                // This worker's main loop. It can be used for general host-level tasks.
                // In this project, more specific background tasks are implemented as separate Hosted Services
                // to improve separation of concerns. This worker primarily keeps the host alive.
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
