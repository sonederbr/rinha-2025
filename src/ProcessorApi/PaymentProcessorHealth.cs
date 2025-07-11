using System.Threading;

namespace ProcessorApi;

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
