namespace QuantDesk.Runtime.Options;

public static class BlackScholes
{
    public static double NormalCdf(double value) => 0.5 * (1.0 + Erf(value / Math.Sqrt(2.0)));

    public static double EuropeanCall(double spot, double strike, double timeYears, double riskFreeRate, double volatility)
    {
        if (spot <= 0 || strike <= 0 || timeYears <= 0 || volatility <= 0)
            return Math.Max(spot - strike, 0);

        double sqrtTime = Math.Sqrt(timeYears);
        double d1 = (Math.Log(spot / strike) + (riskFreeRate + 0.5 * volatility * volatility) * timeYears) /
            (volatility * sqrtTime);
        double d2 = d1 - volatility * sqrtTime;
        return spot * NormalCdf(d1) - strike * Math.Exp(-riskFreeRate * timeYears) * NormalCdf(d2);
    }

    private static double Erf(double value)
    {
        double sign = Math.Sign(value);
        double x = Math.Abs(value);
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;
        double t = 1.0 / (1.0 + p * x);
        double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return sign * y;
    }
}

