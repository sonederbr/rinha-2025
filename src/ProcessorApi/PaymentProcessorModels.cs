namespace ProcessorApi;

public record PaymentRequest(string CorrelationId, decimal Amount, string RequestedAt);

public record PaymentResponse(string CorrelationId, decimal Amount, DateTimeOffset RequestedAt, bool Success);

record HealthStatus(bool Failing, int MinResponseTime);

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

    public int MinResponseTime { get; set; }
}

internal static class Constants
{
    internal const string HeaderClientSourceName = "X-Client-Source";
    internal const string DefaultClient = "DefaultUrl";
    internal const string FallbackClient = "FallbackUrl";
    internal const ushort HealthCheckIntervalInSeconds = 5;
    internal const ushort QueueLimit = 10000;
    internal const ushort HttpTimeoutInSeconds = 45;
}