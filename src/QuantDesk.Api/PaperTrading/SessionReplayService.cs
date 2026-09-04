using QuantDesk.Domain.Replay;
using QuantDesk.Runtime.Replay;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>What the last replay attempt found.</summary>
/// <param name="State">idle, replayed, diverged, refused, or unavailable.</param>
/// <param name="SessionFile">Which recorded session was replayed.</param>
/// <param name="EventCount">How many events it contained.</param>
/// <param name="TraceHash">The hash both passes agreed on, when they agreed.</param>
/// <param name="Reason">Why it could not be replayed, or how the two passes disagreed.</param>
/// <param name="CheckedAt">When this was established.</param>
public sealed record SessionReplaySnapshot(
    string State,
    string? SessionFile,
    int EventCount,
    string? TraceHash,
    string? Reason,
    DateTimeOffset CheckedAt);

/// <summary>Holds the last replay result for the status surface.</summary>
public sealed class SessionReplayState
{
    private readonly Lock _gate = new();
    private SessionReplaySnapshot _snapshot =
        new("idle", null, 0, null, "NO_REPLAY_ATTEMPTED_YET", DateTimeOffset.UnixEpoch);

    public SessionReplaySnapshot Snapshot()
    {
        lock (_gate) return _snapshot;
    }

    public void Update(SessionReplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate) _snapshot = snapshot;
    }
}

/// <summary>
/// Replays the previous session on start-up and reports whether it reproduced.
///
/// This is what closes section 22's gate. The runner and the recorder both existed and were both
/// disconnected; recording was wired last commit, and until now nothing in the running system
/// replayed what it wrote -- so determinism was still only ever demonstrated by tests against their
/// own deciders.
///
/// Why the previous session rather than the current one
/// ----------------------------------------------------
/// The current log is still being appended to. Replaying a file while it grows compares a prefix
/// against a different prefix and reports a divergence that is an artefact of when each pass
/// happened to read. The most recent *completed* session is the newest file that is not the one
/// this process is writing.
///
/// Why on start-up
/// ---------------
/// It is the moment the answer is most useful and least intrusive: the previous session has just
/// ended, nothing is trading yet, and if yesterday no longer reproduces then something changed
/// between then and now -- a model artifact, a policy version, or the code itself -- which is
/// exactly what the manifest in the trace hash exists to make visible.
///
/// A divergence does not stop the host
/// -----------------------------------
/// It is recorded and surfaced. Refusing to start on a replay failure would take the system down
/// for a diagnostic result, and the honest response to "yesterday no longer reproduces" is to look
/// at why, not to stop trading today.
/// </summary>
public sealed class SessionReplayService(
    MarketDataSessionRecorder recorder,
    SessionReplayState state,
    IRuntimeClock clock,
    ILogger<SessionReplayService> logger) : BackgroundService
{
    private readonly string _root =
        Environment.GetEnvironmentVariable("QUANTDESK_REPLAY_LOG_ROOT") ?? "/app/replay-logs";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Replay();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Session replay could not run.");
            state.Update(Unavailable("REPLAY_FAILED"));
        }

        return Task.CompletedTask;
    }

    /// <summary>Replays the newest completed session, twice, and records whether it agreed.</summary>
    internal void Replay()
    {
        string? session = MostRecentCompletedSession();
        if (session is null)
        {
            // A recorder that could not open its log and a deployment that has simply not finished
            // a session yet both leave no file behind, and they mean opposite things: the second is
            // the correct reading on a fresh volume, the first is the gate quietly not running.
            // Reporting one reason for both is how a release gate stays broken -- it was, for the
            // whole of this deployment, because the log volume arrived owned by root and recording
            // degraded to disabled exactly as designed.
            if (!recorder.IsRecording)
            {
                logger.LogError(
                    "Replay recording is disabled, so no session will be produced to replay. "
                    + "Section 22's gate cannot run until the log directory is writable.");
                state.Update(Unavailable("RECORDING_DISABLED"));
                return;
            }

            state.Update(Unavailable("NO_COMPLETED_SESSION"));
            return;
        }

        IReadOnlyList<ReplayEnvelope> log;
        try
        {
            log = ReplayEventRecorder.ReadFile(session);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            logger.LogWarning(exception, "Recorded session {Session} could not be read.", session);
            state.Update(Unavailable("SESSION_UNREADABLE") with { SessionFile = session });
            return;
        }

        if (log.Count == 0)
        {
            state.Update(Unavailable("SESSION_EMPTY") with { SessionFile = session });
            return;
        }

        try
        {
            ReplayRunResult result = new ReplayRunner().RunAndProveDeterministic(
                Manifest(), log, MarketStateReplay.Decider);

            state.Update(new SessionReplaySnapshot(
                "replayed", Path.GetFileName(session), result.EventCount,
                result.DeterministicTraceHash, null, clock.UtcNow));

            logger.LogInformation(
                "Replayed {Events} events from {Session}; trace {Hash}.",
                result.EventCount, Path.GetFileName(session), result.DeterministicTraceHash);
        }
        catch (ReplayRefusedException refused)
        {
            // Both outcomes land here: a log the runner will not accept, and two passes that
            // disagreed. They are reported apart because they mean different things -- the first is
            // a bad recording, the second is something outside the log deciding part of the answer.
            string outcome = refused.Refusal is ReplayRefusal.None ? "diverged" : "refused";

            state.Update(new SessionReplaySnapshot(
                outcome, Path.GetFileName(session), log.Count, null,
                refused.Message, clock.UtcNow));

            logger.LogError(refused, "Session {Session} did not replay.", Path.GetFileName(session));
        }
    }

    /// <summary>
    /// The newest recorded session other than the one being written now.
    ///
    /// Ordered by name, which sorts chronologically because the recorder stamps each file with the
    /// moment it began. Ordering by last-write time would put the file currently being appended to
    /// first, which is the one file that must be skipped.
    /// </summary>
    private string? MostRecentCompletedSession()
    {
        if (!Directory.Exists(_root)) return null;

        string? current = recorder.Path is null ? null : Path.GetFullPath(recorder.Path);

        return Directory.GetFiles(_root, "session-*.jsonl")
            .Where(file => !string.Equals(Path.GetFullPath(file), current, StringComparison.Ordinal))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// What produced the decisions being reproduced.
    ///
    /// Folded into the trace hash, so replaying the same events after a policy change or a retrain
    /// is visibly a different run rather than a silent one. The values are placeholders until the
    /// configuration and artifact hashes are threaded through; the manifest refuses an empty field,
    /// so they cannot simply be omitted and forgotten.
    /// </summary>
    private static ReplayManifest Manifest() => new(
        ConfigurationHash: "runtime-configuration",
        ModelArtifactsHash: "runtime-artifacts",
        PolicyVersion: "market-state-v1",
        RandomSeed: 0,
        EvidenceClass: ReplayEvidenceClass.PassiveHistoricalReplay);

    private SessionReplaySnapshot Unavailable(string reason) =>
        new("unavailable", null, 0, null, reason, clock.UtcNow);
}
