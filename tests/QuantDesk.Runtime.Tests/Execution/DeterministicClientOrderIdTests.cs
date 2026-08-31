using QuantDesk.Runtime.Execution;

namespace QuantDesk.Runtime.Tests.Execution;

public sealed class DeterministicClientOrderIdTests
{
    private const string Identity = "BTC/USD|0|crypto-long-momentum-v1|1234567890|42";

    [Fact]
    public void TheSameOpportunityAlwaysYieldsTheSameId()
    {
        string first = DeterministicClientOrderId.Create("auto", Identity, "entry");
        string second = DeterministicClientOrderId.Create("auto", Identity, "entry");

        // This is the property that makes ambiguous-submit recovery possible at all.
        Assert.Equal(first, second);
    }

    [Fact]
    public void EntryAndExitLegsNeverCollide() =>
        Assert.NotEqual(
            DeterministicClientOrderId.Create("auto", Identity, "entry"),
            DeterministicClientOrderId.Create("auto", Identity, "exit"));

    [Fact]
    public void DifferentOpportunitiesYieldDifferentIds() =>
        Assert.NotEqual(
            DeterministicClientOrderId.Create("auto", Identity, "entry"),
            DeterministicClientOrderId.Create("auto", Identity + "|x", "entry"));

    [Fact]
    public void DifferentLanesNeverCollide() =>
        Assert.NotEqual(
            DeterministicClientOrderId.Create("auto", Identity, "entry"),
            DeterministicClientOrderId.Create("opt", Identity, "entry"));

    [Fact]
    public void IdsAreCaseInsensitiveInLaneAndLegButStableInIdentity()
    {
        Assert.Equal(
            DeterministicClientOrderId.Create("auto", Identity, "entry"),
            DeterministicClientOrderId.Create("AUTO", Identity, "ENTRY"));
        Assert.NotEqual(
            DeterministicClientOrderId.Create("auto", Identity, "entry"),
            DeterministicClientOrderId.Create("auto", Identity.ToLowerInvariant(), "entry"));
    }

    [Fact]
    public void IdIsBrokerSafeAndCarriesItsLaneAndLeg()
    {
        string id = DeterministicClientOrderId.Create("auto", Identity, "entry");

        Assert.StartsWith("qd-auto-", id, StringComparison.Ordinal);
        Assert.EndsWith("-entry", id, StringComparison.Ordinal);
        Assert.True(id.Length <= 48);
        Assert.All(id, character =>
            Assert.True(char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyComponentsAreRejected(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => DeterministicClientOrderId.Create(value!, Identity, "entry"));
        Assert.ThrowsAny<ArgumentException>(() => DeterministicClientOrderId.Create("auto", value!, "entry"));
        Assert.ThrowsAny<ArgumentException>(() => DeterministicClientOrderId.Create("auto", Identity, value!));
    }
}
