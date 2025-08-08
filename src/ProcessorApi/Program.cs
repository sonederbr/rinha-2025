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
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CreateHttpHandler);

builder.Services.AddHttpClient(Constants.FallbackClient, httpClient =>
    {
        httpClient.BaseAddress = new Uri(paymentProcessorFallbackUrl);
        httpClient.DefaultRequestHeaders.Add(Constants.HeaderClientSourceName, Constants.FallbackClient);
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
    })
    .ConfigurePrimaryHttpMessageHandler(CreateHttpHandler);

var paymentChannel = Channel.CreateBounded<PaymentRequest>(new BoundedChannelOptions(Constants.QueueLimit)
{
    SingleReader = false, // Allow multiple readers
    SingleWriter = true, // Optimize for a single writer
    FullMode = BoundedChannelFullMode.DropOldest // Wait when the channel is full
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

ThreadPool.SetMinThreads(Environment.ProcessorCount * 10, Environment.ProcessorCount * 10);
var processorCount = Environment.ProcessorCount * 10;
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

var app = builder.Build();
app.MapPost("/payments", async (PaymentRequest payment, CancellationToken cancellationToken = default) =>
{
    if (string.IsNullOrWhiteSpace(payment.CorrelationId) || payment.Amount <= 0)
        return Results.BadRequest("Invalid payment request.");

    var tcs = new TaskCompletionSource<PaymentResponse>();
    responseDictionary[payment.CorrelationId] = tcs;

    if (!await paymentChannel.Writer.WaitToWriteAsync(cancellationToken))
        return Results.InternalServerError("Service unavailable, queue is full.");

    payment = payment with { RequestedAt = DateTime.UtcNow.ToString("o") };
    await paymentChannel.Writer.WriteAsync(payment, cancellationToken);

    var completedTask = await Task.WhenAny(tcs.Task,
        Task.Delay(TimeSpan.FromSeconds(Constants.HttpTimeoutInSeconds), cancellationToken));

    if (completedTask != tcs.Task || !tcs.Task.IsCompletedSuccessfully)
        return Results.InternalServerError("Payment processing timed out.");

    var paymentResponse = await tcs.Task;
    return paymentResponse.Success
        ? Results.Ok(new { Message = "Payment processed successfully." })
        : Results.InternalServerError("Payment processing failed.");
});

app.MapGet("/payments-summary", (HttpRequest req) =>
{
    var fromStr = req.Query["from"].ToString();
    var toStr = req.Query["to"].ToString();

    DateTimeOffset? from = null;
    DateTimeOffset? to = null;

    if (DateTimeOffset.TryParse(fromStr, out var parsedFrom))
        from = parsedFrom;

    if (DateTimeOffset.TryParse(toStr, out var parsedTo))
        to = parsedTo;

    var filteredDefault = paymentsDefault
        .Where(p => (!from.HasValue || p.RequestedAt >= from) && (!to.HasValue || p.RequestedAt <= to))
        .ToList();

    var filteredFallback = paymentsFallback
        .Where(p => (!from.HasValue || p.RequestedAt >= from) && (!to.HasValue || p.RequestedAt <= to))
        .ToList();

    return Results.Ok(new
    {
        @default = new
        {
            totalRequests = filteredDefault.Count,
            totalAmount = filteredDefault.Sum(p => p.Amount)
        },
        fallback = new
        {
            totalRequests = filteredFallback.Count,
            totalAmount = filteredFallback.Sum(p => p.Amount)
        }
    });
});

app.Run();
return;

SocketsHttpHandler CreateHttpHandler() => new()
{
    SslOptions = new SslClientAuthenticationOptions
    {
        RemoteCertificateValidationCallback = (_, _, _, _) => true
    },
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    MaxConnectionsPerServer = 500,
    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
};
// dotnet run --project ProcessorApi.csproj --no-restore --Environment=Production--ServerSettings:Port=9999