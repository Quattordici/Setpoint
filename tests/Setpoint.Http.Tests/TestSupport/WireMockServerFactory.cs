// Adapted from the shared test infrastructure in the csharp-xunit-test-generator conventions.
using System.Globalization;
using System.Net.Sockets;
using WireMock.Logging;
using WireMock.Server;
using WireMock.Settings;

namespace Setpoint.Http.Tests.TestSupport;

/// <summary>
/// Factory for creating and disposing WireMock servers with consistent settings across all tests.
/// </summary>
/// <remarks>
/// Centralises WireMock server configuration and lifecycle management. All integration and unit tests
/// that need to intercept HTTP calls must use this factory - never call
/// <c>WireMockServer.Start(...)</c>
/// directly. Port allocation is delegated to <see cref="WireMockPortAllocator"/> to prevent conflicts
/// in parallel test execution.
/// </remarks>
internal static class WireMockServerFactory
{
    private const int MaxStartupAttempts = 5;
    private const int StartupRetryBaseDelayMilliseconds = 20;
    private const int ReadinessMaxAttempts = 25;
    private const int ReadinessRetryDelayMilliseconds = 20;
    private const int ReadinessConnectTimeoutMilliseconds = 100;

    /// <summary>
    /// Creates a new WireMock server with automatic port allocation and default test settings.
    /// </summary>
    /// <param name="serverName">Friendly name used in diagnostic output to identify this server.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A started <see cref="WireMockServer"/> ready to receive stub registrations.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a port cannot be allocated or the server fails to start.
    /// </exception>
    public static async Task<WireMockServer> CreateAsync(string serverName, CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= MaxStartupAttempts; attempt++)
        {
            if (!WireMockPortAllocator.TryAllocatePort(out int port))
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Failed to start WireMock server '{serverName}'. No free port could be allocated (attempt {attempt}/{MaxStartupAttempts})."));
            }

            WireMockServerSettings settings = CreateDefaultSettings(port);
            WireMockServer? server = null;

            try
            {
                server = WireMockServer.Start(settings);
                string boundUrl = server.Urls is { Length: > 0 } ? server.Urls[0] : string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}");
                if (!Uri.TryCreate(boundUrl, UriKind.Absolute, out Uri? boundUri))
                {
                    throw new InvalidOperationException(
                        string.Create(CultureInfo.InvariantCulture, $"WireMock server '{serverName}' returned an invalid bound URL '{boundUrl}'."));
                }

                await EnsureServerReadinessAsync(boundUri, cancellationToken);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"WireMock server '{serverName}' started at: {boundUrl} (port {port}, attempt {attempt}/{MaxStartupAttempts})"));
                return server;
            }
            catch (Exception ex) when (attempt < MaxStartupAttempts && (IsPortBindingConflict(ex) || IsServerReadinessFailure(ex)))
            {
                DisposeServerIfCreated(server);
                WireMockPortAllocator.ReleasePort(port);
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
            catch (Exception ex)
            {
                DisposeServerIfCreated(server);
                WireMockPortAllocator.ReleasePort(port);
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"Failed to start WireMock server '{serverName}' on port {port} (attempt {attempt}/{MaxStartupAttempts})."),
                    ex);
            }
        }

        throw new InvalidOperationException(
            string.Create(CultureInfo.InvariantCulture, $"Failed to start WireMock server '{serverName}' after {MaxStartupAttempts} attempts due to repeated port binding conflicts."));
    }

    internal static async Task EnsureServerReadinessAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        for (int attempt = 1; attempt <= ReadinessMaxAttempts; attempt++)
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ReadinessConnectTimeoutMilliseconds);

            try
            {
                using TcpClient tcpClient = new();
                await tcpClient.ConnectAsync(endpoint.Host, endpoint.Port, timeoutCts.Token);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
            }

            if (attempt == ReadinessMaxAttempts)
            {
                break;
            }

            await Task.Delay(ReadinessRetryDelayMilliseconds, cancellationToken);
        }

        throw new TimeoutException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"WireMock endpoint '{endpoint}' did not become ready after {ReadinessMaxAttempts} attempts."));
    }

    /// <summary>
    /// Stops the server, releases its allocated port, and frees all associated resources.
    /// </summary>
    /// <param name="server">The server to dispose. Safe to call if already stopped.</param>
    public static void Dispose(WireMockServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        int? allocatedPort = null;

        // Extract port before stopping so we can release it in the allocator.
        if (server.Urls is { Length: > 0 } &&
            Uri.TryCreate(server.Urls[0], UriKind.Absolute, out Uri? uri))
        {
            allocatedPort = uri.Port;
        }

        try
        {
            server.Stop();
            server.Dispose();
        }
        finally
        {
            if (allocatedPort.HasValue)
            {
                WireMockPortAllocator.ReleasePort(allocatedPort.Value);
            }
        }
    }

    /// <summary>
    /// Resets all registered stubs and recorded requests on the server without stopping it.
    /// Call this at the start of every <c>// Arrange</c> section in collection-fixture tests
    /// to prevent stub state from leaking between tests.
    /// </summary>
    /// <param name="server">The server to reset.</param>
    public static void Reset(WireMockServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Reset();
    }

    private static WireMockServerSettings CreateDefaultSettings(int port)
    {
        return new()
        {
            Urls = [string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}")],
            StartAdminInterface = false,
            Logger = new WireMockNullLogger(),
        };
    }

    internal static bool IsPortBindingConflict(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socketException &&
                socketException.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return true;
            }

            if (current.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("only one usage of each socket address", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsServerReadinessFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }

    internal static async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        int delayMilliseconds = checked(StartupRetryBaseDelayMilliseconds * attempt);
        if (delayMilliseconds <= 0)
        {
            return;
        }

        await Task.Delay(delayMilliseconds, cancellationToken);
    }

    private static void DisposeServerIfCreated(WireMockServer? server)
    {
        if (server is null)
        {
            return;
        }

        try
        {
            server.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        server.Dispose();
    }
}
