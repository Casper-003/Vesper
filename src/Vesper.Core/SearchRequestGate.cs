namespace Vesper.Core;

/// <summary>Publication authority. Cancellation saves work; checking the ticket at the
/// actual write boundary also rejects work that ignored cancellation or was queued earlier.</summary>
public sealed class SearchRequestGate : IDisposable
{
    private readonly object sync = new();
    private long queryId, session;
    private bool active, disposed;
    private CancellationTokenSource? pending;

    public SearchRequest BeginEdit()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending?.Cancel(); pending?.Dispose();
            pending = new(); active = true;
            return new(++queryId, session, pending.Token);
        }
    }

    public bool CanPublish(SearchRequest request)
    {
        lock (sync) return !disposed && active && request.QueryId == queryId
            && request.Session == session && !request.CancellationToken.IsCancellationRequested;
    }

    public void Leave()
    {
        lock (sync)
        {
            active = false; session++; queryId++;
            pending?.Cancel(); pending?.Dispose(); pending = null;
        }
    }

    public void Dispose()
    {
        Leave();
        lock (sync) disposed = true;
    }
}

public sealed record SearchRequest(long QueryId, long Session, CancellationToken CancellationToken);
