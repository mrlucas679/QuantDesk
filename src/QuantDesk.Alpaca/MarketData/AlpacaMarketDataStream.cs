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
    IAlpacaMarketDataParser parser,
    Action<string, Exception?>? diagnostic = null)
{
    private readonly Uri _validatedEndpoint = ValidateEndpoint(endpoint);
    private readonly string _validatedApiKey = ValidateCredential(apiKey, nameof(apiKey));
    private readonly string _validatedSecretKey = ValidateCredential(secretKey, nameof(secretKey));

    /// <summary>Reports whether the authenticated market-data subscription is live.</summary>
    public event Action<bool>? ConnectivityChanged;

    public async IAsyncEnumerable<NormalizedMarketEvent> ReadAsync(
        IReadOnlyCollection<string> symbols,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TimeSpan retryDelay = TimeSpan.FromMilliseconds(250);
        while (!cancellationToken.IsCancellationRequested)
        {
            await foreach (NormalizedMarketEvent value in ReadConnectionAsync(symbols, cancellationToken))
                yield return value;
            ConnectivityChanged?.Invoke(false);
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
        using CancellationTokenSource handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        CancellationToken handshakeToken = handshakeTimeout.Token;
        try
        {
            diagnostic?.Invoke("Alpaca market-data stream opening connection.", null);
            await ConnectWithTimeoutAsync(socket, cancellationToken);
            string? welcome = await ReceiveMessageAsync(socket, handshakeToken);
            if (!AlpacaStreamHandshake.IsSuccess(welcome, "connected"))
            {
                diagnostic?.Invoke("Alpaca market-data stream did not acknowledge connection.", null);
                yield break;
            }
            await SendAsync(socket, new { action = "auth", key = _validatedApiKey, secret = _validatedSecretKey }, handshakeToken);
            string? authentication = await ReceiveMessageAsync(socket, handshakeToken);
            if (!AlpacaStreamHandshake.IsSuccess(authentication, "authenticated"))
            {
                diagnostic?.Invoke("Alpaca market-data stream did not acknowledge authentication.", null);
                yield break;
            }
            await SendAsync(socket, new { action = "subscribe", quotes = symbols, trades = symbols, orderbooks = symbols }, handshakeToken);
            if (!AlpacaStreamHandshake.IsSubscriptionAccepted(
                    await ReceiveMessageAsync(socket, handshakeToken), symbols))
            {
                diagnostic?.Invoke("Alpaca market-data stream did not acknowledge every requested channel.", null);
                yield break;
            }
            ConnectivityChanged?.Invoke(true);
        }
        catch (WebSocketException exception)
        {
            diagnostic?.Invoke("Alpaca market-data stream connection failed.", exception);
            yield break;
        }
        catch (TimeoutException)
        {
            diagnostic?.Invoke("Alpaca market-data stream connection timed out.", null);
            yield break;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostic?.Invoke("Alpaca market-data stream handshake timed out.", null);
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

    /// <summary>
    /// Connects with a deadline that remains effective when the platform WebSocket implementation
    /// does not promptly observe cancellation during DNS or TLS setup.
    /// </summary>
    private async Task ConnectWithTimeoutAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        Task connection = socket.ConnectAsync(_validatedEndpoint, cancellationToken);
        Task timeout = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        if (await Task.WhenAny(connection, timeout) != connection)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = connection.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new TimeoutException("The Alpaca market-data connection did not complete before its deadline.");
        }

        await connection;
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

    /// <summary>Requires Alpaca to acknowledge every requested quote, trade, and order-book subscription.</summary>
    public static bool IsSubscriptionAccepted(string? payload, IReadOnlyCollection<string> symbols)
    {
        if (string.IsNullOrWhiteSpace(payload) || symbols.Count == 0) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            JsonElement subscription = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;
            if (subscription.ValueKind != JsonValueKind.Object ||
                !subscription.TryGetProperty("T", out JsonElement type) ||
                !string.Equals(type.GetString(), "subscription", StringComparison.Ordinal))
                return false;

            HashSet<string> expected = new(symbols, StringComparer.Ordinal);
            return ContainsAll(subscription, "quotes", expected) &&
                ContainsAll(subscription, "trades", expected) &&
                ContainsAll(subscription, "orderbooks", expected);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsAll(JsonElement subscription, string property, IReadOnlySet<string> expected)
    {
        if (!subscription.TryGetProperty(property, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
            return false;
        HashSet<string> actual = values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
        return expected.IsSubsetOf(actual);
    }
}
