using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using QuantDesk.Domain.Market;

namespace QuantDesk.Alpaca.MarketData;

public sealed class AlpacaMarketDataStream(
    Uri endpoint,
    string apiKey,
    string secretKey,
    IAlpacaMarketDataParser parser)
{
    private readonly Uri _validatedEndpoint = ValidateEndpoint(endpoint);
    private readonly string _validatedApiKey = ValidateCredential(apiKey, nameof(apiKey));
    private readonly string _validatedSecretKey = ValidateCredential(secretKey, nameof(secretKey));

    public async IAsyncEnumerable<NormalizedMarketEvent> ReadAsync(
        IReadOnlyCollection<string> symbols,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(250);
        while (!cancellationToken.IsCancellationRequested)
        {
            await foreach (NormalizedMarketEvent value in ReadConnectionAsync(symbols, cancellationToken))
                yield return value;
            if (cancellationToken.IsCancellationRequested)
                yield break;
            await Task.Delay(retryDelay, cancellationToken);
            retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 10_000));
        }
    }

    private async IAsyncEnumerable<NormalizedMarketEvent> ReadConnectionAsync(
        IReadOnlyCollection<string> symbols,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(_validatedEndpoint, cancellationToken);
            string? welcome = await ReceiveMessageAsync(socket, cancellationToken);
            if (!AlpacaStreamHandshake.IsSuccess(welcome, "connected")) yield break;
            await SendAsync(socket, new { action = "auth", key = _validatedApiKey, secret = _validatedSecretKey }, cancellationToken);
            string? authentication = await ReceiveMessageAsync(socket, cancellationToken);
            if (!AlpacaStreamHandshake.IsSuccess(authentication, "authenticated")) yield break;
            await SendAsync(socket, new { action = "subscribe", quotes = symbols, trades = symbols }, cancellationToken);
        }
        catch (WebSocketException)
        {
            yield break;
        }
        byte[] buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using MemoryStream message = new();
            WebSocketReceiveResult result;
            do
            {
                try
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken);
                }
                catch (WebSocketException)
                {
                    yield break;
                }
                if (result.MessageType == WebSocketMessageType.Close)
                    yield break;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            string payload = Encoding.UTF8.GetString(message.ToArray());
            foreach (NormalizedMarketEvent normalized in parser.ParseMany(payload, Stopwatch.GetTimestamp()))
                yield return normalized;
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8 * 1024];
        using MemoryStream message = new();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Market-data endpoint must use wss.", nameof(endpoint));
        return endpoint;
    }

    private static string ValidateCredential(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Credential is required.", name) : value;
}

/// <summary>Validates Alpaca's mandatory connection and authentication acknowledgements.</summary>
public static class AlpacaStreamHandshake
{
    public static bool IsSuccess(string? payload, string expectedMessage)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            JsonElement message = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;
            return message.ValueKind == JsonValueKind.Object &&
                message.TryGetProperty("T", out JsonElement type) &&
                message.TryGetProperty("msg", out JsonElement text) &&
                string.Equals(type.GetString(), "success", StringComparison.Ordinal) &&
                string.Equals(text.GetString(), expectedMessage, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
