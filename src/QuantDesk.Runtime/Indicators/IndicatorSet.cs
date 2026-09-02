namespace QuantDesk.Runtime.Indicators;

/// <summary>
/// The canonical indicators a strategy may read, computed once per evaluation from one bar series.
///
/// Why these are implemented here rather than taken from a library
/// ---------------------------------------------------------------
/// The formula is the thing being tested. A library whose seeding or smoothing differs from the
/// research plane's would make the two disagree about what the same indicator means, and the
/// disagreement would surface as a strategy that backtested well and traded differently -- the
/// hardest class of defect to attribute. These follow the published definitions, and the research
/// scan that selects strategies uses the same ones.
///
/// Wilder's smoothing, not an EMA
/// ------------------------------
/// RSI, ATR and ADX are defined on Wilder's smoothing, which decays at 1/n rather than the 2/(n+1)
/// of a conventional EMA. Substituting one for the other is a common and quiet error: the result
/// still looks like an RSI and still oscillates, but it is a different series, and a threshold
/// calibrated on one is wrong on the other.
///
/// Every series is left NaN until it has enough observations to be real. Nothing is back-filled or
/// zero-padded, because a warm-up value that looks valid is worse than one that is visibly absent:
/// the consumer can decline on a NaN, and cannot decline on a plausible wrong number.
/// </summary>
public sealed class IndicatorSet
{
    private IndicatorSet(int length) => Length = length;

    public int Length { get; }

    public double[] Close { get; private init; } = [];
    public double[] High { get; private init; } = [];
    public double[] Low { get; private init; } = [];
    public double[] Volume { get; private init; } = [];

    public double[] Rsi14 { get; private init; } = [];
    public double[] Atr14 { get; private init; } = [];
    public double[] Ema12 { get; private init; } = [];
    public double[] Ema48 { get; private init; } = [];
    public double[] MacdHistogram { get; private init; } = [];
    public double[] BollingerUpper { get; private init; } = [];
    public double[] BollingerLower { get; private init; } = [];
    public double[] BollingerWidth { get; private init; } = [];
    public double[] StochasticK { get; private init; } = [];
    public double[] StochasticD { get; private init; } = [];
    public double[] Adx14 { get; private init; } = [];
    public double[] PlusDi { get; private init; } = [];
    public double[] MinusDi { get; private init; } = [];
    public double[] DonchianHigh { get; private init; } = [];
    public double[] Vwap48 { get; private init; } = [];
    public double[] ObvSlope12 { get; private init; } = [];
    public double[] VolumeZ48 { get; private init; } = [];

    /// <summary>
    /// Computes every indicator, or returns null when the series is too short or misaligned.
    ///
    /// Null rather than a partly-populated set: a strategy handed a set whose longest indicator is
    /// still warming up would read NaN and decline anyway, and returning something that looks
    /// complete invites a caller to assume it is.
    /// </summary>
    public static IndicatorSet? Build(
        IReadOnlyList<decimal> closes,
        IReadOnlyList<decimal> highs,
        IReadOnlyList<decimal> lows,
        IReadOnlyList<decimal> volumes)
    {
        ArgumentNullException.ThrowIfNull(closes);
        ArgumentNullException.ThrowIfNull(highs);
        ArgumentNullException.ThrowIfNull(lows);
        ArgumentNullException.ThrowIfNull(volumes);

        int n = closes.Count;
        if (n < MinimumBars) return null;
        if (highs.Count != n || lows.Count != n || volumes.Count != n) return null;

        double[] c = [.. closes.Select(value => (double)value)];
        double[] h = [.. highs.Select(value => (double)value)];
        double[] l = [.. lows.Select(value => (double)value)];
        double[] v = [.. volumes.Select(value => (double)value)];
        if (c.Any(value => !double.IsFinite(value) || value <= 0)) return null;

        double[] trueRange = TrueRange(h, l, c);
        (double[] macdHistogram, _) = Macd(c);
        (double[] upper, double[] lower, double[] width) = Bollinger(c, 20, 2.0);
        (double[] k, double[] d) = Stochastic(h, l, c, 14, 3);
        (double[] adx, double[] plus, double[] minus) = DirectionalMovement(h, l, trueRange, 14);

        return new IndicatorSet(n)
        {
            Close = c,
            High = h,
            Low = l,
            Volume = v,
            Rsi14 = Rsi(c, 14),
            Atr14 = WilderSmooth(trueRange, 14),
            Ema12 = Ema(c, 12),
            Ema48 = Ema(c, 48),
            MacdHistogram = macdHistogram,
            BollingerUpper = upper,
            BollingerLower = lower,
            BollingerWidth = width,
            StochasticK = k,
            StochasticD = d,
            Adx14 = adx,
            PlusDi = plus,
            MinusDi = minus,
            DonchianHigh = DonchianHighs(h, 20),
            Vwap48 = RollingVwap(h, l, c, v, 48),
            ObvSlope12 = ObvSlope(c, v, 12),
            VolumeZ48 = ZScore(v, 48),
        };
    }

    /// <summary>
    /// Bars needed before the longest indicator here is meaningful.
    ///
    /// Driven by the 48-period EMA and VWAP: a recursive average needs several times its period
    /// before its seed stops dominating, and 120 gives the slowest of these room to settle.
    /// </summary>
    public const int MinimumBars = 120;

    /// <summary>
    /// A set holding closes and nothing else, for series too short to warm anything up.
    ///
    /// Every derived series stays NaN, so every rule that reads one declines. Only the rules built
    /// directly on closes can act, which is exactly the intent.
    /// </summary>
    public static IndicatorSet Unwarmed(IReadOnlyList<decimal> closes)
    {
        ArgumentNullException.ThrowIfNull(closes);
        double[] c = [.. closes.Select(value => (double)value)];
        return new IndicatorSet(c.Length)
        {
            Close = c,
            High = c,
            Low = c,
            Volume = new double[c.Length],
            Rsi14 = Filled(c.Length),
            Atr14 = Filled(c.Length),
            Ema12 = Ema(c, 12),
            Ema48 = Ema(c, 48),
            MacdHistogram = Macd(c).Histogram,
            BollingerUpper = Filled(c.Length),
            BollingerLower = Filled(c.Length),
            BollingerWidth = Filled(c.Length),
            StochasticK = Filled(c.Length),
            StochasticD = Filled(c.Length),
            Adx14 = Filled(c.Length),
            PlusDi = Filled(c.Length),
            MinusDi = Filled(c.Length),
            DonchianHigh = Filled(c.Length),
            Vwap48 = Filled(c.Length),
            ObvSlope12 = Filled(c.Length),
            VolumeZ48 = Filled(c.Length),
        };
    }

    /// <summary>True when every named series has a finite value at the last bar.</summary>
    public bool IsReadyAt(int index, params double[][] series) =>
        index >= 0 && index < Length && series.All(s => index < s.Length && double.IsFinite(s[index]));

    private static double[] Ema(double[] x, int n)
    {
        double[] output = Filled(x.Length);
        if (x.Length < n) return output;
        double alpha = 2.0 / (n + 1.0);
        output[n - 1] = x.Take(n).Average();
        for (int i = n; i < x.Length; i++) output[i] = (alpha * x[i]) + ((1 - alpha) * output[i - 1]);
        return output;
    }

    /// <summary>Wilder's smoothing: decays at 1/n, which is what RSI, ATR and ADX are defined on.</summary>
    private static double[] WilderSmooth(double[] x, int n)
    {
        double[] output = Filled(x.Length);
        if (x.Length < n) return output;
        output[n - 1] = x.Take(n).Average();
        for (int i = n; i < x.Length; i++) output[i] = ((output[i - 1] * (n - 1)) + x[i]) / n;
        return output;
    }

    private static double[] Rsi(double[] c, int n)
    {
        double[] gain = new double[c.Length];
        double[] loss = new double[c.Length];
        for (int i = 1; i < c.Length; i++)
        {
            double delta = c[i] - c[i - 1];
            gain[i] = Math.Max(delta, 0);
            loss[i] = Math.Max(-delta, 0);
        }

        double[] averageGain = WilderSmooth(gain, n);
        double[] averageLoss = WilderSmooth(loss, n);
        double[] output = Filled(c.Length);
        for (int i = 0; i < c.Length; i++)
        {
            if (!double.IsFinite(averageGain[i]) || !double.IsFinite(averageLoss[i])) continue;
            // A window with no losses is a fully saturated RSI, not a division by zero.
            output[i] = averageLoss[i] <= 0 ? 100.0 : 100.0 - (100.0 / (1 + (averageGain[i] / averageLoss[i])));
        }

        return output;
    }

    private static double[] TrueRange(double[] h, double[] l, double[] c)
    {
        double[] output = new double[c.Length];
        output[0] = h[0] - l[0];
        for (int i = 1; i < c.Length; i++)
        {
            output[i] = Math.Max(h[i] - l[i],
                Math.Max(Math.Abs(h[i] - c[i - 1]), Math.Abs(l[i] - c[i - 1])));
        }

        return output;
    }

    private static (double[] Histogram, double[] Line) Macd(double[] c)
    {
        double[] fast = Ema(c, 12);
        double[] slow = Ema(c, 26);
        double[] line = Filled(c.Length);
        for (int i = 0; i < c.Length; i++)
        {
            if (double.IsFinite(fast[i]) && double.IsFinite(slow[i])) line[i] = fast[i] - slow[i];
        }

        // The signal line is an EMA of the MACD line, so it can only start where that line does.
        double[] signalInput = [.. line.Select(value => double.IsFinite(value) ? value : 0.0)];
        double[] signal = Ema(signalInput, 9);
        double[] histogram = Filled(c.Length);
        for (int i = 0; i < c.Length; i++)
        {
            if (double.IsFinite(line[i]) && double.IsFinite(signal[i])) histogram[i] = line[i] - signal[i];
        }

        return (histogram, line);
    }

    private static (double[] Upper, double[] Lower, double[] Width) Bollinger(double[] c, int n, double k)
    {
        double[] upper = Filled(c.Length);
        double[] lower = Filled(c.Length);
        double[] width = Filled(c.Length);
        for (int i = n - 1; i < c.Length; i++)
        {
            double mean = 0;
            for (int j = i - n + 1; j <= i; j++) mean += c[j];
            mean /= n;

            double variance = 0;
            for (int j = i - n + 1; j <= i; j++) variance += (c[j] - mean) * (c[j] - mean);
            double deviation = Math.Sqrt(variance / n);

            upper[i] = mean + (k * deviation);
            lower[i] = mean - (k * deviation);
            width[i] = mean > 0 ? (upper[i] - lower[i]) / mean : double.NaN;
        }

        return (upper, lower, width);
    }

    private static (double[] K, double[] D) Stochastic(double[] h, double[] l, double[] c, int n, int smooth)
    {
        double[] k = Filled(c.Length);
        for (int i = n - 1; i < c.Length; i++)
        {
            double highest = double.MinValue;
            double lowest = double.MaxValue;
            for (int j = i - n + 1; j <= i; j++)
            {
                highest = Math.Max(highest, h[j]);
                lowest = Math.Min(lowest, l[j]);
            }

            // A flat window has no position within its range; 50 is the neutral reading, not zero.
            k[i] = highest > lowest ? 100 * (c[i] - lowest) / (highest - lowest) : 50.0;
        }

        double[] d = Filled(c.Length);
        for (int i = n + smooth - 2; i < c.Length; i++)
        {
            double sum = 0;
            for (int j = i - smooth + 1; j <= i; j++) sum += k[j];
            d[i] = sum / smooth;
        }

        return (k, d);
    }

    private static (double[] Adx, double[] PlusDi, double[] MinusDi) DirectionalMovement(
        double[] h, double[] l, double[] trueRange, int n)
    {
        double[] plus = new double[h.Length];
        double[] minus = new double[h.Length];
        for (int i = 1; i < h.Length; i++)
        {
            double up = h[i] - h[i - 1];
            double down = l[i - 1] - l[i];
            plus[i] = up > down && up > 0 ? up : 0;
            minus[i] = down > up && down > 0 ? down : 0;
        }

        double[] smoothedRange = WilderSmooth(trueRange, n);
        double[] smoothedPlus = WilderSmooth(plus, n);
        double[] smoothedMinus = WilderSmooth(minus, n);

        double[] plusDi = Filled(h.Length);
        double[] minusDi = Filled(h.Length);
        double[] dx = new double[h.Length];
        for (int i = 0; i < h.Length; i++)
        {
            if (!double.IsFinite(smoothedRange[i]) || smoothedRange[i] <= 0) continue;
            plusDi[i] = 100 * smoothedPlus[i] / smoothedRange[i];
            minusDi[i] = 100 * smoothedMinus[i] / smoothedRange[i];
            double total = plusDi[i] + minusDi[i];
            dx[i] = total > 0 ? 100 * Math.Abs(plusDi[i] - minusDi[i]) / total : 0;
        }

        return (WilderSmooth(dx, n), plusDi, minusDi);
    }

    /// <summary>
    /// The highest high of the <paramref name="n"/> bars *before* this one.
    ///
    /// Excluding the current bar is what makes a breakout test causal: including it would compare
    /// the bar's own high against a window containing that high, so the channel could never be
    /// broken and the strategy would never fire.
    /// </summary>
    private static double[] DonchianHighs(double[] h, int n)
    {
        double[] output = Filled(h.Length);
        for (int i = n; i < h.Length; i++)
        {
            double highest = double.MinValue;
            for (int j = i - n; j < i; j++) highest = Math.Max(highest, h[j]);
            output[i] = highest;
        }

        return output;
    }

    private static double[] RollingVwap(double[] h, double[] l, double[] c, double[] v, int n)
    {
        double[] output = Filled(c.Length);
        for (int i = n - 1; i < c.Length; i++)
        {
            double weighted = 0;
            double volume = 0;
            for (int j = i - n + 1; j <= i; j++)
            {
                double typical = (h[j] + l[j] + c[j]) / 3.0;
                weighted += typical * v[j];
                volume += v[j];
            }

            // No traded volume means no volume-weighted price. Falling back to an unweighted mean
            // would silently answer a different question.
            output[i] = volume > 0 ? weighted / volume : double.NaN;
        }

        return output;
    }

    private static double[] ObvSlope(double[] c, double[] v, int n)
    {
        double[] obv = new double[c.Length];
        for (int i = 1; i < c.Length; i++)
        {
            obv[i] = obv[i - 1] + (Math.Sign(c[i] - c[i - 1]) * v[i]);
        }

        double[] output = Filled(c.Length);
        for (int i = n; i < c.Length; i++) output[i] = obv[i] - obv[i - n];
        return output;
    }

    private static double[] ZScore(double[] x, int n)
    {
        double[] output = Filled(x.Length);
        for (int i = n - 1; i < x.Length; i++)
        {
            double mean = 0;
            for (int j = i - n + 1; j <= i; j++) mean += x[j];
            mean /= n;

            double variance = 0;
            for (int j = i - n + 1; j <= i; j++) variance += (x[j] - mean) * (x[j] - mean);
            double deviation = Math.Sqrt(variance / n);
            output[i] = deviation > 0 ? (x[i] - mean) / deviation : 0.0;
        }

        return output;
    }

    private static double[] Filled(int length)
    {
        double[] output = new double[length];
        Array.Fill(output, double.NaN);
        return output;
    }
}
