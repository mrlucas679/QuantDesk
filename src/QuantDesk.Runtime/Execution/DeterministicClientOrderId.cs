using System.Security.Cryptography;
using System.Text;

namespace QuantDesk.Runtime.Execution;

/// <summary>
/// Derives a broker client-order ID from the identity of the opportunity rather than from a random
/// number.
///
/// Why this matters more than it looks: when a POST's outcome is ambiguous — a timeout, a dropped
/// connection — the only safe recovery is to ask the broker whether the order already exists, by
/// its client-order ID. That lookup is possible only if the ID can be recomputed from information
/// the application still holds. A <c>Guid.NewGuid()</c> ID is unrecoverable by construction: after
/// the process forgets it, the order can neither be found nor ruled out, and the safe options
/// collapse to "halt" or "risk a duplicate".
///
/// The diagnostic and multi-leg lanes each grew their own copy of this scheme. This is the single
/// definition; the derivation is pure, so the same inputs always yield the same ID.
/// </summary>
public static class DeterministicClientOrderId
{
    /// <summary>Alpaca accepts client order IDs well beyond this; staying short keeps logs legible.</summary>
    private const int DigestLength = 24;

    /// <summary>
    /// Builds an ID from a lane prefix, a stable opportunity identity, and a leg name.
    /// </summary>
    /// <param name="lane">Short lane discriminator, for example <c>auto</c> or <c>opt</c>.</param>
    /// <param name="opportunityIdentity">
    /// Everything that makes this opportunity unique and that the application can reproduce later.
    /// Callers must not include a clock reading or a random value here, or recoverability is lost.
    /// </param>
    /// <param name="leg">Leg name, for example <c>entry</c> or <c>exit</c>.</param>
    public static string Create(string lane, string opportunityIdentity, string leg)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lane);
        ArgumentException.ThrowIfNullOrWhiteSpace(opportunityIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);

        string normalizedLane = lane.Trim().ToLowerInvariant();
        string normalizedLeg = leg.Trim().ToLowerInvariant();
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{normalizedLane}:{opportunityIdentity.Trim()}:{normalizedLeg}"));
        string digest = Convert.ToHexString(hash)[..DigestLength].ToLowerInvariant();
        return $"qd-{normalizedLane}-{digest}-{normalizedLeg}";
    }
}
