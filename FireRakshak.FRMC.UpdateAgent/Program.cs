using FireRakshak.FRMC.UpdateAgent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Register the update service
builder.Services.AddHostedService<UsbUpdateService>();

// Configuration for update paths
builder.Services.Configure<UpdateOptions>(builder.Configuration.GetSection("UpdateOptions"));

var host = builder.Build();
host.Run();

namespace FireRakshak.FRMC.UpdateAgent
{
    public class UpdateOptions
    {
        public string DeviceId { get; set; } = "12345";
        public string MountRoot { get; set; } = "/usb/"; // Common Linux mount root
        public string UpdateFileName { get; set; } = "12345.zip";
        public string ForceUpdateFileName { get; set; } = "12345_f.zip";
        public string InstallPath { get; set; } = "/home/sinnov8/FRMC_CODE";
        public string TempPath { get; set; } = "/tmp/frmc_update";
        public string MainServiceName { get; set; } = "firerakshak.service";
        public string AgentServiceName { get; set; } = "firerakshak-update.service";
        public string BackupPath { get; set; } = "/home/sinnov8/backup";
        public string LocalSecurityLogPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "security_audit.log");
        public string[] AuthorizedUsbSerialIds { get; set; } = new[] { "040177e3e701364b6b3d2a23d9cd2b7da3e603538055652b809020a8629fc3ac42d200000000000000000000008c79c3ff05671891558107b2328cd0" };
        public string AppUser { get; set; } = "sinnov8";
        public string HostExecutableName { get; set; } = "FireRakshak.FRMC.Host";
        public string ConfigFileName { get; set; } = "config.json";
    }
}
