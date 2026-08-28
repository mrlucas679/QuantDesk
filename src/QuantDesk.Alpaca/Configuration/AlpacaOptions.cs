namespace QuantDesk.Alpaca.Configuration;

public sealed class AlpacaOptions
{
    public const string PaperApiHost = "paper-api.alpaca.markets";

    public required Uri BaseUrl { get; init; }

    public required string KeyId { get; init; }

    public required string SecretKey { get; init; }

    public static AlpacaOptions FromEnvironment()
    {
        string baseUrl = RequireEnvironmentVariable("APCA_API_BASE_URL");
        string keyId = RequireEnvironmentVariable("APCA_API_KEY_ID");
        string secretKey = RequireEnvironmentVariable("APCA_API_SECRET_KEY");

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, PaperApiHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"APCA_API_BASE_URL must use HTTPS and the approved paper host '{PaperApiHost}'.");
        }

        return new AlpacaOptions
        {
            BaseUrl = uri,
            KeyId = keyId,
            SecretKey = secretKey
        };
    }

    private static string RequireEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required environment variable '{name}' is missing.")
            : value.Trim();
    }
}

