using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ProcessorApi;

public class PaymentWorker(
    Channel<PaymentRequest> channel,
    ConcurrentDictionary<string, TaskCompletionSource<PaymentResponse>> responseDictionary,
    PaymentProcessor processor,
    PaymentProcessorHealth defaultHealth,
    PaymentProcessorHealth fallbackHealth,
    List<PaymentResponse> paymentsDefault,
    List<PaymentResponse> paymentsFallback)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await foreach (var payment in channel.Reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                await processor.ProcessPaymentsAsync(
                    payment,
                    responseDictionary,
                    defaultHealth,
                    fallbackHealth,
                    paymentsDefault,
                    paymentsFallback);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing payment {payment.CorrelationId}: {ex.Message}");
            }
        }
    }
}