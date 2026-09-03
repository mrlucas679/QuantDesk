using System.Text.RegularExpressions;

namespace QuantDesk.Architecture.Tests;

/// <summary>
/// The decision path reads time through the injected clock, never off the wall.
///
/// Why this is an architecture test and not a code review note
/// -----------------------------------------------------------
/// Section 22 makes deterministic replay a release gate, and the replay runner can prove that a
/// given log reproduces -- but only for code that reads the clock it was handed. A single
/// <c>DateTimeOffset.UtcNow</c> anywhere in the decision path silently opts that decision out, and
/// the failure is quiet in the worst way: a replay run in the afternoon takes different branches
/// than the morning run it claims to reproduce, and every report still says it reproduced.
///
/// The decision path is clean today. This is what keeps it that way -- a reviewer who does not know
/// the rule adds one call, and this fails rather than the guarantee eroding a line at a time.
///
/// What is deliberately outside the scope
/// --------------------------------------
/// Telemetry and the fault campaign. A latency percentile measures how long the machine took, which
/// is a fact about this run rather than about the decision, and a health probe reporting virtual
/// uptime would be reporting a fiction. They are named individually rather than covered by a
/// directory rule, so adding a third exemption is a decision someone has to write down here.
/// </summary>
public sealed class DeterministicTimeTests
{
    /// <summary>Directories whose code decides what the system does.</summary>
    private static readonly string[] DecisionPath =
    [
        "Execution", "Experts", "Indicators", "Scoring", "Costs", "Replay", "Research",
        "Persistence", "Audit",
    ];

    /// <summary>
    /// Files that may read real time, each for a stated reason.
    ///
    /// Named individually. A directory-level exemption would quietly cover the next file added
    /// beside them, and the whole point is that the exemption is a decision rather than a location.
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
    };

    private static readonly Regex WallClockRead = new(
        @"\b(DateTime\.UtcNow|DateTime\.Now|DateTimeOffset\.UtcNow|DateTimeOffset\.Now"
        + @"|Stopwatch\.GetTimestamp|Environment\.TickCount64?)\b",
        RegexOptions.Compiled);

    [Fact]
    public void TheDecisionPathReadsTimeThroughTheInjectedClock()
    {
        string runtime = Path.Combine(FindRepositoryRoot(), "src", "QuantDesk.Runtime");
        var offenders = new List<string>();

        foreach (string directory in DecisionPath)
        {
            string path = Path.Combine(runtime, directory);
            if (!Directory.Exists(path)) continue;

            foreach (string file in Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (Exempt.ContainsKey(Path.GetFileName(file))) continue;

                foreach ((string line, int number) in ReadCode(file))
                {
                    if (WallClockRead.IsMatch(line))
                    {
                        offenders.Add(
                            $"{directory}/{Path.GetFileName(file)}:{number}: {line.Trim()}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The decision path must read time through IRuntimeClock so a recorded session replays "
            + "to the same decisions. These read the wall directly:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryExemptionNamesAFileThatStillExists()
    {
        // A stale exemption is a hole nobody remembers opening. If the file moved or went away, the
        // reason for the exemption went with it.
        string runtime = Path.Combine(FindRepositoryRoot(), "src", "QuantDesk.Runtime");
        string[] present = [.. Directory
            .GetFiles(runtime, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OfType<string>()];

        foreach (string exempt in Exempt.Keys)
            Assert.Contains(exempt, present);
    }

    /// <summary>
    /// Lines of real code, with comments and documentation dropped.
    ///
    /// The rule is about what executes. A comment explaining why wall time is wrong -- and there are
    /// several, because the point has been learned the hard way -- must not read as a violation of
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
