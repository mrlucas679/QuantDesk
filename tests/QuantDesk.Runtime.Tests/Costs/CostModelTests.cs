using QuantDesk.Domain.Numerics;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Tests.TestData;

namespace QuantDesk.Runtime.Tests.Costs;

public sealed class CostModelTests
{
    [Fact]
    public void CryptoCostModel_SeparatelyChargesSpreadFeesAndSlippage()
    {
        var model = new CryptoCostModel(new BasisPoints(10), new BasisPoints(5));

        var estimate = model.Estimate(FinancialTestData.Candidate(notional: 1_000), FinancialTestData.HealthyMarket());

        Assert.True(estimate.Total.Value > estimate.EntrySpreadCost.Value);
        Assert.Equal(estimate.EntrySpreadCost, estimate.ExitSpreadCost);
    }
}

