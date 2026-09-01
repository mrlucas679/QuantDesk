using System.Net.Http.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Capabilities;

namespace QuantDesk.Alpaca.Capabilities;

public sealed class AlpacaCapabilityProbe(
    HttpClient httpClient,
    AlpacaOptions options) : IAlpacaCapabilityProbe
{
    public async Task<CapabilityReport> ProbeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseUrl, "/v2/account"));
        request.Headers.Add("APCA-API-KEY-ID", options.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", options.SecretKey);

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string? requestId = ReadRequestId(response);

        if (!response.IsSuccessStatusCode)
        {
            // A rejected credential and a malformed one look identical from the status alone, and the
            // difference decides whether to regenerate a key or go looking at account permissions.
            string problem = $"Alpaca account probe returned HTTP {(int)response.StatusCode}.";
            if (AlpacaCredentialShape.DescribeSuspectCredentials(options.KeyId, options.SecretKey)
                is string suspect)
                problem = $"{problem} {suspect}";
            return Unavailable(problem, requestId);
        }

        AlpacaAccount? account = await response.Content.ReadFromJsonAsync<AlpacaAccount>(
            cancellationToken: cancellationToken);

        if (account is null)
        {
            return Unavailable("Alpaca account probe returned an empty response.", requestId);
        }

        bool accountActive = string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase);
        bool tradingEnabled = accountActive && !account.TradingBlocked && !account.AccountBlocked;
        bool optionsEnabled = tradingEnabled && account.OptionsTradingLevel > 0;
        bool cryptoEnabled = tradingEnabled &&
            string.Equals(account.CryptoStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase);

        var problems = new List<string>();
        if (!accountActive) problems.Add("Alpaca account is not active.");
        if (account.TradingBlocked) problems.Add("Trading is blocked on the Alpaca account.");
        if (account.AccountBlocked) problems.Add("The Alpaca account is blocked.");
        if (!optionsEnabled) problems.Add("Options trading is not enabled.");
        if (!cryptoEnabled) problems.Add("Crypto trading is not enabled.");

        return new CapabilityReport(
            PaperEnvironment: IsPaperEnvironment(options.BaseUrl),
            EquityTrading: tradingEnabled,
            CryptoTrading: cryptoEnabled,
            OptionsTrading: optionsEnabled,
            OptionsTradingLevel: account.OptionsTradingLevel,
            TradeUpdateStream: false,
            NewsAvailable: false,
            EquityFeed: "UNVERIFIED",
            OptionFeed: "UNVERIFIED",
            EquitySubscriptionLimit: null,
            OptionSubscriptionLimit: null,
            Problems: problems,
            RequestId: requestId);
    }

    private static bool IsPaperEnvironment(Uri baseUrl) =>
        baseUrl.Scheme == Uri.UriSchemeHttps &&
        string.Equals(baseUrl.Host, AlpacaOptions.PaperApiHost, StringComparison.OrdinalIgnoreCase);

    private static string? ReadRequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Request-ID", out IEnumerable<string>? values)
            ? values.FirstOrDefault()
            : null;

    private static CapabilityReport Unavailable(string problem, string? requestId) => new(
        PaperEnvironment: false,
        EquityTrading: false,
        CryptoTrading: false,
        OptionsTrading: false,
        OptionsTradingLevel: 0,
        TradeUpdateStream: false,
        NewsAvailable: false,
        EquityFeed: "UNVERIFIED",
        OptionFeed: "UNVERIFIED",
        EquitySubscriptionLimit: null,
        OptionSubscriptionLimit: null,
        Problems: [problem],
        RequestId: requestId);

    private sealed record AlpacaAccount(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("trading_blocked")] bool TradingBlocked,
        [property: JsonPropertyName("account_blocked")] bool AccountBlocked,
        [property: JsonPropertyName("options_trading_level")] int OptionsTradingLevel,
        [property: JsonPropertyName("crypto_status")] string? CryptoStatus);
}
