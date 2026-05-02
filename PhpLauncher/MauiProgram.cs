using Microsoft.Extensions.Logging;
using PhpLauncher.ServerLoader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PhpLauncher
{
    /// <summary>
    /// MAUI application entry point.
    /// Configures the host, services, and starts the PHP server in the background.
    /// </summary>
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            // Register the main app and fonts
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
            // In DEBUG, send logs to the IDE debug output window
            builder.Logging.AddDebug();
#endif

            // Read command-line arguments (e.g. --ServerOptions:HttpsPort=8443)
            // and expose them as a configuration source
            string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();
            if (args.Any())
            {
                builder.Configuration.AddCommandLine(args);
            }

            // Register HttpClient so ServerLoadService can issue HTTP requests
            // (e.g. validate the PHP server's /up endpoint)
            builder.Services.AddHttpClient();

            // Bind the "ServerOptions" configuration section
            // (CLI args or other providers) to the strongly typed ServerOptions class
            builder.Services.Configure<ServerOptions>(options =>
                builder.Configuration.GetSection("ServerOptions").Bind(options));

            // Register ServerLoadService as transient: a new instance per resolution
            builder.Services.AddTransient<ServerLoadService>();

            // Build the DI container and MAUI app
            var app = builder.Build();

            // Start the PHP server on a background thread so the UI thread is not blocked.
            // The scope is created inside the Task so its lifetime matches the async operation.
            Task.Run(async () =>
            {
                // 'using' ensures the scope (and its services) are disposed when StartAsync completes or throws
                using var scope = app.Services.CreateScope();
                var loader = scope.ServiceProvider.GetRequiredService<ServerLoadService>();

                try
                {
                    await loader.StartAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // If the server failed to start, exit the process with the exception's error code
                    Environment.Exit(ex.HResult);
                }
            });

            return app;
        }
    }
}
