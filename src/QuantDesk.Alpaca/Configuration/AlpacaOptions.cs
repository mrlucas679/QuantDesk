namespace QuantDesk.Alpaca.Configuration;

public sealed class AlpacaOptions
{
    public const string PaperApiHost = "paper-api.alpaca.markets";

    /// <summary>
    /// The only market-data host this application may contact.
    ///
    /// The trading host has always been validated, but the data host was previously a string
    /// literal repeated across eight market-data clients. That left the repository's central safety
    /// claim — that only approved Alpaca hosts are ever contacted — enforced on one of two hosts,
    /// with no single place to review a change and no way to point the clients at a replay.
    /// </summary>
    public const string DataApiHost = "data.alpaca.markets";

    public required Uri BaseUrl { get; init; }

    /// <summary>Validated base for every market-data read. Always ends with a trailing slash.</summary>
    public required Uri DataBaseUrl { get; init; }

    public required string KeyId { get; init; }

    public required string SecretKey { get; init; }

    /// <summary>Builds an absolute market-data request URI from a relative path and query.</summary>
    public string DataUri(string relativePathAndQuery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePathAndQuery);
        return new Uri(DataBaseUrl, relativePathAndQuery.TrimStart('/')).AbsoluteUri;
    }

    public static AlpacaOptions FromEnvironment()
    {
        string baseUrl = RequireEnvironmentVariable("APCA_API_BASE_URL");
        string keyId = RequireEnvironmentVariable("APCA_API_KEY_ID");
        string secretKey = RequireEnvironmentVariable("APCA_API_SECRET_KEY");
        string dataUrl = Environment.GetEnvironmentVariable("APCA_API_DATA_URL")?.Trim() is { Length: > 0 } configured
            ? configured
            : $"https://{DataApiHost}";

        return new AlpacaOptions
        {
            BaseUrl = RequireApprovedHost(baseUrl, PaperApiHost, "APCA_API_BASE_URL"),
            DataBaseUrl = RequireApprovedHost(dataUrl, DataApiHost, "APCA_API_DATA_URL"),
            KeyId = keyId,
            SecretKey = secretKey
        };
    }

    /// <summary>Accepts only HTTPS on the one approved host, with a trailing slash for composition.</summary>
    private static Uri RequireApprovedHost(string value, string approvedHost, string variableName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, approvedHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{variableName} must use HTTPS and the approved host '{approvedHost}'.");
        }

        return uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    private static string RequireEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required environment variable '{name}' is missing.")
            : value.Trim();
    }
}

