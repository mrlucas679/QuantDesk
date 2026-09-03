using System.Text.RegularExpressions;

namespace QuantDesk.Architecture.Tests;

/// <summary>
/// The decision path reads time through the injected clock, never off the wall.
///
/// Why this is an architecture test and not a code review note
/// -----------------------------------------------------------
/// Section 22 makes deterministic replay a release gate. The replay runner can prove a given log
/// reproduces, but only for code that reads the clock it was handed. A single
/// <c>DateTimeOffset.UtcNow</c> anywhere in the decision path silently opts that decision out, and
/// the failure is quiet in the worst way: a replay run in the afternoon takes different branches
/// than the morning run it claims to reproduce, and every report still says it reproduced.
///
/// The first version of this test matched <c>Stopwatch.GetTimestamp</c> and missed
/// <c>Stopwatch.Frequency</c>, which let nine live conversions through -- including the exit
/// engine's maximum holding period and both strategy compilers' candidate lifetimes. Under a
/// virtual clock every one of them was out by a factor of a hundred, because
/// <c>Stopwatch.Frequency</c> is 1,000,000,000 on Linux against <c>TimeSpan</c>'s 10,000,000. On
/// Windows the two coincide, which is worse: the mistake passes on a developer's machine and
/// changes behaviour in the container.
///
/// So the pattern list is part of the guarantee, not an implementation detail of the test, and
/// <c>MonotonicTicksFor</c> exists on the clock so there is no correct way to convert a duration
/// without asking which clock will be compared against.
///
/// What is deliberately outside the scope
/// --------------------------------------
/// Telemetry and the fault campaign. A latency percentile measures how long the machine took --
/// a fact about this run rather than about the decision -- and a health probe reporting virtual
/// uptime would be a fiction. They are named individually rather than covered by a directory rule,
/// so adding a third exemption is a decision someone has to write down here.
///
/// The Alpaca project is outside the scan. Its two wall-clock reads build query windows for a
/// historical HTTP request -- "give me the last thirty-six hours" -- which is an I/O boundary
/// rather than a decision, and a replay reads its events from the recorded log rather than from
/// the venue. The CLI is outside it too: a one-shot smoke tool that references only Alpaca, where
/// both readings come from the same Stopwatch and are therefore self-consistent. Pulling in the
/// runtime project for a clock there would widen a dependency graph for uniformity, which is a
/// worse trade than the inconsistency it removes.
/// </summary>
public sealed class DeterministicTimeTests
{
    /// <summary>
    /// Everything in the runtime project, plus the API files that decide what to trade.
    ///
    /// A whole project rather than a directory list, because the runtime is clean and a list would
    /// silently exclude the next directory added beside the ones on it.
    /// </summary>
    private static readonly string[] CoveredProjects = ["QuantDesk.Runtime"];

    /// <summary>
    /// The API directories that decide and execute, now that they read through the clock.
    ///
    /// Whole directories rather than a file list, for the same reason the runtime is a whole
    /// project: a list silently excludes the next file added beside the ones on it, which is
    /// exactly when a gate stops working.
    /// </summary>
    private static readonly string[] CoveredApiDirectories = ["PaperTrading", "Agents"];

    /// <summary>
    /// Files that may read real time, each for a stated reason.
    ///
    /// Named individually. A directory-level exemption would quietly cover the next file added
    /// beside them, and the point is that the exemption is a decision rather than a location.
    /// </summary>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        ["LiveRuntimeClock.cs"] = "It is the wall clock. Reading it is its job.",
        ["LatencyRecorder.cs"] =
            "Measures how long this machine took, which is a fact about the run and not the decision.",
        ["RuntimeHealthProbe.cs"] =
            "Reports process uptime. Virtual uptime would be a fiction dressed as a health signal.",
        ["FaultCampaign.cs"] =
            "Records when a fault injection ran, which is wall-clock evidence about a real session.",
        ["AutonomousPaperTradingOptions.cs"] =
            "Parses configuration at startup, before any clock is constructed, and decides nothing "
            + "at decision time.",
    };

    /// <summary>
    /// Every way to read real time, including the one the first version of this test missed.
    ///
    /// <c>Stopwatch.Frequency</c> is here because converting a duration with it produces ticks in
    /// the live clock's units regardless of which clock they will be compared against, which is the
    /// same bug as reading the wall directly and harder to see.
    /// </summary>
    private static readonly Regex RealTimeRead = new(
        @"\b(DateTime\.UtcNow|DateTime\.Now|DateTimeOffset\.UtcNow|DateTimeOffset\.Now"
        + @"|Stopwatch\.GetTimestamp|Stopwatch\.Frequency|Environment\.TickCount64?)\b",
        RegexOptions.Compiled);

    [Fact]
    public void TheDecisionPathReadsTimeThroughTheInjectedClock()
    {
        var offenders = new List<string>();

        foreach (string file in CoveredFiles())
        {
            if (Exempt.ContainsKey(Path.GetFileName(file))) continue;

            foreach ((string line, int number) in ReadCode(file))
            {
                if (RealTimeRead.IsMatch(line))
                    offenders.Add($"{Path.GetFileName(file)}:{number}: {line.Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The decision path must read time through IRuntimeClock -- including durations, via "
            + "MonotonicTicksFor -- so a recorded session replays to the same decisions. These read "
            + "real time directly:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryExemptionNamesAFileThatStillExists()
    {
        // A stale exemption is a hole nobody remembers opening. If the file moved or went away, the
        // reason for the exemption went with it.
        string[] present = [.. CoveredFiles().Select(Path.GetFileName).OfType<string>()];

        foreach (string exempt in Exempt.Keys)
            Assert.Contains(exempt, present);
    }

    [Fact]
    public void TheClockOffersTheDurationConversionThatMakesTheRuleFollowable()
    {
        // A rule with no compliant alternative is a rule people route around. Converting a duration
        // to monotonic ticks has exactly one correct answer -- ask the clock whose timestamps it
        // will be added to -- and this fails if that method is ever removed.
        //
        // Read from source rather than by reflection, because this project deliberately references
        // nothing it polices: an architecture test that compiles against the code it inspects can
        // be broken by that code, which is precisely when it most needs to run.
        string contract = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "QuantDesk.Runtime", "Time", "IRuntimeClock.cs"));

        Assert.Contains("long MonotonicTicksFor(TimeSpan duration);", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryClockImplementationAnswersTheDurationConversion()
    {
        // Three clocks exist in this codebase and all three count in different units -- Stopwatch
        // ticks, TimeSpan ticks, and microseconds in one test double. An implementation that
        // inherited a default would be answering in somebody else's units.
        string time = Path.Combine(FindRepositoryRoot(), "src", "QuantDesk.Runtime", "Time");

        foreach (string file in Directory.GetFiles(time, "*RuntimeClock.cs"))
        {
            if (Path.GetFileName(file) == "IRuntimeClock.cs") continue;
            Assert.Contains("MonotonicTicksFor", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> CoveredFiles()
    {
        string root = FindRepositoryRoot();

        foreach (string project in CoveredProjects)
        {
            string path = Path.Combine(root, "src", project);
            if (!Directory.Exists(path)) continue;

            foreach (string file in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                // Generated assembly info and build intermediates are not authored code.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }

        foreach (string directory in CoveredApiDirectories)
        {
            string path = Path.Combine(root, "src", "QuantDesk.Api", directory);
            if (!Directory.Exists(path)) continue;

            foreach (string file in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
                yield return file;
        }
    }

    /// <summary>
    /// Lines of real code, with comments and documentation dropped.
    ///
    /// The rule is about what executes. A comment explaining why wall time is wrong -- and there
    /// are several now, because the point has been learned twice -- must not read as a violation of
    /// the thing it is explaining.
    /// </summary>
    private static IEnumerable<(string Line, int Number)> ReadCode(string file)
    {
        int number = 0;
        bool inBlockComment = false;

        foreach (string raw in File.ReadLines(file))
        {
            number++;
            string line = raw;

            if (inBlockComment)
            {
                int close = line.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0) continue;
                line = line[(close + 2)..];
                inBlockComment = false;
            }

            int open = line.IndexOf("/*", StringComparison.Ordinal);
            if (open >= 0)
            {
                inBlockComment = !line[open..].Contains("*/", StringComparison.Ordinal);
                line = line[..open];
            }

            int lineComment = line.IndexOf("//", StringComparison.Ordinal);
            if (lineComment >= 0) line = line[..lineComment];

            if (!string.IsNullOrWhiteSpace(line)) yield return (line, number);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
