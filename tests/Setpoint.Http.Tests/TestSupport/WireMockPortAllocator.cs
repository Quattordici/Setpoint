// Adapted from the shared test infrastructure in the csharp-xunit-test-generator conventions.
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Setpoint.Http.Tests.TestSupport;

/// <summary>
/// Thread-safe port allocator for WireMock servers in test environments.
/// Implements industry best practices for test infrastructure with configurable port allocation strategies.
/// </summary>
/// <remarks>
/// <para>
/// This utility class provides robust port allocation for WireMock HTTP mock servers across both
/// unit and integration tests. It prevents port conflicts in parallel test execution and CI/CD environments.
/// </para>
/// <para>
/// Port allocation follows a configurable precedence order:
/// <list type="number">
/// <item><description>Fixed port (WIREMOCK_FIXED_PORT) - useful for debugging specific tests</description></item>
/// <item><description>Port range (WIREMOCK_PORT_RANGE) - recommended for CI/CD environments</description></item>
/// <item><description>Legacy start port (WIREMOCK_PORT_START) - backward compatibility</description></item>
/// <item><description>Default safe range (8000-8999) - fallback for local development</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class WireMockPortAllocator
{
    private static readonly ConcurrentDictionary<int, byte> s_allocatedPorts = [];

    private static int s_defaultPortCursor = DefaultRangeStart - 1;

    /// <summary>Environment variable name for forcing a specific WireMock port.</summary>
    public const string FixedPortEnvironmentVariable = "WIREMOCK_FIXED_PORT";

    /// <summary>Environment variable name for configuring an inclusive WireMock port range (e.g. 8000-8999).</summary>
    public const string PortRangeEnvironmentVariable = "WIREMOCK_PORT_RANGE";

    /// <summary>Environment variable name for configuring a legacy WireMock start port.</summary>
    public const string LegacyStartEnvironmentVariable = "WIREMOCK_PORT_START";

    /// <summary>Default starting port for the safe range when no environment variables are set.</summary>
    public const int DefaultRangeStart = 8000;

    /// <summary>Default ending port for the safe range when no environment variables are set.</summary>
    public const int DefaultRangeEnd = 9999;

    /// <summary>Lowest valid TCP port number accepted by this allocator.</summary>
    public const int MinTcpPort = 1;

    /// <summary>Highest valid TCP port number accepted by this allocator.</summary>
    public const int MaxTcpPort = 65535;

    /// <summary>Maximum number of retry attempts when port allocation fails due to race conditions.</summary>
    public const int MaxRetries = 5;

    /// <summary>
    /// Allocates an available port for WireMock server using configured allocation strategy.
    /// </summary>
    /// <returns>An available port number that has been reserved.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when unable to allocate a port after maximum retry attempts.
    /// </exception>
    public static int AllocatePort()
    {
        return TryAllocatePort(out int port)
            ? port
            : throw new InvalidOperationException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Unable to allocate a WireMock port. Checked configured strategies ({FixedPortEnvironmentVariable}, {PortRangeEnvironmentVariable}, {LegacyStartEnvironmentVariable}) and default range {DefaultRangeStart}-{DefaultRangeEnd}."));
    }

    /// <summary>
    /// Tries to allocate an available port for WireMock server using configured allocation strategy.
    /// </summary>
    /// <param name="port">When this method returns, contains the allocated port if successful; otherwise <c>0</c>.</param>
    /// <returns><see langword="true"/> when a port is successfully allocated; otherwise <see langword="false"/>.</returns>
    public static bool TryAllocatePort(out int port)
    {
        port = 0;

        // Strategy 1: Fixed port (debugging)
        string? fixedPortEnv = Environment.GetEnvironmentVariable(FixedPortEnvironmentVariable);
        if (int.TryParse(fixedPortEnv, CultureInfo.InvariantCulture, out int fixedPort) &&
            IsValidPort(fixedPort) &&
            TryReservePortUnsafe(fixedPort))
        {
            port = fixedPort;
            return true;
        }

        // Strategy 2: Port range (recommended for CI/CD)
        string? rangeEnv = Environment.GetEnvironmentVariable(PortRangeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(rangeEnv))
        {
            string[] parts = rangeEnv.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], CultureInfo.InvariantCulture, out int rangeStart) &&
                int.TryParse(parts[1], CultureInfo.InvariantCulture, out int rangeEnd) &&
                IsValidRange(rangeStart, rangeEnd))
            {
                int? rangedPort = FindAvailablePortUnsafe(rangeStart, rangeEnd, ref s_defaultPortCursor);
                if (rangedPort.HasValue)
                {
                    port = rangedPort.Value;
                    return true;
                }
            }
        }

        // Strategy 3: Legacy start port (backward compatibility)
        if (int.TryParse(Environment.GetEnvironmentVariable(LegacyStartEnvironmentVariable), CultureInfo.InvariantCulture, out int legacyStart) &&
            IsValidPort(legacyStart))
        {
            int legacyEnd = Math.Min(legacyStart + 100, MaxTcpPort);
            int? legacyPort = FindAvailablePortUnsafe(legacyStart, legacyEnd, ref s_defaultPortCursor);
            if (legacyPort.HasValue)
            {
                port = legacyPort.Value;
                return true;
            }
        }

        // Strategy 4: Default safe range (fallback)
        int? defaultPort = FindAvailablePortUnsafe(DefaultRangeStart, DefaultRangeEnd, ref s_defaultPortCursor);
        if (defaultPort.HasValue)
        {
            port = defaultPort.Value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Releases a previously allocated port, making it available for future allocations.
    /// </summary>
    /// <param name="port">The port number to release.</param>
    public static void ReleasePort(int port)
    {
        _ = s_allocatedPorts.TryRemove(port, out _);
    }

    /// <summary>
    /// Releases all allocated ports. Useful for test cleanup or resetting test state.
    /// </summary>
    public static void ReleaseAllPorts()
    {
        s_allocatedPorts.Clear();
    }

    /// <summary>
    /// Gets the count of currently allocated ports. Useful for diagnostics and monitoring.
    /// </summary>
    public static int AllocatedPortCount => s_allocatedPorts.Count;

    /// <summary>
    /// Attempts to reserve a port by probing TCP bind and then atomically recording ownership.
    /// </summary>
    /// <param name="port">The port number to reserve.</param>
    /// <returns><see langword="true"/> if the port was successfully reserved; otherwise, <see langword="false"/>.</returns>
    private static bool TryReservePortUnsafe(int port)
    {
        return IsValidPort(port)
            && !s_allocatedPorts.ContainsKey(port)
            && CanBindOnLoopback(port, AddressFamily.InterNetwork)
            && CanBindOnLoopback(port, AddressFamily.InterNetworkV6)
            && s_allocatedPorts.TryAdd(port, 0);
    }

    private static bool CanBindOnLoopback(int port, AddressFamily addressFamily)
    {
        if (addressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            return true;
        }

        IPAddress loopbackAddress = addressFamily == AddressFamily.InterNetwork
            ? IPAddress.Loopback
            : IPAddress.IPv6Loopback;

        try
        {
            using TcpListener tcpListener = new(loopbackAddress, port);
            tcpListener.Start();
            tcpListener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int? FindAvailablePortUnsafe(int start, int end, ref int cursor)
    {
        if (!IsValidRange(start, end))
        {
            return null;
        }

        int rangeSize = end - start + 1;
        int seed = Interlocked.Increment(ref cursor);
        int offset = (int)((uint)seed % (uint)rangeSize);
        int firstCandidate = start + offset;

        for (int i = 0; i < rangeSize; i++)
        {
            int candidate = firstCandidate + i;
            if (candidate > end)
            {
                candidate -= rangeSize;
            }

            if (TryReservePortUnsafe(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsValidPort(int port)
    {
        return port is >= MinTcpPort and <= MaxTcpPort;
    }

    private static bool IsValidRange(int start, int end)
    {
        return IsValidPort(start) && IsValidPort(end) && start <= end;
    }
}
