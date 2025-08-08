using System.Collections.Concurrent;
using Polly;
using Polly.Timeout;
using Polly.Wrap;

namespace ProcessorApi;

public class PaymentProcessor
{
    private readonly IHttpClientFactory _httpClientFactory;

    private AsyncPolicyWrap<HttpResponseMessage>? _defaultPolicy;
    private AsyncPolicy<HttpResponseMessage>? _fallbackPolicy;

    public PaymentProcessor(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        ConfigureRetryPolices();
    }

    public async Task ProcessTransactionAsync(
        PaymentRequest payment,
        ConcurrentDictionary<string, TaskCompletionSource<PaymentResponse>> responseDictionary,
        PaymentProcessorHealth defaultHealth,
        PaymentProcessorHealth fallbackHealth,
        List<PaymentResponse> paymentsDefault,
        List<PaymentResponse> paymentsFallback)
    {
        var payload = new
        {
            payment.CorrelationId,
            payment.Amount,
            payment.RequestedAt
        };

        string usedClient;
        bool response;
        if (ShouldUseFallback(defaultHealth, fallbackHealth))
        {
            response = await SendToFallbackAsync(payload);
            usedClient = Constants.FallbackClient;
        }
        else
        {
            response = await SendToDefaultAsync(payload);
            usedClient = Constants.DefaultClient;
        }

        if (response)
        {
            DateTimeOffset.TryParse(payload.RequestedAt, out var requestedAt);
            var paymentResponse = new PaymentResponse(payment.CorrelationId, payment.Amount, requestedAt, true);
            if (responseDictionary.TryRemove(paymentResponse.CorrelationId, out var tcs))
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
            if (responseDictionary.TryRemove(payment.CorrelationId, out var tcs))
                tcs.SetResult(new PaymentResponse(payment.CorrelationId, payment.Amount, default, false));
        }
    }

    private async Task<bool> SendToDefaultAsync(object payload)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(Constants.DefaultClient);
            var context = new Context
            {
                ["payload"] = payload
            };
            var response = await _defaultPolicy!.ExecuteAsync((_, ct) =>
                client.PostAsJsonAsync("/payments", payload, ct), context, CancellationToken.None);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Send To Default error: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> SendToFallbackAsync(object payload)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(Constants.FallbackClient);
            var context = new Context
            {
                ["payload"] = payload
            };
            var response = await _fallbackPolicy!.ExecuteAsync((_, ct) =>
                client.PostAsJsonAsync("/payments", payload, ct), context, CancellationToken.None);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Send To Fallback error: " + ex.Message);
            return false;
        }
    }

    private static bool ShouldUseFallback(
        PaymentProcessorHealth defaultHealth,
        PaymentProcessorHealth fallbackHealth)
    {
        // Default is out, use fallback
        if (!defaultHealth.IsHealthy)
            return true;

        // Default is ok, fallback is ok, and the default response time is high, use fallback
        return fallbackHealth.IsHealthy && defaultHealth.MinResponseTime > (3 * fallbackHealth.MinResponseTime);
    }

    private void ConfigureRetryPolices()
    {
        // 1. Timeout Policy (3 s per try)
        var timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(
            TimeSpan.FromSeconds(1.5),
            TimeoutStrategy.Pessimistic,
            onTimeoutAsync: (_, timespan, _, _) =>
            {
                Console.WriteLine($"Timeout after {timespan.TotalSeconds}s.");
                return Task.CompletedTask;
            });

        // 2. Fallback Policy (will try a fallback client)
        var fallbackPolicy = Policy<HttpResponseMessage>
            .Handle<Exception>()
            .OrResult(r => !r.IsSuccessStatusCode)
            .FallbackAsync(
                fallbackAction: async (_, context, ct) =>
                {
                    Console.WriteLine("[Fallback] Triggered after default policy failure.");
                    var fallbackClient = _httpClientFactory.CreateClient(Constants.FallbackClient);
                    var fallbackResponse = await fallbackClient.PostAsJsonAsync("/payments", context["payload"], ct);
                    return fallbackResponse;
                },
                onFallbackAsync: async (_, _) =>
                {
                    Console.WriteLine("[Fallback] Executing fallback logic.");
                    await Task.CompletedTask;
                });

        // 3. Retry Policy (Default - 2 retries)
        var defaultRetryPolicy = Policy<HttpResponseMessage>
            .Handle<Exception>()
            .OrResult(r => !r.IsSuccessStatusCode)
            .WaitAndRetryAsync(2,
                attempt => TimeSpan.FromSeconds(Math.Pow(1.5, attempt)),
                onRetry: (outcome, timespan, retryAttempt, _) =>
                {
                    Console.WriteLine($"[Default] Retry {retryAttempt} after {timespan.TotalSeconds}s: " +
                                      $"{outcome.Exception?.Message ?? outcome.Result?.ReasonPhrase}");
                });

        // 4. Wrap them (inner to outer): Timeout -> Retry -> Fallback
        _defaultPolicy = fallbackPolicy.WrapAsync(defaultRetryPolicy.WrapAsync(timeoutPolicy));

        // 5. Fallback-only Policy with Timeout & Retry (optional)
        _fallbackPolicy = Policy.WrapAsync(
            timeoutPolicy,
            Policy<HttpResponseMessage>
                .Handle<Exception>()
                .OrResult(r => !r.IsSuccessStatusCode)
                .WaitAndRetryAsync(2,
                    attempt => TimeSpan.FromSeconds(Math.Pow(1.5, attempt)),
                    onRetry: (outcome, timespan, retryAttempt, _) =>
                    {
                        Console.WriteLine($"[FallbackOnly] Retry {retryAttempt} after {timespan.TotalSeconds}s: " +
                                          $"{outcome.Exception?.Message ?? outcome.Result?.ReasonPhrase}");
                    }));
    }
}