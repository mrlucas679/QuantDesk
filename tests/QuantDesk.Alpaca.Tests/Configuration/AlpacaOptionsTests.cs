using QuantDesk.Alpaca.Configuration;

namespace QuantDesk.Alpaca.Tests.Configuration;

/// <summary>
/// The repository's central safety claim is that only approved Alpaca hosts are ever contacted.
/// That was previously enforced on the trading host alone, while the market-data host was a string
/// literal repeated across eight clients. These tests pin both halves.
/// </summary>
public sealed class AlpacaOptionsTests : IDisposable
{
    private static readonly string[] Variables =
        ["APCA_API_BASE_URL", "APCA_API_KEY_ID", "APCA_API_SECRET_KEY", "APCA_API_DATA_URL"];

    public AlpacaOptionsTests() => SetValidCredentials();

    [Fact]
    public void TheDataHostDefaultsToTheApprovedAlpacaDataHost()
    {
        AlpacaOptions options = AlpacaOptions.FromEnvironment();

        Assert.Equal(AlpacaOptions.DataApiHost, options.DataBaseUrl.Host);
        Assert.Equal(Uri.UriSchemeHttps, options.DataBaseUrl.Scheme);
    }

    [Fact]
    public void DataUriComposesAgainstTheValidatedHost()
    {
        AlpacaOptions options = AlpacaOptions.FromEnvironment();

        string uri = options.DataUri("v2/stocks/bars?symbols=SPY");

        Assert.Equal("https://data.alpaca.markets/v2/stocks/bars?symbols=SPY", uri);
    }

    [Fact]
    public void DataUriToleratesALeadingSlashWithoutDoublingIt()
    {
        AlpacaOptions options = AlpacaOptions.FromEnvironment();

        Assert.Equal(
            options.DataUri("v2/stocks/bars"),
            options.DataUri("/v2/stocks/bars"));
    }

    [Theory]
    [InlineData("https://data.example.com")]
    [InlineData("http://data.alpaca.markets")]
    [InlineData("https://evil.markets")]
    [InlineData("not-a-uri")]
    public void AnUnapprovedOrInsecureDataHostIsRejected(string dataUrl)
    {
        Environment.SetEnvironmentVariable("APCA_API_DATA_URL", dataUrl);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(AlpacaOptions.FromEnvironment);
        Assert.Contains("APCA_API_DATA_URL", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.alpaca.markets")]
    [InlineData("http://paper-api.alpaca.markets")]
    [InlineData("https://live.alpaca.markets")]
    public void AnyTradingHostOtherThanPaperIsRejected(string baseUrl)
    {
        Environment.SetEnvironmentVariable("APCA_API_BASE_URL", baseUrl);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(AlpacaOptions.FromEnvironment);
        Assert.Contains(AlpacaOptions.PaperApiHost, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingCredentialsAreRejected()
    {
        Environment.SetEnvironmentVariable("APCA_API_KEY_ID", null);

        Assert.Throws<InvalidOperationException>(AlpacaOptions.FromEnvironment);
    }

    [Fact]
    public void BothHostsCarryATrailingSlashSoRelativeCompositionIsSafe()
    {
        AlpacaOptions options = AlpacaOptions.FromEnvironment();

        Assert.EndsWith("/", options.DataBaseUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DataUriRejectsAnEmptyPath(string? relative)
    {
        AlpacaOptions options = AlpacaOptions.FromEnvironment();

        Assert.ThrowsAny<ArgumentException>(() => options.DataUri(relative!));
    }

    public void Dispose()
    {
        foreach (string variable in Variables) Environment.SetEnvironmentVariable(variable, null);
    }

    private static void SetValidCredentials()
    {
        Environment.SetEnvironmentVariable("APCA_API_BASE_URL", "https://paper-api.alpaca.markets");
        Environment.SetEnvironmentVariable("APCA_API_KEY_ID", "test-key");
        Environment.SetEnvironmentVariable("APCA_API_SECRET_KEY", "test-secret");
        Environment.SetEnvironmentVariable("APCA_API_DATA_URL", null);
    }
}
