using System.Text.RegularExpressions;

namespace QuantDesk.Architecture.Tests;

/// <summary>
/// Every public type in the runtime is reachable from production code, or says why not.
///
/// The failure this exists for
/// ---------------------------
/// A connection audit found eight substantial components with no production reference at all --
/// three model inference paths, the artifact reader they load through, the replay runner and its
/// recorder, the episode attribution scorer, and an expert. Each was built, tested, documented and
/// wired to nothing. Two more were worse than orphaned: a singleton registered and injected
/// nowhere, and a policy type produced by an agent and read by no one.
///
/// None of that failed a test, because every one of them had good tests. Tests prove a component
/// works; nothing was asking whether anything used it. That is the gap this closes, and it is worth
/// more than any single connection it forces, because the pattern kept recurring while attention
/// was on making each new component correct.
///
/// What counts as reachable, and why it is measured per file
/// ---------------------------------------------------------
/// Reachability spreads from the composition roots -- the API host and the CLI -- through mentions
/// in code, and the unit is the file rather than the type. C# lets a type be used without ever
/// being named: through <c>var</c>, through a property, through inference on a return value. A
/// per-type search reports those as dead, and the first run of this test produced forty
/// candidates, most of them enums and result records that are used constantly and named nowhere.
/// A gate with that many false positives gets suppressed wholesale, which is worse than no gate.
///
/// So a file is reachable when something reachable mentions a type it declares, and every type in
/// a reachable file is reachable with it. That matches how these components are actually
/// organised -- a class and the enum describing its outcome live together and are one unit -- and
/// it leaves the signal this test exists for intact: a component nothing mentions at all.
///
/// Comments do not count as mentions. Three of the eight orphans found by hand were named only
/// inside a doc comment, and a plain text search reported them as connected.
///
/// Tests do not count either. A type reachable only from its tests is a type the running system
/// does not have, however thoroughly it is verified.
///
/// What it cannot see
/// ------------------
/// A type registered with the container and injected nowhere. <c>AddSingleton&lt;ExitEngine&gt;()</c>
/// is a mention, so the exit engine reads as reachable while nothing consults it. That is a real
/// limitation and is stated rather than papered over -- catching it needs the container's own view
/// of what was resolved, not a source scan.
///
/// Why an allow-list rather than a threshold
/// -----------------------------------------
/// Some types are legitimately unreferenced by name: a marker interface, a type resolved only
/// through dependency injection by its interface, a contract deserialised into rather than
/// constructed. Each of those is a judgement, so each gets an entry here with the reason written
/// down. A count-based exemption would cover the next orphan automatically, which is how this
/// happened in the first place.
/// </summary>
public sealed class OrphanedComponentTests
{
    private const string Project = "QuantDesk.Runtime";

    /// <summary>
    /// What is disconnected today, and what each one is waiting on.
    ///
    /// A baseline, not a suppression list. The gate this test provides is a ratchet: nothing new
    /// may join this list, and anything that gets connected must be removed from it -- there is a
    /// test below that fails if an entry here is no longer an orphan, so fixing one is not
    /// optional bookkeeping.
    ///
    /// Introducing the check any other way was not honest. Failing outright would have meant
    /// thirty-six entries suppressed in one commit to get the build green, which is the same
    /// outcome with more ceremony. Recording what is true and forbidding it from growing is what a
    /// gate on an existing codebase can actually enforce.
    ///
    /// Section 22's replay cluster is gone from this list entirely: the recorder writes every
    /// session and the replay service reproduces the previous one on start-up, so the runner, its
    /// refusals and the virtual clock all have production callers now.
    ///
    /// The portfolio ledger cluster is gone too, by deletion rather than connection. It looked like
    /// recovery having been left disconnected and was not: the autonomous lane keeps its durable
    /// position state in SpotExecutionStore, whose recovery runs every second through a wired
    /// hosted service, and the snapshot-and-journal path was the founding architecture that store
    /// replaced. Wiring it would have created a second position ledger able to disagree with the
    /// first. Deleting code that never ran does not weaken recovery; leaving it in place made a
    /// dead subsystem read as coverage.
    /// </summary>
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        // -- Typed forecasts. The committee itself is connected now; these hang off it.
        ["CommitteeAllocator"] =
            "Allocates weight across a family's members. The committee is wired but every family "
            + "currently has one expert, so nothing allocates yet.",
        ["BoundedWeightProjector"] = "Bounds those allocations.",
        ["ExpertCatalog"] = "Describes the experts the typed committee would assemble.",
        ["ExpertDefinition"] = "One catalog entry.",
        ["ExpertRuntimePlane"] = "Which plane an expert runs on.",

        // -- Model inference paths with no caller. HAR and GARCH are connected and load live; these
        // two need a decision rather than a wire, and each entry says which.
        ["GaussianHmmFilter"] =
            "Verified against hmmlearn and loadable, but connecting it means replacing "
            + "MarketRegimeExpert, which is a closed-form map from an ATR percentile and ADX to four "
            + "named regimes with no fitted model in it. That is a modelling decision -- which latent "
            + "state *is* stress -- not a wiring one, and it needs a fitting pipeline for features "
            + "nothing currently computes in Python.",
        ["GradientBoostedTreeModel"] =
            "Verified against a real booster across every missing-value convention, and nothing "
            + "fits a tree for it to score. The 2026-09-04 comparison settles why: LightGBM, Ridge, "
            + "a random forest and their average were scored on every instrument against each "
            + "venue's real round trip, and none of the twenty-four pairs cleared its costs at a "
            + "fifteen-minute horizon. Wiring this path would connect a verified scorer to a model "
            + "measured to lose money, which is worse than leaving it unwired and saying so.",

        // -- Scoring and features built ahead of the path that would feed them.
        ["OrderBookImbalanceExpert"] =
            "The imbalance already reaches decisions through InstrumentSnapshot, so the value is "
            + "connected and this wrapper is not. Wiring it would publish a MicrostructureForecast "
            + "that nothing consumes -- a forecast with no reader is the pattern this whole list "
            + "exists to stop, so it waits for a consumer rather than being connected for the sake "
            + "of leaving the list.",
        ["FeatureCalculations"] = "Feature maths the live path does not call.",
        ["FeatureSnapshot"] = "A point-in-time feature record nothing builds.",
        ["FeatureSnapshotBuilder"] = "Builds them.",
        ["FeatureValue"] = "One value in a snapshot.",
        ["PriceSample"] = "A sample feeding those primitives.",
        ["EwmaVariance"] = "An exponentially weighted variance primitive with no caller.",
        ["TimestampedRingBuffer"] = "Backs those primitives.",
        ["SequenceGenerator"] = "Issues sequence numbers nothing asks for.",

        // -- Options and cost surfaces for lanes that are stood down.
        ["BlackScholes"] = "Option pricing. The options lane does not trade.",
        ["OptionChainValidator"] = "Validates a chain nothing requests.",
        ["DefinedRiskVerticalRiskProjector"] = "Projects vertical risk for that lane.",
        ["DirectionalStrategyCompiler"] =
            "The equity compiler. Only the crypto compiler is constructed.",
        ["EquityFeeSchedule"] = "Equity fees, for the same reason.",
        ["CryptoCostScenarios"] = "Cost scenarios used in analysis rather than by the runtime.",
        ["CryptoCostScenario"] = "One scenario.",
    };

    /// <summary>Directories whose contents are compiler output rather than authored code.</summary>
    private static readonly string[] Generated = ["obj", "bin"];

    /// <summary>
    /// Top-level public type declarations.
    ///
    /// Deliberately not a full parser. It matches the shapes this codebase actually declares, and a
    /// declaration it fails to recognise is simply not policed -- which is a gap in coverage rather
    /// than a false failure, and the test below reports how many types it found so a sudden drop is
    /// visible.
    /// </summary>
    private static readonly Regex Declaration = new(
        @"^public\s+(?:sealed\s+|static\s+|abstract\s+|readonly\s+|partial\s+)*"
        + @"(?:class|record|interface|enum|struct)\s+(?:struct\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    [Fact]
    public void EveryPublicRuntimeTypeIsReachableFromProductionCode()
    {
        string root = FindRepositoryRoot();
        IReadOnlyDictionary<string, string> declarations = PublicTypes(Path.Combine(root, "src", Project));
        Assert.True(
            declarations.Count > 100,
            $"Only {declarations.Count} public types found; the declaration pattern may have stopped matching.");

        IReadOnlySet<string> reachableFiles = ReachableFiles(root, declarations);

        var orphans = new List<string>();
        foreach ((string name, string declaredIn) in declarations)
        {
            if (Known.ContainsKey(name)) continue;
            if (reachableFiles.Contains(declaredIn)) continue;

            orphans.Add($"{Path.GetFileName(declaredIn)} :: {name}");
        }

        Assert.True(
            orphans.Count == 0,
            $"These public types in {Project} became reachable from no production code. Connect "
            + "each one or delete it. Adding it to the known list is a last resort and needs the "
            + "reason written down -- a component nothing calls is not coverage, however well it "
            + "is tested, and this list exists to shrink."
            + Environment.NewLine
            + string.Join(Environment.NewLine, orphans.Order(StringComparer.Ordinal).Distinct()));
    }

    /// <summary>
    /// Files reachable from the composition roots, following mentions until nothing new appears.
    /// </summary>
    private static IReadOnlySet<string> ReachableFiles(
        string root, IReadOnlyDictionary<string, string> declarations)
    {
        // Every production file, with the type names it mentions in code.
        var mentions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ProductionFiles(root))
            mentions[file] = TypeNamesIn(File.ReadAllText(file), declarations.Keys);

        // The roots: the host and the command line. Everything the running system does starts here.
        var reachable = new HashSet<string>(
            mentions.Keys.Where(IsCompositionRoot), StringComparer.OrdinalIgnoreCase);

        var frontier = new Queue<string>(reachable);
        while (frontier.Count > 0)
        {
            string file = frontier.Dequeue();
            if (!mentions.TryGetValue(file, out HashSet<string>? named)) continue;

            foreach (string name in named)
            {
                if (!declarations.TryGetValue(name, out string? declaredIn)) continue;
                if (!reachable.Add(declaredIn)) continue;
                frontier.Enqueue(declaredIn);
            }
        }

        return reachable;
    }

    private static bool IsCompositionRoot(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}QuantDesk.Api{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase)
        || file.Contains($"{Path.DirectorySeparatorChar}QuantDesk.Cli{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Which of the known type names appear in this source, outside comments.</summary>
    private static HashSet<string> TypeNamesIn(string source, IEnumerable<string> known)
    {
        string code = StripComments(source);
        var found = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in Identifier.Matches(code))
        {
            found.Add(match.Value);
        }

        found.IntersectWith(known);
        return found;
    }

    /// <summary>
    /// Every identifier-shaped token. Greedy, so tokens come out maximal and a word-boundary
    /// assertion would add nothing -- and the result is intersected with the known type names
    /// anyway, so a partial match could not survive it.
    /// </summary>
    private static readonly Regex Identifier =
        new("[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);


    [Fact]
    public void NothingOnTheKnownListIsSecretlyConnected()
    {
        // The ratchet. Connecting a component means deleting its entry here, so the list can only
        // shrink -- otherwise it would slowly become a record of what someone once believed rather
        // than what is true, which is how a suppression list is born.
        string root = FindRepositoryRoot();
        IReadOnlyDictionary<string, string> declarations = PublicTypes(Path.Combine(root, "src", Project));
        IReadOnlySet<string> reachableFiles = ReachableFiles(root, declarations);

        var connected = new List<string>();
        foreach (string name in Known.Keys)
        {
            if (!declarations.TryGetValue(name, out string? declaredIn)) continue;
            if (reachableFiles.Contains(declaredIn)) connected.Add(name);
        }

        Assert.True(
            connected.Count == 0,
            "These are on the known-disconnected list and are now reachable. Remove their entries: "
            + string.Join(", ", connected.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void EveryKnownEntryNamesATypeThatStillExists()
    {
        // A stale entry is a hole nobody remembers opening. If the type was deleted, so should the
        // entry be -- and if it was renamed, the new name is unpoliced until someone notices.
        IReadOnlyDictionary<string, string> declarations =
            PublicTypes(Path.Combine(FindRepositoryRoot(), "src", Project));

        foreach (string known in Known.Keys)
        {
            Assert.True(
                declarations.ContainsKey(known),
                $"{known} is on the known-disconnected list but is no longer declared.");
        }
    }

    [Fact]
    public void EveryKnownEntryCarriesAReason()
    {
        // The entry is the reason. An empty one is a suppression wearing an exemption's clothes.
        Assert.All(Known, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value)));
    }

    /// <summary>Type name to the file that declares it.</summary>
    private static IReadOnlyDictionary<string, string> PublicTypes(string projectRoot)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string file in AuthoredFiles(projectRoot))
        {
            foreach (Match match in Declaration.Matches(StripComments(File.ReadAllText(file))))
                declarations[match.Groups["name"].Value] = file;
        }

        return declarations;
    }

    /// <summary>Every authored file across the production projects, tests excluded by construction.</summary>
    private static IReadOnlyList<string> ProductionFiles(string root)
    {
        string source = Path.Combine(root, "src");
        return [.. AuthoredFiles(source)];
    }

    private static IEnumerable<string> AuthoredFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;

        foreach (string file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            bool generated = Generated.Any(folder => file.Contains(
                $"{Path.DirectorySeparatorChar}{folder}{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal));

            if (!generated) yield return file;
        }
    }

    /// <summary>
    /// Source with comments and documentation removed.
    ///
    /// The reason this matters more than it sounds: three of the eight orphans this test was
    /// written for were mentioned only inside a doc comment, and a plain text search reported them
    /// as referenced. A comment explaining what a type is for is not a use of it.
    /// </summary>
    private static string StripComments(string source)
    {
        var stripped = new System.Text.StringBuilder(source.Length);
        bool inBlock = false;
        bool inString = false;
        bool inVerbatim = false;

        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (inBlock)
            {
                if (current == '*' && next == '/') { inBlock = false; index++; }
                continue;
            }

            if (inString)
            {
                stripped.Append(current);
                if (!inVerbatim && current == '\\') { if (next != '\0') { stripped.Append(next); index++; } }
                else if (current == '"') { inString = false; inVerbatim = false; }
                continue;
            }

            if (current == '/' && next == '/')
            {
                while (index < source.Length && source[index] is not ('\n' or '\r')) index++;
                stripped.Append('\n');
                continue;
            }

            if (current == '/' && next == '*') { inBlock = true; index++; continue; }

            if (current == '"')
            {
                inString = true;
                inVerbatim = index > 0 && source[index - 1] == '@';
                stripped.Append(current);
                continue;
            }

            stripped.Append(current);
        }

        return stripped.ToString();
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
