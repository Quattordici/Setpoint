using System.Net;

namespace Setpoint.Http.Tests.TestSupport;

/// <summary>A primary handler that answers 200 after a short delay and records the most requests it saw in flight at once.</summary>
internal sealed class InFlightCountingHandler(TimeSpan delay) : HttpMessageHandler
{
    private int _inFlight;
    private int _maxInFlight;

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
        {
        }

        try
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}
