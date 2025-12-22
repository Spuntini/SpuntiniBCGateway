public class Worker : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly SpuntiniBCGateway.Services.EventLog _eventLogger;
    private GatewayScheduler? _scheduler;

    public Worker(IConfiguration config)
    {
        _config = config;
        _eventLogger = new SpuntiniBCGateway.Services.EventLog(config);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _eventLogger.InfoAsync(SpuntiniBCGateway.Services.EventLog.GetMethodName(), "Worker", "SpuntiniBCGateway service started.");

            // Initialize scheduler
            _scheduler = new GatewayScheduler(_config, _eventLogger);
            await _scheduler.InitializeAsync();

            if (bool.TryParse(_config["StartAtStartup"], out bool start) && start)
            {
                await _eventLogger.InfoAsync(SpuntiniBCGateway.Services.EventLog.GetMethodName(), "Worker", "Auto start-up");

                // Acquire the execution lock to prevent concurrent gateway execution from any source
                if (!await Program._gatewayExecutionLock.WaitAsync(TimeSpan.Zero, stoppingToken))
                {
                    await _eventLogger.WarningAsync(nameof(GatewayScheduler), "Worker", $"Gateway is already running from another source.");
                    return;
                }

                try
                {         
                     await Program.RunGatewayAsync(_config, _eventLogger, stoppingToken);
                }
                finally
                {
                    Program._gatewayExecutionLock.Release();
                }               
            }
            else
            {
                await _eventLogger.InfoAsync(SpuntiniBCGateway.Services.EventLog.GetMethodName(), "Worker", "No Auto start-up");
            }

            // Start scheduler loop - checks every minute
            await RunSchedulerLoopAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            await _eventLogger.ErrorAsync(SpuntiniBCGateway.Services.EventLog.GetMethodName(), "Worker", ex);
        }
        finally
        {
            stopwatch.Stop();
            await _eventLogger.InfoAsync(SpuntiniBCGateway.Services.EventLog.GetMethodName(), "Worker", $"SpuntiniBCGateway service finished in {SpuntiniBCGateway.Services.StringHelper.GetDurationString(stopwatch.Elapsed)}.");
        }
    }

    private async Task RunSchedulerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _eventLogger.InfoAsync(nameof(Worker), "Worker", $"Check scheduler");

                if (_scheduler != null)
                {
                    await _scheduler.CheckAndExecuteSchedulesAsync(stoppingToken);
                }

                // Wait 60 seconds before the next check
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when service is stopping
            }
            catch (Exception ex)
            {
                await _eventLogger.ErrorAsync(nameof(Worker), "Worker", ex);
                // Continue the loop even if there's an error
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException) { }
            }
        }
    }
}
