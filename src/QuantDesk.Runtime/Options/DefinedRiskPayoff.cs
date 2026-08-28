using QuantDesk.Domain.Numerics;

namespace QuantDesk.Runtime.Options;

public static class DefinedRiskPayoff
{
    public static (Usd MaxLoss, Usd MaxProfit, decimal Breakeven) BullCallDebitSpread(
        decimal lowerStrike,
        decimal upperStrike,
        decimal longPremium,
        decimal shortPremium,
        int multiplier)
    {
        if (lowerStrike <= 0 || upperStrike <= lowerStrike || longPremium < 0 || shortPremium < 0 || multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(lowerStrike), "Bull-call spread inputs are invalid.");

        decimal debit = (longPremium - shortPremium) * multiplier;
        if (debit < 0) throw new ArgumentException("A debit spread cannot have negative net debit.");
        decimal width = (upperStrike - lowerStrike) * multiplier;
        return (new Usd(debit), new Usd(width - debit), lowerStrike + (longPremium - shortPremium));
    }

    public static (Usd MaxLoss, Usd MaxProfit, decimal Breakeven) BearPutDebitSpread(
        decimal lowerStrike,
        decimal upperStrike,
        decimal longPremium,
        decimal shortPremium,
        int multiplier)
    {
        if (lowerStrike <= 0 || upperStrike <= lowerStrike || longPremium < 0 || shortPremium < 0 || multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(lowerStrike), "Bear-put spread inputs are invalid.");
        decimal debit = (longPremium - shortPremium) * multiplier;
        if (debit < 0) throw new ArgumentException("A debit spread cannot have negative net debit.");
        decimal width = (upperStrike - lowerStrike) * multiplier;
        return (new Usd(debit), new Usd(width - debit), upperStrike - (longPremium - shortPremium));
    }
}
