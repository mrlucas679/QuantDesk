using System.Text.Json;
using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Domain.Capabilities;

return await QuantDeskCli.RunAsync(args, CancellationToken.None);

internal static class QuantDeskCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 1 || !string.Equals(args[0], "capabilities", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: quantdesk capabilities");
            return 2;
        }

        try
        {
            AlpacaOptions options = AlpacaOptions.FromEnvironment();
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
}

