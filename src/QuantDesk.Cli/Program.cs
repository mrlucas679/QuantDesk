using System.Text.Json;
using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Market;

return await QuantDeskCli.RunAsync(args, CancellationToken.None);

internal static class QuantDeskCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: quantdesk capabilities|stream-test");
            return 2;
        }

        try
        {
            AlpacaOptions options = AlpacaOptions.FromEnvironment();
            if (string.Equals(args[0], "stream-test", StringComparison.OrdinalIgnoreCase))
                return await VerifyMarketDataStreamAsync(options, cancellationToken);
            if (!string.Equals(args[0], "capabilities", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Usage: quantdesk capabilities|stream-test");
                return 2;
            }
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var probe = new AlpacaCapabilityProbe(httpClient, options);
            CapabilityReport report = await probe.ProbeAsync(cancellationToken);

            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }));

            return report.PaperEnvironment && report.EquityTrading && report.OptionsTrading ? 0 : 1;
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("The Alpaca paper API could not be reached.");
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("The Alpaca paper API request timed out.");
            return 1;
        }
    }

    private static async Task<int> VerifyMarketDataStreamAsync(AlpacaOptions options, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var parser = new AlpacaMarketDataParser(new Dictionary<string, int>(StringComparer.Ordinal) { ["FAKEPACA"] = 0 });
        var stream = new AlpacaMarketDataStream(
            new Uri("wss://stream.data.alpaca.markets/v2/test"), options.KeyId, options.SecretKey, parser);
        try
        {
            await foreach (NormalizedMarketEvent marketEvent in stream.ReadAsync(["FAKEPACA"], timeout.Token))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { connected = true, eventKind = marketEvent.Kind.ToString() }));
                return 0;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Report a bounded test failure below without emitting exception details.
        }
        Console.Error.WriteLine("The Alpaca test market-data stream did not produce a normalized event within 20 seconds.");
        return 1;
    }
}
