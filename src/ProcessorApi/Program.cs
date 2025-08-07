using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Threading.Channels;
using ProcessorApi;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
const string paymentProcessorDefaultUrl = "http://localhost:8001";
const string paymentProcessorFallbackUrl = "http://localhost:8002";
builder.Services.AddHttpClient(Constants.DefaultClient, httpClient =>
    {
        httpClient.BaseAddress = new Uri(paymentProcessorDefaultUrl);
        httpClient.DefaultRequestHeaders.Add(Constants.HeaderClientSourceName, Constants.DefaultClient);
        httpClient.Timeout = TimeSpan.FromSeconds(Constants.TimeoutInSeconds);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
        },
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), // Refresh stale connections
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 100, // Increase if under high load
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });

builder.Services.AddHttpClient(Constants.FallbackClient, httpClient =>
    {
        httpClient.BaseAddress = new Uri(paymentProcessorFallbackUrl);
        httpClient.DefaultRequestHeaders.Add(Constants.HeaderClientSourceName, Constants.FallbackClient);
        httpClient.Timeout = TimeSpan.FromSeconds(Constants.TimeoutInSeconds);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
        },
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), // Refresh stale connections
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 100, // Increase if under high load
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });

var paymentChannel = Channel.CreateBounded<PaymentRequest>(new BoundedChannelOptions(Constants.QueueLimit)
{
    SingleReader = false, // Allow multiple readers
    SingleWriter = true, // Optimize for a single writer
    FullMode = BoundedChannelFullMode.Wait // Wait when the channel is full
});
var responseDictionary = new ConcurrentDictionary<string, TaskCompletionSource<PaymentResponse>>();
var defaultProcessorHealth = new PaymentProcessorHealth();
var fallbackProcessorHealth = new PaymentProcessorHealth();
var paymentsDefault = new List<PaymentResponse>();
var paymentsFallback = new List<PaymentResponse>();

builder.Services.AddOpenApi();
builder.Services.AddSingleton<PaymentProcessor>();
builder.Services.AddSingleton<HealthCheckProcessor>();

builder.Services.AddHostedService<HealthCheckWorker>(provider => new HealthCheckWorker(
    provider.GetRequiredService<HealthCheckProcessor>(),
    defaultProcessorHealth,
    fallbackProcessorHealth,
    TimeSpan.FromSeconds(Constants.HealthCheckIntervalInSeconds)));

ThreadPool.SetMinThreads(Environment.ProcessorCount * 4, Environment.ProcessorCount * 4);
var processorCount = Environment.ProcessorCount * 4;
for (var i = 0; i < processorCount; i++)
{
    builder.Services.AddHostedService<PaymentWorker>(provider => new PaymentWorker(
        paymentChannel,
        responseDictionary,
        provider.GetRequiredService<PaymentProcessor>(),
        defaultProcessorHealth,
        fallbackProcessorHealth,
        paymentsDefault,
        paymentsFallback));
}

await paymentChannel.Writer.WriteAsync(new PaymentRequest(Guid.NewGuid().ToString(), 0.0m));

var app = builder.Build();
app.MapPost("/payments", async (PaymentRequest payment, CancellationToken cancellationToken = default) =>
{
    if (string.IsNullOrWhiteSpace(payment.CorrelationId) || payment.Amount <= 0)
        return Results.BadRequest("Invalid payment request.");

    var tcs = new TaskCompletionSource<PaymentResponse>();
    responseDictionary[payment.CorrelationId] = tcs;

    if (!await paymentChannel.Writer.WaitToWriteAsync(cancellationToken))
        return Results.InternalServerError("Service unavailable, queue is full.");

    await paymentChannel.Writer.WriteAsync(payment, cancellationToken);

    var completedTask = await Task.WhenAny(tcs.Task,
        Task.Delay(TimeSpan.FromSeconds(Constants.TimeoutInSeconds), cancellationToken));

    if (completedTask != tcs.Task || !tcs.Task.IsCompletedSuccessfully)
        return Results.InternalServerError("Payment processing timed out.");

    var paymentResponse = await tcs.Task;
    return paymentResponse.Success
        ? Results.Ok(new { Message = "Payment processed successfully." })
        : Results.InternalServerError("Payment processing failed.");
});

app.MapGet("/payments-summary", (HttpRequest req) =>
{
    var from = req.Query["from"];
    var to = req.Query["to"];

    return Task.FromResult(Results.Ok(new
    {
        Default = new PaymentSummary()
            { TotalAmount = paymentsDefault.Sum(p => p.Amount), TotalRequests = paymentsDefault.Count() },
        Fallback = new PaymentSummary()
            { TotalAmount = paymentsFallback.Sum(p => p.Amount), TotalRequests = paymentsFallback.Count() }
    }));
});

app.Run();