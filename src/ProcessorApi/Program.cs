using ProcessorApi;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Polly;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddSingleton<PaymentProcessor>();
builder.Services.AddSingleton<MonitorProcessor>();
builder.Services.AddHttpClient<PaymentProcessor>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(5); // Set global timeout to 5 seconds
})
.SetHandlerLifetime(TimeSpan.FromMinutes(5)) // Reuse HttpClient handlers
.AddTransientHttpErrorPolicy(policy => policy.RetryAsync(3)); // Retry policy

var app = builder.Build();

const string paymentProcessorDefaultUrl = "http://localhost:8001";
const string paymentProcessorFallbackUrl = "http://localhost:8002";

var paymentChannel = Channel.CreateBounded<PaymentRequest>(new BoundedChannelOptions(1000)
{
    SingleReader = false, // Allow multiple readers
    SingleWriter = true,  // Optimize for a single writer
    FullMode = BoundedChannelFullMode.Wait // Wait when the channel is full
});
var responseDictionary = new ConcurrentDictionary<string, TaskCompletionSource<PaymentResponse>>();
var defaultProcessorHealth = new PaymentProcessorHealth();
var fallbackProcessorHealth = new PaymentProcessorHealth();

// Start health check threads
var healthCheckInterval = TimeSpan.FromSeconds(5);
var processorHealth = app.Services.GetRequiredService<MonitorProcessor>();
_ = Task.Run(() => processorHealth.MonitorProcessorHealth(paymentProcessorDefaultUrl!, defaultProcessorHealth, healthCheckInterval));
_ = Task.Run(() => processorHealth.MonitorProcessorHealth(paymentProcessorFallbackUrl!, fallbackProcessorHealth, healthCheckInterval));

// Start payment processing tasks
var processorCount = Environment.ProcessorCount * 2; // Scale based on CPU cores
for (var i = 0; i < processorCount; i++)
{
    _ = Task.Run(async () =>
    {
        var processor = app.Services.GetRequiredService<PaymentProcessor>();
        await foreach (var payment in paymentChannel.Reader.ReadAllAsync())
        {
            await processor.ProcessPaymentsAsync(
                paymentProcessorDefaultUrl!,
                paymentProcessorFallbackUrl!,
                payment,
                responseDictionary,
                defaultProcessorHealth,
                fallbackProcessorHealth);
        }
    });
}

app.MapPost("/payments", async (PaymentRequest payment, CancellationToken token = default) =>
{
    if (string.IsNullOrEmpty(payment.CorrelationId) || payment.Amount <= 0)
        return Results.BadRequest("Invalid payment request.");

    var tcs = new TaskCompletionSource<PaymentResponse>();
    responseDictionary[payment.CorrelationId] = tcs;

    if (!await paymentChannel.Writer.WaitToWriteAsync(token)) // Timeout if channel is full
        return Results.StatusCode(503); // "Service unavailable, queue is full."

    await paymentChannel.Writer.WriteAsync(payment);

    var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10), token));
    if (completedTask == tcs.Task && tcs.Task.IsCompletedSuccessfully)
    {
        var paymentResponse = tcs.Task.Result;
        return paymentResponse.Success
            ? Results.Ok(new { Message = "Payment processed successfully." })
            : Results.StatusCode(503); // "Payment processing failed."
    }

    return Results.StatusCode(504); // "Payment processing timed out."
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
    app.MapOpenApi();
app.UseHttpsRedirection();

app.Run();
