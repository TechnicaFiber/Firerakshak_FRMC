using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FireRakshak.FRMC.Core.Models;
using Microsoft.Extensions.Logging;

namespace FireRakshak.FRMC.Managers.ServiceManager
{
    public static class UsbConfigHandler
    {
        private const string ResponseType = "USBConfigurationCommand_response";

        public static async Task HandleCommandAsync(
            WebSocketMessage message, 
            WebSocket webSocket, 
            Func<WebSocket, WebSocketMessage, CancellationToken, Task> sendResponse,
            ILogger logger,
            CancellationToken ct)
        {
            try
            {
                if (!message.Payload.TryGetProperty("SubCommand", out var subCmdProp)) return;
                string subCommand = subCmdProp.GetString() ?? "";

                if (subCommand == "Read")
                {
                    await HandleReadAsync(webSocket, sendResponse, logger, ct);
                }
                else if (subCommand == "Save")
                {
                    await HandleWriteAsync(message, webSocket, sendResponse, logger, ct);
                }
                else if (subCommand == "Scan")
                {
                    await HandleScanAsync(webSocket, sendResponse, logger, ct);
                }
                else if (subCommand == "Delete")
                {
                    await HandleDeleteAsync(message, webSocket, sendResponse, logger, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "USB Command failed");
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, 
                    JsonSerializer.SerializeToElement(new { Success = false, Error = ex.Message })), ct);
            }
        }

        private static string? GetConfigPath(ILogger? logger = null)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] agentFolders = { "FireRakshak.FRMC.UpdateAgent", "update-agent", "UpdateAgent" };
            
            string? currentSearchDir = baseDir;
            for (int i = 0; i < 6; i++)
            {
                if (string.IsNullOrEmpty(currentSearchDir)) break;
                foreach (var folder in agentFolders)
                {
                    string path = Path.Combine(currentSearchDir, folder, "appsettings.json");
                    if (File.Exists(path))
                    {
                        try { if (File.ReadAllText(path).Contains("UpdateOptions")) return path; } catch { }
                    }
                }
                string directPath = Path.Combine(currentSearchDir, "appsettings.json");
                if (File.Exists(directPath))
                {
                    try { if (File.ReadAllText(directPath).Contains("UpdateOptions")) return directPath; } catch { }
                }
                currentSearchDir = Path.GetDirectoryName(currentSearchDir);
            }

            string binPath = Path.Combine(baseDir, "appsettings.json");
            if (File.Exists(binPath) && File.ReadAllText(binPath).Contains("UpdateOptions")) return binPath;
            return null;
        }

        private static async Task HandleDeleteAsync(WebSocketMessage message, WebSocket webSocket, Func<WebSocket, WebSocketMessage, CancellationToken, Task> sendResponse, ILogger logger, CancellationToken ct)
        {
            if (!message.Payload.TryGetProperty("AuthorizedUsbSerialId", out var idProp))
            {
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = false, Error = "Invalid payload" })), ct);
                return;
            }

            string idToRemove = idProp.GetString() ?? "";
            string? configPath = GetConfigPath(logger);
            if (configPath == null)
            {
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = false, Error = "Config file not found" })), ct);
                return;
            }

            try
            {
                logger.LogInformation("Deleting USB ID {Id} from: {Path}", idToRemove, configPath);
                string json = await File.ReadAllTextAsync(configPath);
                var jsonDoc = JsonNode.Parse(json);
                var root = jsonDoc?.AsObject();
                if (root != null && root.TryGetPropertyValue("UpdateOptions", out var optsNode) && optsNode is JsonObject opts)
                {
                    if (opts.TryGetPropertyValue("AuthorizedUsbSerialIds", out var idsNode) && idsNode is JsonArray idsArray)
                    {
                        var itemToRemove = idsArray.FirstOrDefault(x => x?.ToString().Equals(idToRemove, StringComparison.OrdinalIgnoreCase) == true);
                        if (itemToRemove != null)
                        {
                            idsArray.Remove(itemToRemove);
                            await File.WriteAllTextAsync(configPath, jsonDoc!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                            logger.LogInformation("Successfully removed {Id} and saved to {Path}", idToRemove, configPath);

                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                            {
                                try { System.Diagnostics.Process.Start("systemctl", "restart firerakshak-update.service"); } catch { }
                            }

                            await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = true, SubCommand = "Delete" })), ct);
                            return;
                        }
                    }
                }
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = false, Error = "ID not found" })), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Delete failed");
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = false, Error = ex.Message })), ct);
            }
        }

        private static async Task HandleWriteAsync(WebSocketMessage message, WebSocket webSocket, Func<WebSocket, WebSocketMessage, CancellationToken, Task> sendResponse, ILogger logger, CancellationToken ct)
        {
            if (!message.Payload.TryGetProperty("AuthorizedUsbSerialIds", out var idsProp) || idsProp.ValueKind != JsonValueKind.Array)
            {
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = false, Error = "Invalid payload" })), ct);
                return;
            }

            var newIds = idsProp.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList();
            string? configPath = GetConfigPath(logger);
            if (configPath == null)
            {
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = false, Error = "Config file not found" })), ct);
                return;
            }

            try
            {
                logger.LogInformation("Writing USB configuration to: {Path}", configPath);
                string json = await File.ReadAllTextAsync(configPath);
                var jsonDoc = JsonNode.Parse(json);
                var root = jsonDoc?.AsObject();
                if (root != null && root.TryGetPropertyValue("UpdateOptions", out var optsNode) && optsNode is JsonObject opts)
                {
                    var idsArray = new JsonArray();
                    foreach (var id in newIds) idsArray.Add(JsonValue.Create(id));
                    opts["AuthorizedUsbSerialIds"] = idsArray;

                    await File.WriteAllTextAsync(configPath, jsonDoc!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    logger.LogInformation("Successfully saved {Count} IDs to {Path}", newIds.Count, configPath);

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        try { System.Diagnostics.Process.Start("systemctl", "restart firerakshak-update.service"); } catch { }
                    }

                    await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = true, SubCommand = "Save" })), ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Write failed");
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = false, Error = ex.Message })), ct);
            }
        }

        private static async Task HandleReadAsync(WebSocket webSocket, Func<WebSocket, WebSocketMessage, CancellationToken, Task> sendResponse, ILogger logger, CancellationToken ct)
        {
            string? configPath = GetConfigPath(logger);
            if (configPath == null)
            {
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = false, Error = "Config file not found" })), ct);
                return;
            }

            logger.LogInformation("Reading USB configuration from: {Path}", configPath);
            string json = await File.ReadAllTextAsync(configPath);
            using var doc = JsonDocument.Parse(json);
            var ids = new List<string>();
            if (doc.RootElement.TryGetProperty("UpdateOptions", out var opts) && opts.TryGetProperty("AuthorizedUsbSerialIds", out var idsProp))
            {
                foreach (var id in idsProp.EnumerateArray()) { string? val = id.GetString(); if (!string.IsNullOrEmpty(val)) ids.Add(val); }
            }
            await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = true, SubCommand = "Read", AuthorizedUsbSerialIds = ids })), ct);
        }

        private static async Task HandleScanAsync(WebSocket webSocket, Func<WebSocket, WebSocketMessage, CancellationToken, Task> sendResponse, ILogger logger, CancellationToken ct)
        {
            var detectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    logger.LogInformation("Starting Windows USB Scan...");
                    string psScript = @"
                        $results = @();
                        $results += Get-CimInstance Win32_PnPEntity -Filter ""Service='USBSTOR'"" | ForEach-Object { $parts = $_.DeviceID.Split('\'); if($parts.Count -gt 2) { $s = $parts[2]; if($s.Contains('&')) { $s = $s.Split('&')[0] }; $s } };
                        $results += Get-CimInstance Win32_DiskDrive | Where-Object { $_.InterfaceType -eq 'USB' } | Select-Object -ExpandProperty SerialNumber;
                        $results | Where-Object { $_ -ne $null -and $_ -ne '' } | Select-Object -Unique
                    ";

                    var psi = new System.Diagnostics.ProcessStartInfo { FileName = "powershell", Arguments = $"-NoProfile -Command \"{psScript.Replace("\"", "\\\"")}\"", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var process = System.Diagnostics.Process.Start(psi);
                    if (process != null) { string output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync(); foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)) { string id = line.Trim(); if (!string.IsNullOrEmpty(id)) detectedIds.Add(id); } }
                }
                else
                {
                    logger.LogInformation("Starting Linux USB Scan...");
                    var psi = new System.Diagnostics.ProcessStartInfo { FileName = "lsblk", Arguments = "-no SERIAL,TRAN,TYPE", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var process = System.Diagnostics.Process.Start(psi);
                    if (process != null)
                    {
                        string output = await process.StandardOutput.ReadToEndAsync();
                        await process.WaitForExitAsync();
                        foreach (var line in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 3 && parts[1].ToLower() == "usb" && parts[2].ToLower() == "disk")
                                detectedIds.Add(parts[0]);
                        }
                    }
                }
                
                logger.LogInformation("USB Scan found {Count} unique serials", detectedIds.Count);
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = true, SubCommand = "Scan", DeviceIds = detectedIds.ToList() })), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "USB Scan failed");
                await sendResponse(webSocket, new WebSocketMessage(ResponseType, JsonSerializer.SerializeToElement(new { Success = false, Error = ex.Message })), ct);
            }
        }
    }
}
