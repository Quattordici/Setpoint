using Azure.Core;
using NSubstitute;
using Setpoint.Adapters.AzureDevOps.Auth;

namespace Setpoint.Adapters.AzureDevOps.Tests.Auth;

/// <summary>
/// Tests for <see cref="EntraTokenSource"/>: the Azure DevOps scope, reusing a token until five minutes before it
/// expires, and registering every token with the redactor.
/// </summary>
public sealed class EntraTokenSourceTests : IDisposable
{
    private static readonly DateTimeOffset s_now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly TokenCredential _credential = Substitute.For<TokenCredential>();
    private readonly ManualClock _time = new(s_now);
    private readonly List<string> _registered = [];
    private readonly EntraTokenSource _sut;

    public EntraTokenSourceTests() => _sut = new EntraTokenSource(_credential, _time, _registered.Add);

    public void Dispose() => _sut.Dispose();

    /// <summary>
    /// Given a credential that mints an hour-long token,
    /// when tokens are asked for twice within the hour,
    /// then one token is minted for the Azure DevOps scope, reused, and registered as a secret.
    /// </summary>
    [Fact(DisplayName = "👍 Mints one token for the Azure DevOps scope and reuses it")]
    public async Task Get_Twice_MintsOnceForScope()
    {
        // Arrange
        Mint("token-1", s_now.AddHours(1));

        // Act
        string first = await _sut.GetAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(30));
        string second = await _sut.GetAsync(TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBe("token-1");
        second.ShouldBe("token-1");
        await _credential.Received(1).GetTokenAsync(
            Arg.Is<TokenRequestContext>(c => c.Scopes.Single() == "499b84ac-1321-427f-aa17-267ca6975798/.default"),
            Arg.Any<CancellationToken>());
        _registered.ShouldBe(["token-1"]);
    }

    /// <summary>
    /// Given a token that expires within five minutes,
    /// when a token is asked for,
    /// then a new one is minted.
    /// </summary>
    [Fact(DisplayName = "🖐️ Replaces a token five minutes before it expires")]
    public async Task Get_NearExpiry_MintsAgain()
    {
        // Arrange
        _credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("token-1", s_now.AddHours(1)), new AccessToken("token-2", s_now.AddHours(2)));
        await _sut.GetAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(56));

        // Act
        string token = await _sut.GetAsync(TestContext.Current.CancellationToken);

        // Assert
        token.ShouldBe("token-2");
        _registered.ShouldBe(["token-1", "token-2"]);
    }

    private void Mint(string token, DateTimeOffset expires) =>
        _credential.GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>()).Returns(new AccessToken(token, expires));

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
