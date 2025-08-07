namespace ProcessorApi;

public class HealthCheckProcessor(IHttpClientFactory httpClientFactory)
{
    private readonly Lock _healthLock = new();

    internal async Task HealthCheckAsync(
        string client,
        PaymentProcessorHealth health,
        CancellationToken cancellationToken = default)
    {
        // lock (_healthLock)
        // {
        //     health.IsHealthy = true;
        //     health.MinResponseTime = 0;
        // }

        try
        {
            var httpClient = httpClientFactory.CreateClient(client);
            var response = await httpClient.GetFromJsonAsync<ServiceHealth>(
                "payments/service-health",
                cancellationToken: cancellationToken);

            if (response != null)
            {
                lock (_healthLock)
                {
                    health.IsHealthy = !response.Failing;
                    health.MinResponseTime = response.MinResponseTime;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_healthLock)
            {
                health.IsHealthy = false;
            }

            Console.WriteLine($"[{client}] Health check failed: {ex.Message}");
        }
    }
}