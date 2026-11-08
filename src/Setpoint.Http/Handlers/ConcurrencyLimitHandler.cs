namespace Setpoint.Http.Handlers;

/// <summary>
/// Lets at most a fixed number of requests to one target be in flight at once. It sits inside the retry
/// handler, so a request waiting out a back-off doesn't hold a slot.
/// </summary>
internal sealed class ConcurrencyLimitHandler(int maxConcurrency) : DelegatingHandler
{
    private readonly SemaphoreSlim _slots = new(maxConcurrency, maxConcurrency);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _slots.Dispose();
        }

        base.Dispose(disposing);
    }
}
