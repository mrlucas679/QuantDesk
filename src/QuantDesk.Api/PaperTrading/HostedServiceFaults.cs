namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Distinguishes a background service being shut down from one that actually failed.
///
/// The obvious filter — <c>when (exception is not OperationCanceledException)</c> — is wrong, and wrong
/// in the most damaging direction. <see cref="HttpClient"/> reports its own timeouts as
/// <see cref="TaskCanceledException"/>, which *is* an <see cref="OperationCanceledException"/>, so that
/// filter declines to catch the single most common failure a polling service meets. The exception then
/// escapes <c>ExecuteAsync</c>, and the .NET host stops the entire application by default.
///
/// This is not theoretical: the API was killed by a research-readiness probe timing out, taking the
/// trading lanes down with it. A background probe losing a connection must never stop a process that
/// may be holding an open position.
///
/// The question is therefore not what type the exception is, but whether *this service was asked to
/// stop*. Only then is a cancellation genuine.
/// </summary>
internal static class HostedServiceFaults
{
    /// <summary>
    /// True when the exception represents a real fault the service should absorb and log, rather than
    /// the host shutting it down. A cancellation counts as shutdown only when the stopping token was
    /// actually signalled.
    /// </summary>
    public static bool IsFault(Exception exception, CancellationToken stoppingToken) =>
        exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested;
}
