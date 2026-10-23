namespace NetRoute.Service;

/// <summary>
/// A "something changed" flag the service loop can wait on with a timeout.
///
/// <para>The loop used to start a semaphore wait every 10 seconds and abandon it when the delay
/// won. A semaphore hands a later release to its oldest waiter, which was an abandoned one, so
/// from the second idle tick on a network change woke nobody, and a waiter leaked every tick.
/// Each wait here is cancelled when it ends, so none is left queued.</para>
/// </summary>
public sealed class WakeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);
    private long _lastRaisedTicks;

    /// <summary>When <see cref="Raise"/> was last called, or the default if never.</summary>
    public DateTimeOffset LastRaised => new(Volatile.Read(ref _lastRaisedTicks), TimeSpan.Zero);

    public void Raise()
    {
        Volatile.Write(ref _lastRaisedTicks, DateTimeOffset.UtcNow.UtcTicks);
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already pending; one wake-up is all a burst of changes needs.
        }
    }

    /// <summary>True if raised, false if <paramref name="timeout"/> passed first.</summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, attempt.Token);
        var wake = _signal.WaitAsync(attempt.Token);
        await Task.WhenAny(delay, wake);

        // Withdraws whichever of the two lost, so no waiter stays queued for a later Raise to feed.
        attempt.Cancel();

        // The semaphore removes a cancelled waiter on a continuation, not inside Cancel, so a Raise
        // in that gap can still be given to this waiter. Settling it here means that raise is
        // honoured rather than lost.
        try
        {
            await wake;
            return true;
        }
        catch (OperationCanceledException)
        {
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }
}
