using System.Text.Json;
using System.Text.Json.Serialization;
using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.Trading;
using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Trading;

return await QuantDeskCli.RunAsync(args, CancellationToken.None);

internal static class QuantDeskCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: quantdesk capabilities|stream-test|paper-order-smoke|option-preflight");
            return 2;
        }

        try
        {
            AlpacaOptions options = AlpacaOptions.FromEnvironment();
            if (string.Equals(args[0], "stream-test", StringComparison.OrdinalIgnoreCase))
                return await VerifyMarketDataStreamAsync(options, cancellationToken);
            if (string.Equals(args[0], "paper-order-smoke", StringComparison.OrdinalIgnoreCase))
                return await VerifyPaperOrderLifecycleAsync(options, cancellationToken);
            if (string.Equals(args[0], "option-preflight", StringComparison.OrdinalIgnoreCase))
                return await VerifyOptionDataAsync(options, cancellationToken);
            if (!string.Equals(args[0], "capabilities", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("Usage: quantdesk capabilities|stream-test|paper-order-smoke|option-preflight");
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
        catch (HttpRequestException exception)
        {
            // Print the message rather than a generic line. The clients now carry the venue's own
            // status, endpoint and error text, and swallowing it here would throw away the only thing
            // that says whether this is an outage, a bad key, or an unentitled feed.
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("The Alpaca paper API request timed out.");
            return 1;
        }
    }

    /// <summary>
    /// Enum members are written by name. This report exists to be read by a person on first contact
    /// with the venue, and "Outcome": 1 tells them nothing.
    /// </summary>
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Exercises every option data path against the live venue, read-only, and prints what each one
    /// returned. This is the command to run the moment credentials exist: no option client in this
    /// repository has ever been run against Alpaca, so the first contact should produce a report
    /// rather than a stack trace.
    /// </summary>
    private static async Task<int> VerifyOptionDataAsync(
        AlpacaOptions options, CancellationToken cancellationToken)
    {
        string underlying = Environment.GetEnvironmentVariable("QUANTDESK_PREFLIGHT_UNDERLYING") ?? "SPY";
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var preflight = new OptionDataPreflight(
            new AlpacaOptionContractClient(httpClient, options),
            new AlpacaLatestOptionQuoteClient(httpClient, options),
            new AlpacaOptionRiskSnapshotClient(httpClient, options),
            new AlpacaHistoricalOptionBarClient(httpClient, options));

        DateTimeOffset asOf = DateTimeOffset.UtcNow;
        DateOnly today = DateOnly.FromDateTime(asOf.UtcDateTime);
        OptionPreflightReport report = await preflight.RunAsync(
            underlying, today, today.AddDays(45), asOf, cancellationToken);

        Console.WriteLine(JsonSerializer.Serialize(report, ReportJson));
        return report.Passed ? 0 : 1;
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

    /// <summary>Verifies paper order submission and cancellation with an intentionally non-marketable order.</summary>
    private static async Task<int> VerifyPaperOrderLifecycleAsync(AlpacaOptions options, CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var resolver = new DictionaryInstrumentSymbolResolver(new Dictionary<int, string> { [0] = "SPY" });
        var gateway = new AlpacaTradingGateway(httpClient, options, resolver);
        BrokerAccountSnapshot? account = await gateway.GetAccountAsync(cancellationToken);
        if (account is null || account.TradingBlocked || account.AccountBlocked ||
            !string.Equals(account.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The Alpaca paper account is not available for the order-lifecycle smoke test.");
            return 1;
        }

        string clientOrderId = $"qd-smoke-{Guid.NewGuid():N}";

        // Stopwatch directly, and deliberately. This is a one-shot smoke tool that is never
        // replayed, it references only the Alpaca project, and both readings below come from the
        // same source so the arithmetic is self-consistent. Pulling in QuantDesk.Runtime for a
        // clock would widen the dependency graph of a tool for cosmetic uniformity -- so the rule
        // it looks like it is breaking does not apply here, and this comment is why nobody should
        // "fix" it.
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var command = new ExecutionCommand(
            CommandId: now,
            Priority: ExecutionPriority.ExplorationEntry,
            RiskReservationId: 1,
            CapitalReservationId: 1,
            ClientOrderId: clientOrderId,
            InstrumentSlot: 0,
            Side: OrderSide.Buy,
            PositionIntent: PositionIntent.Open,
            OrderType: ExecutionOrderType.Limit,
            TimeInForce: ExecutionTimeInForce.Day,
            Quantity: 1,
            LimitPrice: 1m,
            CreatedMonotonicTicks: now,
            ExpiresMonotonicTicks: now + (System.Diagnostics.Stopwatch.Frequency * 60),
            StrategyId: "paper-order-smoke");
        BrokerSubmitResult submission = await gateway.SubmitAsync(command, cancellationToken);
        if (submission.State != BrokerSubmitState.Acknowledged || string.IsNullOrWhiteSpace(submission.BrokerOrderId))
        {
            Console.Error.WriteLine("Alpaca did not acknowledge the paper order-lifecycle smoke test.");
            return 1;
        }

        bool cancelled = false;
        try
        {
            BrokerOrderSnapshot? order = await gateway.FindByClientOrderIdAsync(clientOrderId, cancellationToken);
            if (order is null || !string.Equals(order.BrokerOrderId, submission.BrokerOrderId, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("The acknowledged paper order could not be reconciled by client order id.");
                return 1;
            }
        }
        finally
        {
            BrokerSubmitResult cancellation = await gateway.CancelAsync(submission.BrokerOrderId, cancellationToken);
            cancelled = cancellation.State == BrokerSubmitState.Acknowledged;
            if (!cancelled)
                Console.Error.WriteLine("Warning: the smoke-test order cancellation was not acknowledged; reconcile it in Alpaca immediately.");
        }

        if (!cancelled) return 1;
        if (!await IsCancelledAsync(gateway, clientOrderId, cancellationToken))
        {
            Console.Error.WriteLine("The paper smoke-test order did not reach the canceled state within five seconds.");
            return 1;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { submitted = true, reconciled = true, cancelled = true }));
        return 0;
    }

    private static async Task<bool> IsCancelledAsync(
        AlpacaTradingGateway gateway, string clientOrderId, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            BrokerOrderSnapshot? order = await gateway.FindByClientOrderIdAsync(clientOrderId, cancellationToken);
            if (string.Equals(order?.Status, "canceled", StringComparison.OrdinalIgnoreCase)) return true;
            if (attempt < 9) await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        return false;
    }
}
