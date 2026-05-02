using CliWrap;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using System.Text;

namespace PhpLauncher.ServerLoader
{
    public class ServerLoadService : IHostedService
    {
        private readonly ServerOptions _options;
        private readonly ILogger<ServerLoadService> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        // Guards concurrent access to _processCts and _processTask
        private readonly object _processLock = new();

        private CliWrap.Command? _frankenPhpCommand = null;
        private CancellationTokenSource? _processCts = null; // Controls the child process lifetime
        private Task? _processTask = null;                   // Task wrapping process execution

        public ServerLoadService(
            IOptions<ServerOptions> options,
            ILogger<ServerLoadService> logger,
            IHttpClientFactory httpClientFactory)
        {
            _options = options.Value;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Ensure the project directory exists before continuing
            if (!Directory.Exists(_options.ProjectPath))
            {
                _logger.LogError("Project path does not exist: {Path}", _options.ProjectPath);
                throw new DirectoryNotFoundException(_options.ProjectPath);
            }

            var serviceUrl = $"https://localhost:{_options.HttpsPort}/up";

            // Avoid double startup if FrankenPHP is already running
            if (await IsServiceAlreadyRunningAsync(serviceUrl, cancellationToken))
            {
                _logger.LogInformation("Service is already running at {Url}.", serviceUrl);
                return;
            }

            // Generate Caddyfile with configured ports before starting the process
            try
            {
                await PrepareConfigFileAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to prepare configuration file. Aborting startup.");
                throw;
            }

            // Retry up to 3 times with 5s between attempts;
            // do not retry on external cancellation (OperationCanceledException / TaskCanceledException)
            var retryPolicy = Policy
                .Handle<Exception>(ex =>
                    ex is not OperationCanceledException &&
                    ex is not TaskCanceledException)
                .WaitAndRetryAsync(
                    retryCount: 3,
                    sleepDurationProvider: _ => TimeSpan.FromSeconds(5),
                    onRetry: (exception, timeSpan, retryCount, _) =>
                    {
                        _logger.LogWarning(
                            "Attempt {Count} failed. Retrying in {Wait}s... Error: {Msg}",
                            retryCount, timeSpan.TotalSeconds, exception.Message);
                    });

            await retryPolicy.ExecuteAsync(async () =>
            {
                // Cancel and wait for any previous process before starting a new one
                await CancelExistingProcessAsync();

                var stdErrBuffer = new StringWriter();
                var stdErrLock = new object(); // Local lock for concurrent writes to the buffer

                // Create a CTS linked to the external token so the process can be cancelled individually
                lock (_processLock)
                {
                    _processCts?.Cancel();
                    _processCts?.Dispose();
                    _processCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }

                var processToken = _processCts.Token;

                // Run FrankenPHP in the background; the process runs until processToken is cancelled
                _processTask = Task.Run(async () =>
                {
                    try
                    {
                        var result = await Cli
                            .Wrap(Path.Combine(_options.FrankenPhpPath, "frankenphp"))
                            .WithArguments("run")
                            .WithWorkingDirectory(_options.ProjectPath)
                            // Capture stderr without blocking to diagnose process failures
                            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
                            {
                                lock (stdErrLock)
                                    stdErrBuffer.WriteLine(line);
                            }))
                            .WithValidation(CommandResultValidation.None) // Handle exit code manually
                            .ExecuteAsync(processToken);

                        // Only log an error if the process exited unexpectedly (not due to cancellation)
                        if (result.ExitCode != 0 && !processToken.IsCancellationRequested)
                        {
                            string captured;
                            lock (stdErrLock)
                                captured = stdErrBuffer.ToString();

                            _logger.LogError(
                                "FrankenPHP exited unexpectedly (code {Code}): {Err}",
                                result.ExitCode, captured);
                        }
                    }
                    catch (OperationCanceledException) { /* Normal cancellation; do not log */ }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unhandled exception in FrankenPHP process.");
                    }
                }, CancellationToken.None); // CancellationToken.None: the task is not cancelled before it starts

                // Allow the process time to initialize before the health check
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // External cancellation during wait: clean up the process and propagate
                    await CancelExistingProcessAsync();
                    throw;
                }

                // Verify the process responded correctly after startup
                if (!await IsServiceAlreadyRunningAsync(serviceUrl, cancellationToken))
                    throw new HttpRequestException(
                        $"FrankenPHP did not respond after startup at {serviceUrl}.");

                _logger.LogInformation("FrankenPHP is running at {Url}.", serviceUrl);
            });
        }

        // ─── Helpers ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Cancels the running process and waits for it to exit to avoid orphaned processes.
        /// Thread-safe: the lock ensures only one thread manipulates _processCts / _processTask.
        /// </summary>
        private async Task CancelExistingProcessAsync()
        {
            Task? taskToAwait = null;

            lock (_processLock)
            {
                if (_processCts != null)
                {
                    _processCts.Cancel();
                    _processCts.Dispose();
                    _processCts = null;
                }
                taskToAwait = _processTask;
                _processTask = null;
            }

            // Await outside the lock to avoid blocking other threads during await
            if (taskToAwait != null)
            {
                try { await taskToAwait; }
                catch { /* Exceptions already logged inside _processTask */ }
            }
        }

        /// <summary>
        /// Checks whether FrankenPHP is already serving requests at <paramref name="serviceUrl"/>.
        /// Distinguishes HttpClient timeout (treat as retry) from external cancellation (propagated).
        /// </summary>
        private async Task<bool> IsServiceAlreadyRunningAsync(
            string serviceUrl, CancellationToken cancellationToken)
        {
            try
            {
                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetAsync(serviceUrl, cancellationToken);

                if (response.IsSuccessStatusCode)
                    return true;

                _logger.LogWarning(
                    "Service returned non-success status {StatusCode}. FrankenPHP will be started.",
                    response.StatusCode);
                return false;
            }
            catch (HttpRequestException ex)
            {
                // Connection refused or network error: FrankenPHP is not running
                _logger.LogInformation(
                    "Service is not responding ({Msg}). Starting FrankenPHP...", ex.Message);
                return false;
            }
            catch (TaskCanceledException ex)
            {
                // Distinguish HttpClient timeout (retry) from external cancellation (propagate)
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(
                        "Service health check was cancelled externally.", ex, cancellationToken);

                _logger.LogInformation("Timeout while checking service. Starting FrankenPHP...");
                return false;
            }
        }

        // StopAsync delegates process shutdown to CancelExistingProcessAsync via the linked CTS;
        // the host's cancellationToken triggers the cancellation chain automatically.
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Generates the Caddyfile by substituting ports from <see cref="ServerOptions"/>.
        /// Overwrites any existing file on each startup to ensure a fresh configuration.
        /// </summary>
        private async Task PrepareConfigFileAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_options.ProjectPath))
                    throw new DirectoryNotFoundException("Project path is not configured.");

                string configPath = Path.GetFullPath(Path.Combine(_options.ProjectPath, "Caddyfile"));
                string directoryPath = Path.GetDirectoryName(configPath) ?? string.Empty;

                // Create the directory if it does not exist yet (first run)
                if (!Directory.Exists(directoryPath))
                    Directory.CreateDirectory(directoryPath);

                // Embedded Caddyfile template (ports substituted below)
                string configFileContent = """
                {
                    # Global settings
                    http_port  ${HTTP_PORT}
                    https_port ${HTTPS_PORT}

                    # FrankenPHP block (worker mode, etc.)
                    frankenphp {
                        # Additional FrankenPHP settings
                    }

                    # Optional: disable admin API or tune global logging
                    admin off
                }

                # The domain name of your server
                localhost {
                    # Document root
                    root * public/

                    # Compression
                    encode zstd gzip

                    # Basic hardening: block sensitive paths
                    @disallowed {
                        path /.*
                        path /composer.*
                        path /storage/*.json
                    }
                    error @disallowed 403

                    # PHP and static file server (php_server handles index.php)
                    php_server {
                        index index.php
                    }

                    # Access log (useful for debugging)
                    log {
                        output file storage/logs/caddy.log
                    }
                }
                """;


                configFileContent = configFileContent
                    .Replace("${HTTP_PORT}", _options.HttpPort.ToString())
                    .Replace("${HTTPS_PORT}", _options.HttpsPort.ToString());

                var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                await File.WriteAllTextAsync(configPath, configFileContent, encoding);
            }
            catch (Exception)
            {

                throw;
            }

        }
    }
}
