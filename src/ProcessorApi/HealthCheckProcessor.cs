namespace ProcessorApi;

public class HealthCheckProcessor(IHttpClientFactory httpClientFactory)
{
    internal async Task HealthCheckAsync(
        string client,
        PaymentProcessorHealth health,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var httpClient = httpClientFactory.CreateClient(client);
            httpClient.Timeout = TimeSpan.FromSeconds(2);
            var response = await httpClient.GetFromJsonAsync<HealthStatus>(
                "payments/service-health",
                cancellationToken: cancellationToken);

            if (response != null)
            {
                health.IsHealthy = !response.Failing;
                health.MinResponseTime = response.MinResponseTime;
            }
        }
        catch
        {
            health.IsHealthy = false;
            health.MinResponseTime = int.MaxValue;
        }
    }
}