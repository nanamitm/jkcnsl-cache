namespace jkcnsl_cache;

public sealed class LocalStreamConnectionLimiter
{
    private int _current;

    public int Current => Volatile.Read(ref _current);

    public bool TryAcquire(int maxTotalClients, out Lease? lease)
    {
        lease = null;
        if (maxTotalClients <= 0)
        {
            Interlocked.Increment(ref _current);
            lease = new Lease(this);
            return true;
        }

        while (true)
        {
            var current = Volatile.Read(ref _current);
            if (current >= maxTotalClients)
                return false;
            if (Interlocked.CompareExchange(ref _current, current + 1, current) == current)
            {
                lease = new Lease(this);
                return true;
            }
        }
    }

    private void Release() => Interlocked.Decrement(ref _current);

    public sealed class Lease : IDisposable
    {
        private LocalStreamConnectionLimiter? _owner;

        public Lease(LocalStreamConnectionLimiter owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
