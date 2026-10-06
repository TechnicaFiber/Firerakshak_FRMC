using FireRakshak.FRMC.Managers.ServiceManager;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace FireRakshak.FRMC.Host
{
    public class Startup
    {
        public IConfiguration Configuration { get; }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            // Services are already configured in Program.cs, so this can be empty or add web-specific services if needed.
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            logger.LogInformation("Configuring Kestrel for WebSockets.");

            app.UseWebSockets();

            app.Use(async (context, next) =>
            {
                if (context.Request.Path == "/get_token" && context.Request.Method == "POST")
                {
                    using var reader = new System.IO.StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    var credentials = System.Text.Json.JsonSerializer.Deserialize<UserCredentials>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (credentials != null)
                    {
                        var userStore = app.ApplicationServices.GetRequiredService<FireRakshak.FRMC.Infrastructure.Security.UserStore>();
                        var jwtTokenHandler = app.ApplicationServices.GetRequiredService<FireRakshak.FRMC.Infrastructure.Security.JwtTokenHandler>();

                        if (await userStore.ValidateCredentialsAsync(credentials.Username, credentials.Password))
                        {
                            var role = await userStore.GetUserRoleAsync(credentials.Username);
                            if (role.HasValue)
                            {
                                var token = jwtTokenHandler.GenerateToken(credentials.Username, role.Value.ToString());
                                context.Response.ContentType = "application/json";
                                await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { token }));
                                return;
                            }
                        }
                    }

                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsync("Invalid credentials.");
                    return;
                }

                if (context.Request.Path == "/service_ws/")
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                        using var scope = app.ApplicationServices.CreateScope(); // Use ApplicationServices
                        //var webSocketServiceServer = scope.ServiceProvider.GetRequiredService<WebSocketServiceServer>();
                        var programLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>(); // Get a logger for Program
                        var token = context.Request.Query["token"].FirstOrDefault(); // Corrected: Use context.Request.Query

                        programLogger.LogInformation("Incoming WebSocket connection request to /service_ws/.");
                        //await webSocketServiceServer.HandleWebSocketConnectionAsync(webSocket, token);
                    }
                    else
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    }
                }
                else
                {
                    await next();
                }
            });

            // Add a default endpoint for HTTP requests if needed, otherwise they will 404
            app.Run(async context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsync("Endpoint not found.");
            });
        }
    }

    public class UserCredentials
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
