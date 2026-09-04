using System.Net.Http.Json;
using System.Text.Json;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Reads an Alpaca market-data response, or fails with a message that says what the venue actually
/// answered.
///
/// Every market-data client previously called <c>EnsureSuccessStatusCode()</c>, which throws
/// "Response status code does not indicate success: 403 (Forbidden)" and discards the response body.
/// Alpaca puts the real explanation in that body — an unentitled options feed, an unapproved
/// account, a symbol outside the subscription — so the one line that would tell an operator what to
/// fix was the one line being thrown away. These clients had never been run against a real venue,
/// which is exactly when an opaque failure costs the most.
///
/// The failure stays an <see cref="HttpRequestException"/> so callers that classify transport
/// faults keep classifying this one the same way. Only the diagnosis is added.
/// </summary>
internal static class AlpacaMarketDataResponse
{
    /// <summary>
    /// Bounded so a large or non-JSON error page (a proxy's HTML, say) cannot be pasted wholesale
    /// into an exception message or a log.
    /// </summary>
    private const int MaximumReportedBodyLength = 400;

    /// <summary>
    /// Deserializes a successful response, or throws an <see cref="HttpRequestException"/> naming
    /// the endpoint, the status, and the venue's own error code and message.
    /// </summary>
    /// <param name="endpoint">
    /// The path being read, for the message. Pass the path only — a full request URI would put
    /// requested symbols and date ranges into logs.
    /// </param>
    public static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        string endpoint,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, endpoint, cancellationToken);

        T? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<T>(jsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Alpaca {endpoint} returned a body that is not the expected JSON shape: {exception.Message}",
                exception);
        }

        return payload ?? throw new InvalidDataException($"Alpaca {endpoint} returned an empty body.");
    }

    /// <summary>
    /// Throws when the venue refused the request, naming the endpoint, the status, and the venue's
    /// own error code and message. Succeeds silently otherwise, so it drops into a client that
    /// deserializes its own payload.
    /// </summary>
    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.IsSuccessStatusCode) return;
        throw await DescribeFailureAsync(response, endpoint, cancellationToken);
    }

    private static async Task<HttpRequestException> DescribeFailureAsync(
        HttpResponseMessage response,
        string endpoint,
        CancellationToken cancellationToken)
    {
        string detail;
        try
        {
            detail = Describe(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The body is a courtesy; never let reading it replace the status we already know.
            detail = "body unavailable";
        }

        return new HttpRequestException(
            $"Alpaca {endpoint} failed with {(int)response.StatusCode} {response.StatusCode}: {detail}",
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>
    /// Prefers Alpaca's structured <c>code</c>/<c>message</c> pair, falling back to a bounded
    /// excerpt when the body is not the documented error envelope.
    /// </summary>
    private static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "empty body";

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                string? message = document.RootElement.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.ToString()
                    : null;
                string? code = document.RootElement.TryGetProperty("code", out JsonElement codeElement)
                    ? codeElement.ToString()
                    : null;
                if (message is not null)
                    return code is null ? message : $"code {code}, {message}";
            }
        }
        catch (JsonException)
        {
            // Not the documented envelope; the excerpt below is still better than nothing.
        }

        string collapsed = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaximumReportedBodyLength
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, MaximumReportedBodyLength), "…");
    }
}
