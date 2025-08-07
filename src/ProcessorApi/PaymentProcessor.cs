using System.Collections.Concurrent;
using System.Net;
using Polly;

namespace ProcessorApi;

public class PaymentProcessor(IHttpClientFactory httpClientFactory)
{
    internal async Task ProcessPaymentsAsync(
        PaymentRequest payment,
        ConcurrentDictionary<string, TaskCompletionSource<PaymentResponse>> responses,
        PaymentProcessorHealth defaultHealth,
        PaymentProcessorHealth fallbackHealth,
        List<PaymentResponse> paymentsDefault,
        List<PaymentResponse> paymentsFallback)
    {
        var isDefaultHealthy = defaultHealth.IsHealthy;
        var isFallbackHealthy = fallbackHealth.IsHealthy;
        // Console.WriteLine($"Default Health: {isDefaultHealthy}, Fallback Health: {isFallbackHealthy}");
        // if (!isDefaultHealthy && !isFallbackHealthy)
        // {
        //     await Task.Delay(2000);
        //     isDefaultHealthy = defaultHealth.IsHealthy;
        //     isFallbackHealthy = fallbackHealth.IsHealthy;
        //     Console.WriteLine($"Default Health: {isDefaultHealthy}, Fallback Health: {isFallbackHealthy}");
        //     if (!isDefaultHealthy && !isFallbackHealthy)
        //     {
        //         await Task.Delay(2000);
        //         isDefaultHealthy = defaultHealth.IsHealthy;
        //         isFallbackHealthy = fallbackHealth.IsHealthy;
        //         Console.WriteLine($"Default Health: {isDefaultHealthy}, Fallback Health: {isFallbackHealthy}");
        //         if (!isDefaultHealthy && !isFallbackHealthy)
        //         {
        //             await Task.Delay(1000);
        //             isDefaultHealthy = defaultHealth.IsHealthy;
        //             Console.WriteLine($"Default Health: {isDefaultHealthy}, Fallback Health: {isFallbackHealthy}");
        //         }
        //     }
        // }

        var httpClient = isDefaultHealthy
            ? httpClientFactory.CreateClient(Constants.DefaultClient)
            : httpClientFactory.CreateClient(Constants.FallbackClient);

        var retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        var response = await retryPolicy.ExecuteAsync(() => httpClient.PostAsJsonAsync("/payments", new
        {
            payment.CorrelationId,
            payment.Amount,
            RequestedAt = DateTime.UtcNow.ToString("o")
        }));
        
        // try
        // {
        //     var response2 = await retryPolicy.ExecuteAsync(() =>
        //         httpClient.PostAsJsonAsync("/payments", new
        //         {
        //             payment.CorrelationId,
        //             payment.Amount,
        //             RequestedAt = DateTime.UtcNow.ToString("o")
        //         }));
        //
        //     if (!response2.IsSuccessStatusCode)
        //     {
        //         var errorContent = await response2.Content.ReadAsStringAsync();
        //         Console.WriteLine($"Request failed: {response2.StatusCode}, Content: {errorContent}");
        //     }
        // }
        // catch (Exception ex)
        // {
        //     Console.WriteLine($"Exception occurred: {ex.Message}");
        // }

        // var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (response.IsSuccessStatusCode)
        {
            var paymentResponse = new PaymentResponse(payment.CorrelationId, payment.Amount, true);
            if (responses.TryRemove(payment.CorrelationId, out var tcs))
                tcs.SetResult(paymentResponse);

            if (GetClientExecutor(httpClient) == Constants.DefaultClient)
            {
                lock (paymentsDefault)
                    paymentsDefault.Add(paymentResponse);
                return;
            }

            lock (paymentsFallback)
                paymentsFallback.Add(paymentResponse);
        }
        else
        {
            if (responses.TryRemove(payment.CorrelationId, out var tcs))
                tcs.SetResult(new PaymentResponse(payment.CorrelationId, payment.Amount, false));
        }
    }

    private static string GetClientExecutor(HttpClient httpClient)
    {
        return httpClient.DefaultRequestHeaders.TryGetValues(Constants.HeaderClientSourceName, out var values)
            ? values.First()
            : string.Empty;
    }
}