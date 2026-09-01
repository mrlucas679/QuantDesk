namespace QuantDesk.Alpaca.Configuration;

/// <summary>
/// Explains a rejected credential by the shape of what was configured, when the shape is wrong in a
/// recognisable way.
///
/// The venue answers a malformed key and a revoked key identically — <c>401 unauthorized</c> — so the
/// status alone cannot tell an operator whether to regenerate a key or to go looking at account
/// permissions. The most common cause is not a bad key at all: Alpaca shows an account number and an
/// API key ID a few centimetres apart in the same dashboard, and the account number is what gets
/// pasted.
///
/// This is deliberately **advisory and never a gate**. It runs only after the venue has already
/// refused, and it only adds a sentence. Alpaca can change its key formats whenever it likes, and a
/// shape rule that blocked a request would then reject working credentials — a far worse failure than
/// the opaque one it set out to fix. Guessing wrong here costs a misleading sentence; guessing wrong
/// in a gate would cost the whole system.
/// </summary>
public static class AlpacaCredentialShape
{
    /// <summary>An Alpaca account number: a short, upper-case identifier beginning <c>PA</c>.</summary>
    private const int LongestAccountNumber = 16;

    /// <summary>
    /// Returns a sentence explaining what looks wrong about the configured credentials, or null when
    /// nothing recognisable is. Describes shape only — length and prefix — and never reproduces a
    /// secret.
    /// </summary>
    public static string? DescribeSuspectCredentials(string? keyId, string? secretKey)
    {
        string key = keyId ?? string.Empty;
        string secret = secretKey ?? string.Empty;

        if (string.IsNullOrWhiteSpace(key)) return "No API key ID is configured.";
        if (string.IsNullOrWhiteSpace(secret)) return "No API secret key is configured.";

        if (key.Length != key.Trim().Length || secret.Length != secret.Trim().Length)
        {
            return "The configured credentials have leading or trailing whitespace, which usually " +
                   "means they were pasted with surrounding characters.";
        }

        // Checked before the prefix rules, which would otherwise report a baffling "begins '<P'".
        if (IsWrapped(key) || IsWrapped(secret))
        {
            return "The configured credentials are wrapped in brackets or quotes, which usually means " +
                   "a placeholder's punctuation was copied along with the value. The key and secret " +
                   "should be bare, with nothing around them.";
        }

        if (LooksLikeAccountNumber(key))
        {
            return $"The configured key ID is {key.Length} characters beginning '{key[..2]}', which is " +
                   "the shape of an Alpaca account number rather than an API key ID. API key IDs begin " +
                   "'PK' for paper and 'AK' for live. Check that the account number was not pasted into " +
                   "the key-ID field.";
        }

        if (!key.StartsWith("PK", StringComparison.Ordinal) &&
            !key.StartsWith("AK", StringComparison.Ordinal))
        {
            return $"The configured key ID begins '{key[..Math.Min(2, key.Length)]}', not the usual 'PK' " +
                   "(paper) or 'AK' (live) prefix. This is worth checking, though Alpaca may issue other " +
                   "formats.";
        }

        return null;
    }

    /// <summary>
    /// A value still carrying a placeholder's punctuation — <c>&lt;PK...&gt;</c>, <c>"PK..."</c>. The
    /// venue rejects these the same way it rejects a wrong key, so the punctuation is worth naming.
    /// </summary>
    private static bool IsWrapped(string value) =>
        value.Length >= 2 &&
        (WrappingPunctuation.Contains(value[0]) || WrappingPunctuation.Contains(value[^1]));

    /// <summary>
    /// Punctuation that never belongs in a credential, so its presence at either end is enough. No
    /// attempt is made to pair an opening character with its closing one: a lone bracket at one end is
    /// just as wrong, and just as worth naming.
    /// </summary>
    private const string WrappingPunctuation = "<>\"'[]`";

    private static bool LooksLikeAccountNumber(string key) =>
        key.Length <= LongestAccountNumber &&
        key.StartsWith("PA", StringComparison.Ordinal) &&
        key.All(char.IsAsciiLetterOrDigit) &&
        !key.Any(char.IsAsciiLetterLower);
}
