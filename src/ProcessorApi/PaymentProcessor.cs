using System.Collections.Concurrent;
using System.Net.Http.Json;

namespace ProcessorApi;

internal class PaymentProcessor
{
    private readonly HttpClient _httpClient;

    public PaymentProcessor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    internal async Task ProcessPaymentsAsync(
        string defaultUrl,
        string fallbackUrl,
        PaymentRequest payment,
        ConcurrentDictionary<string, TaskCompletionSource<PaymentResponse>> responses,
        PaymentProcessorHealth defaultHealth,
        PaymentProcessorHealth fallbackHealth)
    {
        string? targetUrl = null;
        bool isDefaultUrl = false;
        // Determine the target URL based on health status
        if (defaultHealth.IsHealthy)
        {
            targetUrl = defaultUrl;
            isDefaultUrl = true;
        }
        else if (fallbackHealth.IsHealthy)
        {
            targetUrl = fallbackUrl;
        }

        if (targetUrl != null)
        {
            var requestBody = new
            {
                payment.CorrelationId,
                payment.Amount,
                RequestedAt = DateTime.UtcNow.ToString("o")
            };

            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{targetUrl}/payments", requestBody);
                if (response.IsSuccessStatusCode)
                {
                    if (responses.TryRemove(payment.CorrelationId, out var tcs))
                    {
                        tcs.SetResult(new PaymentResponse(payment.CorrelationId, true));
                    }
                    Console.WriteLine($"Payment processed successfully: {payment.CorrelationId} is default url {isDefaultUrl}");
                }
                else
                {
                    if (responses.TryRemove(payment.CorrelationId, out var tcs))
                    {
                        tcs.SetResult(new PaymentResponse(payment.CorrelationId, false));
                    }
                    Console.WriteLine($"Payment failed: {payment.CorrelationId}");
                }
            }
            catch
            {
                if (responses.TryRemove(payment.CorrelationId, out var tcs))
                {
                    tcs.SetResult(new PaymentResponse(payment.CorrelationId, false));
                }
                Console.WriteLine($"Payment processing error: {payment.CorrelationId}");
                
                // Set the url as unhealthy
                if (isDefaultUrl && defaultHealth.IsHealthy) defaultHealth.IsHealthy = false;
                if (!isDefaultUrl && fallbackHealth.IsHealthy) fallbackHealth.IsHealthy = false;
            }
        }
        else
        {
            if (responses.TryRemove(payment.CorrelationId, out var tcs))
            {
                tcs.SetResult(new PaymentResponse(payment.CorrelationId, false));
            }
            Console.WriteLine($"No healthy processor available for payment: {payment.CorrelationId}");
        }
    }
}