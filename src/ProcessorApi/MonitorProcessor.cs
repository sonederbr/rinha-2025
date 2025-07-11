namespace ProcessorApi;

internal class MonitorProcessor(HttpClient httpClient)
{
    internal async Task MonitorProcessorHealth(string processorUrl, PaymentProcessorHealth health, TimeSpan interval)
    {
        while (true)
        {
            try
            {
                var response = await httpClient.GetFromJsonAsync<ServiceHealth>($"{processorUrl}/payments/service-health");
                if (response != null)
                {
                    lock (health)
                    {
                        health.IsHealthy = !response.Failing;
                        health.MinResponseTime = response.MinResponseTime;
                        if(!health.IsHealthy)
                            Console.WriteLine($"The url: {processorUrl} is Healthy { health.IsHealthy }");
                        if(health.MinResponseTime > 0)
                            Thread.Sleep(health.MinResponseTime);
                    }
                }
            }
            catch
            {
                lock (health)
                {
                    health.IsHealthy = false;
                    Console.WriteLine($"Error url: {processorUrl} is Healthy { health.IsHealthy }");
                }
            }
            Thread.Sleep(interval);
        }
    }
}