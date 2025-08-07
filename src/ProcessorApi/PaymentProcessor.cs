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

        string usedClient;

        var defaultClient = httpClientFactory.CreateClient(Constants.DefaultClient);
        var fallbackClient = httpClientFactory.CreateClient(Constants.FallbackClient);
        
        var retryPolicy = Policy
            .Handle<Exception>()
            .OrResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(
                3,
                retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryAttempt, context) =>
                {
                    if (outcome.Exception != null)
                    {
                        Console.WriteLine("Retry {RetryAttempt} due to exception. Waiting {Delay}s before next attempt.",
                            retryAttempt, timespan.TotalSeconds);
                    }
                    else
                    {
                        Console.WriteLine("Retry {RetryAttempt} due to unsuccessful response ({StatusCode}). Waiting {Delay}s before next attempt.",
                            retryAttempt, outcome.Result?.StatusCode, timespan.TotalSeconds);
                    }
                });


        
        HttpResponseMessage response;

        if (isDefaultHealthy)
        {
            response = await retryPolicy.ExecuteAsync(() =>
                defaultClient.PostAsJsonAsync("/payments", new
                {
                    payment.CorrelationId,
                    payment.Amount,
                    RequestedAt = DateTime.UtcNow.ToString("o")
                }));

            usedClient = Constants.DefaultClient;
            
            // If still unsuccessful after retries, fallback
            if (!response.IsSuccessStatusCode)
            {
                response = await retryPolicy.ExecuteAsync(() =>
                    fallbackClient.PostAsJsonAsync("/payments", new
                    {
                        payment.CorrelationId,
                        payment.Amount,
                        RequestedAt = DateTime.UtcNow.ToString("o")
                    }));
                
                usedClient = Constants.FallbackClient;
            }
        }
        else
        {
            // Go directly to fallback client
            response = await retryPolicy.ExecuteAsync(() =>
                fallbackClient.PostAsJsonAsync("/payments", new
                {
                    payment.CorrelationId,
                    payment.Amount,
                    RequestedAt = DateTime.UtcNow.ToString("o")
                }));
            
            usedClient = Constants.FallbackClient;
        }

        // var httpClient = isDefaultHealthy
        //     ? httpClientFactory.CreateClient(Constants.DefaultClient)
        //     : httpClientFactory.CreateClient(Constants.FallbackClient);
        //
        //
        // var response = await retryPolicy.ExecuteAsync(() => defaultClient.PostAsJsonAsync("/payments", new
        // {
        //     payment.CorrelationId,
        //     payment.Amount,
        //     RequestedAt = DateTime.UtcNow.ToString("o")
        // }));
        // var response = new HttpResponseMessage(HttpStatusCode.OK);
        
        if (response.IsSuccessStatusCode)
        {
            var paymentResponse = new PaymentResponse(payment.CorrelationId, payment.Amount, true);
            if (responses.TryRemove(payment.CorrelationId, out var tcs))
                tcs.SetResult(paymentResponse);

            if (usedClient == Constants.DefaultClient)
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