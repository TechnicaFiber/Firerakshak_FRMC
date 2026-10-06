
using FRMC.HighAvailability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Enable Windows Service
builder.Services.AddWindowsService();

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddEventLog();   // For service
builder.Logging.AddConsole();    // Optional (ignored in service)

// Worker
builder.Services.AddHostedService<Worker>();

builder.Build().Run();
