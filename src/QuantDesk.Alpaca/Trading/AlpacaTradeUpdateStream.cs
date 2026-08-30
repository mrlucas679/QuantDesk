using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using QuantDesk.Domain.Execution;

namespace QuantDesk.Alpaca.Trading;

/// <summary>Maintains the authenticated paper-account trade-update subscription.</summary>
public sealed class AlpacaTradeUpdateStream(Uri endpoint, string apiKey, string secretKey)
{
    private readonly Uri _endpoint = ValidateEndpoint(endpoint);
    private readonly string _apiKey = Require(apiKey, nameof(apiKey));
    private readonly string _secretKey = Require(secretKey, nameof(secretKey));

    public event Action<bool>? ConnectivityChanged;

    public async IAsyncEnumerable<BrokerTradeUpdate> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(250);
        while (!cancellationToken.IsCancellationRequested)
        {
            await foreach (BrokerTradeUpdate update in ReadConnectionAsync(cancellationToken))
                yield return update;
            ConnectivityChanged?.Invoke(false);
            if (cancellationToken.IsCancellationRequested) yield break;
            await Task.Delay(retryDelay, cancellationToken);
            retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 10_000));
        }
    }

    private async IAsyncEnumerable<BrokerTradeUpdate> ReadConnectionAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(_endpoint, cancellationToken);
            await SendAsync(socket, new { action = "auth", key = _apiKey, secret = _secretKey }, cancellationToken);
            if (!IsAuthorized(await ReceiveAsync(socket, cancellationToken))) yield break;
            await SendAsync(socket, new { action = "listen", data = new { streams = new[] { "trade_updates" } } }, cancellationToken);
            if (!IsListening(await ReceiveAsync(socket, cancellationToken))) yield break;
            ConnectivityChanged?.Invoke(true);
        }
        catch (WebSocketException)
        {
            yield break;
        }

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            string? payload;
            try { payload = await ReceiveAsync(socket, cancellationToken); }
            catch (WebSocketException) { yield break; }
            if (payload is null) yield break;
            if (AlpacaTradeUpdateParser.TryParse(payload, out BrokerTradeUpdate update))
                yield return update;
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
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

    private static bool IsAuthorized(string? payload) => HasNestedValue(payload, "authorization", "status", "authorized");

    private static bool IsListening(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            return root.TryGetProperty("stream", out JsonElement stream) && stream.GetString() == "listening" &&
                root.TryGetProperty("data", out JsonElement data) && data.TryGetProperty("streams", out JsonElement streams) &&
                streams.EnumerateArray().Any(item => item.GetString() == "trade_updates");
        }
        catch (JsonException) { return false; }
    }

    private static bool HasNestedValue(string? payload, string streamName, string property, string expected)
    {
        if (string.IsNullOrWhiteSpace(payload)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            return root.TryGetProperty("stream", out JsonElement stream) && stream.GetString() == streamName &&
                root.TryGetProperty("data", out JsonElement data) && data.TryGetProperty(property, out JsonElement value) &&
                value.GetString() == expected;
        }
        catch (JsonException) { return false; }
    }

    private static Uri ValidateEndpoint(Uri endpoint) => endpoint.Scheme == Uri.UriSchemeWss
        ? endpoint
        : throw new ArgumentException("Trade-update endpoint must use wss.", nameof(endpoint));

    private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Credential is required.", name)
        : value;
}
