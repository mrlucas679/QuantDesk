using System.Net.Http;
using System.Text.Json;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Decides what a thrown exception means to a durable execution lane.
///
/// The distinction that matters is not the exception type but what is still true afterwards. A submission
/// that timed out may already have reached the venue, so it can never be retried blindly — it has to be
/// resolved by looking the order up. A read that timed out changed nothing, so the lane may simply report
/// itself unavailable and try again later.
///
/// These predicates were duplicated across the lanes that need them. Sharing one definition keeps the
/// lanes from drifting into disagreeing about which faults are safe to retry.
/// </summary>
internal static class DiagnosticFailureClassification
{
    /// <summary>
    /// A submission whose outcome is unknown. The order may exist at the venue, so the only safe next
    /// step is a lookup by client order ID — never a second submission.
    /// </summary>
    public static bool IsAmbiguousSubmission(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is TimeoutException or TaskCanceledException or HttpRequestException or IOException;

    /// <summary>
    /// A read that failed without changing anything. Includes <see cref="JsonException"/>, because a
    /// response we cannot parse tells us nothing about broker state and must not be read as "flat".
    /// </summary>
    public static bool IsInfrastructureFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is TimeoutException or TaskCanceledException or HttpRequestException or IOException or JsonException;

    /// <summary>A durable-store fault. Cancellation is not one, so it is not excluded here.</summary>
    public static bool IsPersistenceFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            JsonException or NotSupportedException;
}
