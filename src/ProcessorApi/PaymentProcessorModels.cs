namespace ProcessorApi;

public record PaymentRequest(string CorrelationId, decimal Amount);

public record PaymentResponse(string CorrelationId, decimal Amount, bool Success);

record ServiceHealth(bool Failing, int MinResponseTime);

record PaymentSummary(int TotalRequests = 0, decimal TotalAmount = 0.0m);

public class PaymentProcessorHealth
{
    private readonly ReaderWriterLockSlim _lock = new();
    private bool _isHealthy;

    public bool IsHealthy
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _isHealthy;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
        set
        {
            _lock.EnterWriteLock();
            try
            {
                _isHealthy = value;
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }
    }

    public DateTime LastCheck { get; set; }
    public int MinResponseTime { get; set; }
}


internal static class Constants
{
    internal const string HeaderClientSourceName = "X-Client-Source";
    internal const string DefaultClient = "DefaultUrl";
    internal const string FallbackClient = "FallbackUrl";
    internal const ushort HttpClientTimeoutInSeconds = 10;
    internal const ushort HealthCheckIntervalInSeconds = 5;
    internal const ushort QueueLimit = 1000;
    internal const ushort HttpTimeoutInSeconds = 30;
}