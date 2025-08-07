namespace ProcessorApi;

public class HealthCheckWorker(
    HealthCheckProcessor healthCheckProcessor,
    PaymentProcessorHealth defaultHealth,
    PaymentProcessorHealth fallbackHealth,
    TimeSpan interval)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var defaultTask = CheckAsync(Constants.DefaultClient, defaultHealth, cancellationToken);
        var fallbackTask = CheckAsync(Constants.FallbackClient, fallbackHealth, cancellationToken);

        await Task.WhenAll(defaultTask, fallbackTask);
    }

    private async Task CheckAsync(string client, PaymentProcessorHealth health, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await healthCheckProcessor.HealthCheckAsync(client, health, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Health check failed for {client}: {ex.Message}");
            }

            await Task.Delay(interval, cancellationToken);
        }
    }
}