using QuantDesk.Alpaca.Configuration;

namespace QuantDesk.Alpaca.Tests.Configuration;

/// <summary>
/// The rule that must hold above all others here: a well-formed key is never described as suspect.
/// This runs only after the venue has already refused, so a false positive would send an operator
/// hunting a credential problem that does not exist.
/// </summary>
public sealed class AlpacaCredentialShapeTests
{
    private const string PaperKeyId = "PKZK4A1B2C3D4E5F6G7H";
    private const string Secret = "J1sGh2i3hqkkucW1Szcfo2qZAgCHZcaJfacBEtHw";

    [Theory]
    [InlineData(PaperKeyId)]
    [InlineData("AKZK4A1B2C3D4E5F6G7H")]
    public void WellFormedCredentialsAreNeverCalledSuspect(string keyId) =>
        Assert.Null(AlpacaCredentialShape.DescribeSuspectCredentials(keyId, Secret));

    [Fact]
    public void AnAccountNumberPastedIntoTheKeyFieldIsNamedExactly()
    {
        // The actual mistake this exists for: Alpaca shows the account number and the key ID together.
        string? problem = AlpacaCredentialShape.DescribeSuspectCredentials("PA3TZ0I4BMUL", Secret);

        Assert.NotNull(problem);
        Assert.Contains("account number", problem, StringComparison.Ordinal);
        Assert.Contains("'PK' for paper", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", Secret, "No API key ID")]
    [InlineData("   ", Secret, "No API key ID")]
    [InlineData(PaperKeyId, "", "No API secret key")]
    [InlineData(null, Secret, "No API key ID")]
    public void AnAbsentCredentialIsReportedBeforeAnythingElse(
        string? keyId, string secret, string expected)
    {
        string? problem = AlpacaCredentialShape.DescribeSuspectCredentials(keyId, secret);

        Assert.NotNull(problem);
        Assert.Contains(expected, problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" " + PaperKeyId, Secret)]
    [InlineData(PaperKeyId, Secret + "\n")]
    public void SurroundingWhitespaceIsCalledOutBecauseItSurvivesACopyPaste(string keyId, string secret)
    {
        string? problem = AlpacaCredentialShape.DescribeSuspectCredentials(keyId, secret);

        Assert.NotNull(problem);
        Assert.Contains("whitespace", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnfamiliarPrefixIsFlaggedButHedged()
    {
        // Alpaca can issue formats this rule has never seen, so the wording must not assert a defect.
        string? problem = AlpacaCredentialShape.DescribeSuspectCredentials("ZZ4A1B2C3D4E5F6G7H8I", Secret);

        Assert.NotNull(problem);
        Assert.Contains("may issue other", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSecretMaterialAppearsInAnyDescription()
    {
        // These strings reach logs and terminals. The key ID's two-character prefix is the most that
        // may be echoed, and the secret must never appear at all.
        foreach (string? problem in new[]
        {
            AlpacaCredentialShape.DescribeSuspectCredentials("PA3TZ0I4BMUL", Secret),
            AlpacaCredentialShape.DescribeSuspectCredentials("ZZ4A1B2C3D4E5F6G7H8I", Secret),
            AlpacaCredentialShape.DescribeSuspectCredentials(" " + PaperKeyId, Secret)
        })
        {
            Assert.NotNull(problem);
            Assert.DoesNotContain(Secret, problem, StringComparison.Ordinal);
            Assert.DoesNotContain("PA3TZ0I4BMUL", problem, StringComparison.Ordinal);
            Assert.DoesNotContain(PaperKeyId, problem, StringComparison.Ordinal);
        }
    }
}
