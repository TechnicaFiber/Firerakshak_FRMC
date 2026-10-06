using Microsoft.Extensions.Configuration;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Infrastructure.Services
{
    /// <summary>
    /// Provides a service for reading and potentially updating the system clock.
    /// This service can be configured to use a dummy clock for testing/simulation
    /// or interact with the actual system clock.
    /// </summary>
    public class ClockService
    {
        private readonly IConfiguration _configuration;

        // P/Invoke declarations for Windows API
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear;
            public ushort wMonth;
            public ushort wDayOfWeek;
            public ushort wDay;
            public ushort wHour;
            public ushort wMinute;
            public ushort wSecond;
            public ushort wMilliseconds;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetSystemTime(ref SYSTEMTIME lpSystemTime);

        /// <summary>
        /// Initializes a new instance of the <see cref="ClockService"/> class.
        /// </summary>
        /// <param name="configuration">The application configuration, used to determine clock behavior.</param>
        public ClockService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Asynchronously reads the current system time.
        /// The behavior (dummy or real) is determined by the "ClockSettings:UseDummy" configuration.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. The task result contains the current <see cref="DateTime"/>.</returns>
        public Task<DateTime> ReadSystemClock()
        {
            // Check configuration to see if a dummy clock should be used.
            var useDummy = _configuration.GetValue<bool>("ClockSettings:UseDummy");

            if (useDummy)
            {
                // For dummy mode, return UTC Now for consistency in testing.
                Console.WriteLine("[Dummy Clock Read] Simulating reading system clock.");
                return Task.FromResult(DateTime.UtcNow);
            }
            else
            {
                // In a real scenario, this would involve platform-specific calls to get the actual system time.
                // DateTime.Now reflects the local system time.
                Console.WriteLine("[Real Clock Read] Reading actual system clock.");
                return Task.FromResult(DateTime.Now);
            }
        }

        /// <summary>
        /// Asynchronously attempts to update the system clock to a new specified time.
        /// The behavior (dummy or real) is determined by the "ClockSettings:UseDummy" configuration.
        /// </summary>
        /// <param name="newTime">The new <see cref="DateTime"/> to set the system clock to.</param>
        /// <returns>A task that represents the asynchronous operation. The task result is a tuple indicating success and a message.</returns>
        public async Task<(bool Success, string Message)> UpdateSystemClock(DateTime newTime)
        {
            var useDummy = _configuration.GetValue<bool>("ClockSettings:UseDummy");
            var enableClockConfiguration = _configuration.GetValue<bool>("SystemSettings:EnableClockConfiguration");
            var disableNetworkTimeSync = _configuration.GetValue<bool>("SystemSettings:DisableNetworkTimeSync");
            var simulateClockConfig = _configuration.GetValue<bool>("SystemSettings:SimulateConfigurationChanges:Clock");

            if (simulateClockConfig)
            {
                Console.WriteLine($"[Clock Update Simulation] Simulating clock update to: {newTime}");
                return (true, "Clock update simulated successfully.");
            }

            if (!enableClockConfiguration)
            {
                return (false, "System clock configuration is disabled by settings.");
            }

            if (useDummy)
            {
                // Simulate a delay for the dummy update.
                Console.WriteLine($"[Dummy Clock Update] Simulating updating system clock to: {newTime}");
                await Task.Delay(100);
                return (true, "Dummy clock updated successfully.");
            }
            else
            {
                Console.WriteLine($"[Real Clock Update] Attempting to update system clock to: {newTime}");

                if (disableNetworkTimeSync)
                {
                    var (syncSuccess, syncMessage) = await DisableNetworkTimeSynchronization();
                    if (!syncSuccess)
                    {
                        return (false, $"Failed to disable network time synchronization: {syncMessage}");
                    }
                }

                // Convert DateTime to SYSTEMTIME
                SYSTEMTIME st = new SYSTEMTIME
                {
                    wYear = (ushort)newTime.Year,
                    wMonth = (ushort)newTime.Month,
                    wDay = (ushort)newTime.Day,
                    wHour = (ushort)newTime.Hour,
                    wMinute = (ushort)newTime.Minute,
                    wSecond = (ushort)newTime.Second,
                    wMilliseconds = (ushort)newTime.Millisecond
                };

                // Set the system time
                bool success = SetSystemTime(ref st);

                if (success)
                {
                    Console.WriteLine($"[Real Clock Update] System clock updated successfully to: {newTime}");
                    return (true, "System clock updated successfully.");
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[Real Clock Update] Failed to update system clock. Error code: {error}");
                    return (false, $"Failed to update system clock. Error code: {error}. This operation often requires administrative privileges.");
                }
            }
        }

        /// <summary>
        /// Disables network time synchronization using w32tm command.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. The task result is a tuple indicating success and a message.</returns>
        public async Task<(bool Success, string Message)> DisableNetworkTimeSynchronization()
        {
            return await RunShellCommand("w32tm /config /manualpeerlist:none /syncfromflags:MANUAL", "Disabling network time synchronization");
        }

        /// <summary>
        /// Enables network time synchronization using w32tm command.
        /// </summary>
        /// <returns>A task that represents the asynchronous operation. The task result is a tuple indicating success and a message.</returns>
        public async Task<(bool Success, string Message)> EnableNetworkTimeSynchronization()
        {
            return await RunShellCommand("w32tm /config /syncfromflags:DOMHIER /update", "Enabling network time synchronization");
        }

        protected virtual async Task<(bool Success, string Message)> RunShellCommand(string command, string description)
        {
            try
            {
                var startInfo = new ProcessStartInfo("cmd.exe", $"/c {command}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return (false, $"Failed to start process for: {command}");
                    }

                    await process.WaitForExitAsync();

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();

                    if (process.ExitCode != 0)
                    {
                        return (false, $"{description} failed. Error: {error}. Output: {output}");
                    }
                    return (true, $"{description} successful. Output: {output}");
                }
            }
            catch (Exception ex)
            {
                return (false, $"{description} failed with exception: {ex.Message}");
            }
        }
    }
}