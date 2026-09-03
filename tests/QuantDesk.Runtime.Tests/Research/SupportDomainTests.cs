using QuantDesk.Domain.Contracts;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Research;

namespace QuantDesk.Runtime.Tests.Research;

/// <summary>
/// A model may only be asked about what it was fitted on.
///
/// There was exactly one HAR artifact and one GARCH artifact, both fitted on the BTC/USD five-minute
/// series, and both were consulted for SPY, QQQ, IWM and DIA. Nothing detected it and nothing could:
/// the schema hashes matched, the parity cases reproduced, the coefficients loaded. The artifact
/// never said what it was fitted on, so no check could compare that against what it was being asked.
///
/// The store's shape is the fix. A question that cannot be asked without naming an instrument
/// cannot be answered for the wrong one.
/// </summary>
public sealed class SupportDomainTests
{
    private static readonly ExpertSupportDomain Bitcoin =
        new("spot_crypto", ["BTC/USD"], 5);

    [Fact]
    public void AModelFittedOnBitcoinIsNotOfferedForSpy()
    {
        // The live defect, in one assertion.
        FittedModelStore store = FittedModelStore.Of(Bitcoin, Fitted());

        Assert.True(store.Har("BTC/USD", 5).IsFitted);
        Assert.False(store.Har("SPY", 5).IsFitted);
    }

    [Fact]
    public void TheBarIsPartOfTheDomainBecauseTheSchemaHashDoesNotCoverIt()
    {
        // A HAR fitted on five-minute bars and fed one-minute bars has an identical feature schema
        // hash -- same names, same ordering -- and is a different model.
        FittedModelStore store = FittedModelStore.Of(Bitcoin, Fitted());

        Assert.False(store.Har("BTC/USD", 1).IsFitted);
    }

    [Fact]
    public void AnArtifactThatDeclaresNoDomainIsRefusedRatherThanAdoptedEverywhere()
    {
        // Every artifact written before the field existed is in this state, and those are exactly
        // the ones whose reach was never established. Reading silence as universal permission would
        // preserve the defect while looking like a compatibility shim.
        var store = new FittedModelStore();

        Assert.False(store.Adopt(Fitted(), ExpertSupportDomain.Undeclared));
        Assert.False(store.Har("BTC/USD", 5).IsFitted);
    }

    [Fact]
    public void TheBankHoldsSeveralInstrumentsAtOnce()
    {
        // A single field made this impossible by construction: adopting a fresh fit for SPY evicted
        // the one for BTC/USD, so a per-symbol bank could never accumulate.
        var store = new FittedModelStore();
        Assert.True(store.Adopt(Fitted(), Bitcoin));
        Assert.True(store.Adopt(Fitted(), new ExpertSupportDomain("us_equity", ["SPY"], 5)));

        Assert.True(store.Har("BTC/USD", 5).IsFitted);
        Assert.True(store.Har("SPY", 5).IsFitted);
        Assert.False(store.Har("QQQ", 5).IsFitted);
    }

    [Fact]
    public void ARefitForTheSameDomainSupersedesRatherThanAccumulates()
    {
        var store = new FittedModelStore();
        store.Adopt(Fitted(), Bitcoin);
        store.Adopt(Fitted(), Bitcoin);

        Assert.Single(store.Domains);
    }

    [Fact]
    public void AnUndeclaredDomainSupportsNothingAtAll()
    {
        Assert.False(ExpertSupportDomain.Undeclared.IsDeclared);
        Assert.False(ExpertSupportDomain.Undeclared.Supports("BTC/USD", 5));
    }

    [Fact]
    public void SymbolComparisonIgnoresCaseButNotIdentity()
    {
        Assert.True(Bitcoin.Supports("btc/usd", 5));
        Assert.False(Bitcoin.Supports("BTC/USDT", 5));
    }

    [Fact]
    public void TheRefusalSaysWhatWasFittedAndWhatWasAsked()
    {
        // An operator seeing "unfitted" on SPY needs to know whether no model exists or the wrong
        // one was declined, because those call for opposite actions.
        Assert.Contains("BTC/USD", Bitcoin.ExplainRefusal("SPY", 5), StringComparison.Ordinal);
        Assert.Contains("5-minute", Bitcoin.ExplainRefusal("BTC/USD", 1), StringComparison.Ordinal);
        Assert.Contains(
            "no support domain",
            ExpertSupportDomain.Undeclared.ExplainRefusal("SPY", 5),
            StringComparison.Ordinal);
    }

    /// <summary>A HAR that reports itself fitted; the coefficients are irrelevant to routing.</summary>
    private static HarVarianceModel Fitted()
    {
        Assert.True(HarVarianceModel.TryLoad(Artifact(), Runtime, out HarVarianceModel model, out _));
        return model;
    }

    private static readonly string RuntimeHash =
        RealizedVolatilityExpert.FeatureContract.FeatureSchemaHash;

    private static readonly RuntimeFeatureContract Runtime =
        RuntimeFeatureContract.SchemaOnly(RuntimeHash);

    private static FittedModelContract Artifact() => new(
        ArtifactId: "har-support-domain",
        ModelId: "realised-variance",
        ModelType: "har",
        ModelVersion: "1.0.0",
        FeatureSchemaHash: RuntimeHash,
        DatasetHash: "dataset-abc",
        Parameters: new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["intercept"] = 0.001d,
            ["beta_short"] = 0.4d,
            ["beta_medium"] = 0.3d,
            ["beta_long"] = 0.2d,
        },
        RandomSeed: 1,
        EvidenceGrade: "B",
        PromotionState: "VALIDATED",
        GitCommit: "abc1234",
        CreatedAt: DateTimeOffset.Parse("2026-09-02T00:00:00Z"))
    {
        ParityChecks = [new ModelParityCheck([[2d, 3d, 4d]], [2.501d])],
    };
}
