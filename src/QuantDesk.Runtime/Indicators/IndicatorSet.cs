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

    /// <summary>
    /// The lowest low of the prior window, which is the short side's breakout level.
    ///
    /// Absent until now, and its absence was structural rather than an oversight in one rule: every
    /// breakout rule in the book tested the high alone, so a breakdown through support could not be
    /// expressed at all. A rule set that can only see one side of a range can only ever be long.
    /// </summary>
    public double[] DonchianLow { get; private init; } = [];
    public double[] Vwap48 { get; private init; } = [];
    public double[] ObvSlope12 { get; private init; } = [];
    public double[] VolumeZ48 { get; private init; } = [];

    /// <summary>The instant each bar opened, or empty when the feed supplied no time axis.</summary>
    public DateTimeOffset[] Timestamps { get; private init; } = [];

    /// <summary>True when session-scoped measures reset on the session rather than on a bar count.</summary>
    public bool SessionScoped { get; private init; }

    /// <summary>
    /// Series that could not be computed from the history supplied, and why.
    ///
    /// The gap this closes. A series that is entirely NaN behaves identically to one that is merely
    /// warming up: every rule reading it declines, quietly and forever, and nothing anywhere says
    /// the feature is missing rather than the market being quiet.
    ///
    /// That was live within hours of the time-of-day volume baseline landing. It needs five prior
    /// observations at the same time of day, which means five prior days; the crypto client fetches
    /// twenty-four hours and retains twenty, so every time-of-day bucket held exactly one
    /// observation and VolumeZ48 was NaN for every bar in production. The research scan computes it
    /// happily over sixty days -- so the scan and the live lane disagreed about what the feature
    /// even is, which is the divergence this class exists to prevent and the hardest kind of defect
    /// to attribute after the fact.
    /// </summary>
    public IReadOnlyList<string> Unavailable { get; private init; } = [];

    /// <summary>Whether a named series could be computed at all from this history.</summary>
    public bool IsAvailable(string series) =>
        !Unavailable.Any(item => item.StartsWith(series, StringComparison.Ordinal));

    /// <summary>True when every bar carries the instant it opened.</summary>
    public bool HasTimeAxis => Timestamps.Length == Length && Length > 0;

    /// <summary>
    /// The index of the most recent bar at or before <paramref name="span"/> before bar
    /// <paramref name="i"/>, or -1 when the history does not reach back that far.
    ///
    /// The replacement for counting bars. "Twelve bars ago" equals "an hour ago" only while the
    /// feed returns an unbroken five-minute sequence; across a halt, a dropped bar, or an equity
    /// session boundary the two diverge and the rule silently measures a different span than the
    /// one it was calibrated on. With no time axis this falls back to the bar count, which is what
    /// the caller had before and is honest about being an approximation.
    /// </summary>
    public int IndexAtOrBefore(int i, TimeSpan span, int fallbackBars)
    {
        if (i < 0 || i >= Length) return -1;
        if (!HasTimeAxis) return i - fallbackBars >= 0 ? i - fallbackBars : -1;

        DateTimeOffset cutoff = Timestamps[i] - span;
        for (int j = i; j >= 0; j--)
        {
            if (Timestamps[j] <= cutoff) return j;
        }

        return -1;
    }

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
        IReadOnlyList<decimal> volumes,
        IReadOnlyList<DateTimeOffset>? timestamps = null,
        bool sessionScoped = false)
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

        // The time axis, when the feed supplied one. Every measure that is defined against a span
        // of time rather than a count of bars reads it; the rest are unaffected.
        DateTimeOffset[]? t = timestamps is not null && timestamps.Count == n
            ? [.. timestamps]
            : null;

        double[] trueRange = TrueRange(h, l, c);
        (double[] macdHistogram, _) = Macd(c);
        (double[] upper, double[] lower, double[] width) = Bollinger(c, 20, 2.0);
        (double[] k, double[] d) = Stochastic(h, l, c, 14, 3);
        (double[] adx, double[] plus, double[] minus) = DirectionalMovement(h, l, trueRange, 14);

        IReadOnlyList<string> unavailable = t is null ? [] : Unmeasurable(v, t);

        // Blanking is the point, not the reporting. A series named unavailable that is still
        // readable will be read: the rules take it by index and have no way to consult the report.
        bool volumeUnusable = unavailable.Any(item => item.StartsWith("Vwap48", StringComparison.Ordinal));

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
            DonchianLow = DonchianLows(l, 20),
            Vwap48 = volumeUnusable ? Filled(n)
                : sessionScoped && t is not null ? SessionVwap(h, l, c, v, t)
                : RollingVwap(h, l, c, v, 48),
            ObvSlope12 = volumeUnusable ? Filled(n) : ObvSlope(c, v, 12),
            VolumeZ48 = volumeUnusable ? Filled(n)
                : t is not null ? TimeOfDayVolumeZ(v, t)
                : ZScore(v, 48),
            Timestamps = t ?? [],
            SessionScoped = sessionScoped && t is not null,
            Unavailable = unavailable,
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
            DonchianLow = Filled(c.Length),
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

    /// <inheritdoc cref="DonchianHighs"/>
    private static double[] DonchianLows(double[] l, int n)
    {
        double[] output = Filled(l.Length);
        for (int i = n; i < l.Length; i++)
        {
            double lowest = double.MaxValue;
            for (int j = i - n; j < i; j++) lowest = Math.Min(lowest, l[j]);
            output[i] = lowest;
        }

        return output;
    }

    /// <summary>
    /// VWAP accumulated from the start of each trading session, reset at every session boundary.
    ///
    /// The definition VWAP actually has. A rolling window is a different measure wearing the same
    /// name: it answers "the volume-weighted price of the last N bars", where the real one answers
    /// "the volume-weighted price so far today" -- which is the number a displacement is judged
    /// against, and the number every published description of VWAP reversion refers to.
    ///
    /// This matters here more than most corrections. reversion.vwap.v1 is the best-scoring equity
    /// rule in the book at +3.3 bps mean, so it is the rule most likely to be traded on, and its
    /// figure was measured against a 48-bar rolling window rather than against VWAP.
    ///
    /// A session boundary is a gap in the time axis longer than a session's own bars can explain.
    /// Deriving it from the gaps rather than from a hardcoded 9:30-16:00 keeps the venue's calendar
    /// authoritative: a half day, a holiday, or an extended-hours bar changes where the boundaries
    /// fall without changing this code, and the engineering constitution forbids hardcoding the
    /// session anyway.
    /// </summary>
    private static double[] SessionVwap(
        double[] h, double[] l, double[] c, double[] v, DateTimeOffset[] t)
    {
        double[] output = Filled(c.Length);
        double weighted = 0, volume = 0;

        for (int i = 0; i < c.Length; i++)
        {
            if (i > 0 && StartsNewSession(t, i))
            {
                weighted = 0;
                volume = 0;
            }

            double typical = (h[i] + l[i] + c[i]) / 3.0;
            weighted += typical * v[i];
            volume += v[i];

            // No traded volume means no volume-weighted price. Falling back to an unweighted mean
            // would silently answer a different question.
            output[i] = volume > 0 ? weighted / volume : double.NaN;
        }

        return output;
    }

    /// <summary>
    /// Whether bar <paramref name="i"/> opens a new session.
    ///
    /// Taken as a gap materially larger than the series' own typical spacing. The median spacing is
    /// the series' natural bar width whatever timeframe it was requested at, so this works on
    /// five-minute and one-minute bars without being told which it is.
    /// </summary>
    private static bool StartsNewSession(DateTimeOffset[] t, int i)
    {
        TimeSpan spacing = TypicalSpacing(t);
        if (spacing <= TimeSpan.Zero) return false;

        // Three times the bar width: wide enough that one dropped bar is not a session boundary,
        // narrow enough that an overnight break always is.
        return t[i] - t[i - 1] > spacing * 3;
    }

    /// <summary>The median gap between consecutive bars, which is the series' own bar width.</summary>
    private static TimeSpan TypicalSpacing(DateTimeOffset[] t)
    {
        if (t.Length < 2) return TimeSpan.Zero;

        long[] gaps = new long[t.Length - 1];
        for (int i = 1; i < t.Length; i++) gaps[i - 1] = (t[i] - t[i - 1]).Ticks;
        Array.Sort(gaps);
        return TimeSpan.FromTicks(gaps[gaps.Length / 2]);
    }

    /// <summary>
    /// Volume measured against what this time of day normally does, rather than against itself.
    ///
    /// The construction the engineering constitution names as wrong is a volume z-score taken over
    /// a trailing window of the same series, which is what this used to be. Volume has a strong and
    /// entirely predictable shape across the day -- the open and the close are busy, the middle is
    /// not -- so a trailing window scores every open as a surge and every lunchtime as a drought.
    /// The rule reading it, volume.surge-breakout.v1, has the widest confidence interval in the
    /// crypto book at -60.1 mean against a -94.2 lower bound, which is what an ill-posed feature
    /// looks like from the outside.
    ///
    /// The baseline is the same time of day on previous days, so the comparison is like for like.
    /// Where too few prior days exist to form one, the bar is left NaN rather than scored against a
    /// baseline that is mostly itself: a rule that reads NaN declines, which is the correct answer
    /// when the question cannot yet be asked.
    /// </summary>
    /// <summary>
    /// Names the series this history cannot support, with the shortfall spelled out.
    ///
    /// Checked against the requirement rather than against the output, so the message can say what
    /// is missing instead of only that something is.
    /// </summary>
    private static IReadOnlyList<string> Unmeasurable(double[] v, DateTimeOffset[] t)
    {
        List<string> missing = [];

        // Does the feed actually report volume?
        //
        // Alpaca's crypto bars very often do not. Measured on 2026-09-02 over seven days of
        // five-minute bars: 65.6% of bars across the traded universe carry zero volume, ranging
        // from 12.6% on BTC/USD to 91.3% on BCH/USD. Equities are unaffected -- SPY reported zero
        // such bars over the same window.
        //
        // That silently corrupts more than the obviously volume-shaped measures. VWAP is a
        // volume-weighted average, so on BCH it was being computed from 170 of 1,960 bars: not a
        // sparser VWAP, a VWAP of a small and non-random subset of the day. A rule comparing price
        // to it reads a number that looks like VWAP and is not, which is exactly the class of
        // defect this set exists to refuse rather than approximate.
        int populated = 0;
        foreach (double volume in v)
        {
            if (volume > 0) populated++;
        }

        double coverage = v.Length > 0 ? (double)populated / v.Length : 0d;
        if (coverage < MinimumVolumeCoverage)
        {
            missing.Add(
                $"Vwap48, ObvSlope12, VolumeZ48: the feed reports volume on {coverage:P1} of bars, " +
                $"below the {MinimumVolumeCoverage:P0} a volume-weighted measure needs");
            return missing;
        }

        TimeSpan span = t.Length > 1 ? t[^1] - t[0] : TimeSpan.Zero;
        TimeSpan required = TimeSpan.FromDays(MinimumPriorDaysForTimeOfDay + 1);
        if (span < required)
        {
            missing.Add(
                $"VolumeZ48: a time-of-day baseline needs {required.TotalDays:0} days of history; " +
                $"this series spans {span.TotalHours:0.#} hours");
        }

        return missing;
    }

    /// <summary>
    /// The share of bars that must carry volume before a volume-weighted measure means anything.
    ///
    /// Half, because below that the "average" is dominated by whichever bars the venue happened to
    /// report, and that subset is not random -- it is the bars that traded, which is precisely the
    /// selection a volume weighting is supposed to express rather than be silently restricted to.
    /// </summary>
    public const double MinimumVolumeCoverage = 0.5;

    /// <summary>
    /// Prior days required before volume can be scored against its own time of day.
    ///
    /// Five, because a mean and a standard deviation over fewer observations than that describe the
    /// sample rather than the population, and the whole point of the baseline is to be a population
    /// the current bar can be unusual against.
    /// </summary>
    public const int MinimumPriorDaysForTimeOfDay = 5;

    private static double[] TimeOfDayVolumeZ(double[] v, DateTimeOffset[] t)
    {
        const int MinimumPriorObservations = MinimumPriorDaysForTimeOfDay;

        double[] output = Filled(v.Length);

        // One bucket per bar slot in the day. Taken from the series' own bar width so the comparison
        // is like for like at whatever timeframe was requested: a wider bucket would lump the busy
        // open in with the quiet bars after it and reintroduce exactly the averaging this replaces.
        double bucketMinutes = Math.Max(TypicalSpacing(t).TotalMinutes, 1d);

        Dictionary<int, List<double>> byTimeOfDay = [];
        for (int i = 0; i < v.Length; i++)
        {
            int bucket = (int)(t[i].UtcDateTime.TimeOfDay.TotalMinutes / bucketMinutes);
            if (!byTimeOfDay.TryGetValue(bucket, out List<double>? prior))
            {
                prior = [];
                byTimeOfDay[bucket] = prior;
            }

            // Scored against prior observations only. Including the current bar in its own baseline
            // is the same self-reference in miniature, and it shrinks every extreme toward zero.
            if (prior.Count >= MinimumPriorObservations)
            {
                double mean = 0;
                foreach (double sample in prior) mean += sample;
                mean /= prior.Count;

                double variance = 0;
                foreach (double sample in prior) variance += (sample - mean) * (sample - mean);
                double deviation = Math.Sqrt(variance / prior.Count);
                output[i] = deviation > 0 ? (v[i] - mean) / deviation : 0.0;
            }

            prior.Add(v[i]);
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
