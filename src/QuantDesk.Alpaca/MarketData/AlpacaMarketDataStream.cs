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
            await SendAsync(socket, new { action = "auth", key = _validatedApiKey, secret = _validatedSecretKey }, cancellationToken);
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
