using QuantDesk.Runtime.Reliability;

namespace QuantDesk.Runtime.Tests.Reliability;

/// <summary>
/// The fault campaign, and the property that makes its result mean anything.
///
/// These tests previously asserted <c>Assert.Equal(21, report.Passed)</c> and
/// <c>Assert.All(report.Cases, item =&gt; Assert.True(item.Passed))</c>. Both held unconditionally,
/// because every case was answered by a switch returning the disposition the case declared it
/// expected -- the campaign compared a constant against itself, reported 21 of 21, and ran no
/// production code. The tests did not catch that; they encoded it.
///
/// What is asserted now is the opposite: that a case nobody has driven cannot report a pass.
/// </summary>
public sealed class FaultCampaignTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"quantdesk-faults-{Guid.NewGuid():N}.json");

    [Fact]
    public void EveryCaseThatRanContainedItsFault()
    {
        FaultCampaignReport report = FaultCampaign.Run(DateTimeOffset.Parse("2026-09-02T08:00:00Z"));

        Assert.Equal(21, report.Total);
        Assert.Equal(21, report.Cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.True(report.NoExercisedFailure, "A case that ran did not contain its fault.");
    }

    [Fact]
    public void ACaseWithNoDriverIsNeverCountedAsPassed()
    {
        // The whole point. An unexercised case reporting a pass is what let this campaign claim a
        // clean release for its entire existence, and it is a dependency of API start-up.
        FaultCampaignReport report = FaultCampaign.Run();

        Assert.All(
            report.Cases.Where(item => !item.Exercised),
            item =>
            {
                Assert.Null(item.ObservedDisposition);
                Assert.False(item.Passed);
            });
    }

    [Fact]
    public void CoverageIsReportedRatherThanImplied()
    {
        FaultCampaignReport report = FaultCampaign.Run();

        Assert.Equal(report.Cases.Count(item => item.Exercised), report.Exercised);
        Assert.Equal(report.Cases.Count(item => item.Passed), report.Passed);
        Assert.Equal(report.Exercised == report.Total && report.Passed == report.Total, report.FullyCovered);
    }

    [Fact]
    public void SomeCasesAreActuallyDrivenThroughProductionCode()
    {
        // Guards the regression directly: a campaign that exercised nothing would satisfy every
        // other assertion here, since "no case that ran, failed" is vacuously true of zero cases.
        FaultCampaignReport report = FaultCampaign.Run();

        Assert.True(report.Exercised > 0, "No case drives production code.");
        Assert.All(
            report.Cases.Where(item => item.Exercised),
            item => Assert.NotNull(item.ObservedDisposition));
    }

    [Fact]
    public void OnlyRecoveryAndReconciliationCasesPermitBrokerMutation()
    {
        FaultCampaignReport report = FaultCampaign.Run();

        Assert.All(
            report.Cases.Where(item => item.BrokerMutationAllowed && item.Exercised),
            item => Assert.Contains(
                item.ObservedDisposition,
                new FaultDisposition?[] { FaultDisposition.RecoverExisting, FaultDisposition.Reconcile }));

        Assert.All(
            report.Cases.Where(item => item.ObservedDisposition is
                FaultDisposition.RejectInput or FaultDisposition.Abstain or FaultDisposition.HaltLane),
            item => Assert.False(item.BrokerMutationAllowed));
    }

    [Fact]
    public void ReportSurvivesPersistenceRoundTrip()
    {
        FaultCampaignReport expected = FaultCampaign.Run();
        FaultCampaign.Save(expected, _path);

        FaultCampaignReport actual = Assert.IsType<FaultCampaignReport>(FaultCampaign.Load(_path));

        Assert.Equal(expected.CampaignId, actual.CampaignId);
        Assert.Equal(expected.Total, actual.Total);
        Assert.Equal(expected.Exercised, actual.Exercised);
        Assert.Equal(expected.Passed, actual.Passed);
        Assert.Equal(expected.Cases, actual.Cases);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
