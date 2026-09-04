using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;

namespace QuantDesk.Api.Tests;

public sealed class OptionResearchDatasetExporterTests : IDisposable
{
    private const string CallSymbol = "SPY260904C00600000";
    private const string PutSymbol = "SPY260904P00600000";
    private static readonly DateOnly Expiry = new(2026, 9, 4);
    private static readonly DateTimeOffset GeneratedAt = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStart = new(2026, 8, 31, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2026, 8, 31, 20, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"quantdesk-option-dataset-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExportContractSnapshotWritesImmutableHashedProvenance()
    {
        OptionResearchDatasetExporter exporter = Exporter(
            contractPages: [ContractPage(null, Contract("one", CallSymbol), Contract("two", PutSymbol))]);

        OptionDatasetManifest manifest = await exporter.ExportContractSnapshotAsync(
            _root, "SPY", Expiry, Expiry, "inactive", GeneratedAt, CancellationToken.None);

        Assert.Equal("option-contract-snapshot", manifest.Kind);
        Assert.Equal(2, manifest.RowCount);
        Assert.Equal(1, manifest.PageCount);
        Assert.Single(manifest.SourceUris);
        Assert.StartsWith("sha256:", manifest.Sha256, StringComparison.Ordinal);
        Assert.Equal(GeneratedAt, manifest.GeneratedAt);
        Assert.Equal(Expiry, manifest.ExpirationStart);
        Assert.Contains("/v2/options/contracts", manifest.SourceUris[0], StringComparison.Ordinal);
        Assert.DoesNotContain("test-secret", manifest.SourceUris[0], StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(_root, manifest.DataFile)));
        Assert.True(File.Exists(Path.Combine(_root, $"{manifest.DatasetId}.manifest.json")));
        Assert.True(File.Exists(Path.Combine(_root, "latest-spy-option-contracts.json")));
    }

    [Fact]
    public async Task ExportContractSnapshotIsDeterministicForIdenticalBrokerContent()
    {
        string page = ContractPage(null, Contract("one", CallSymbol));

        OptionDatasetManifest first = await Exporter(contractPages: [page]).ExportContractSnapshotAsync(
            _root, "SPY", Expiry, Expiry, "inactive", GeneratedAt, CancellationToken.None);
        OptionDatasetManifest second = await Exporter(contractPages: [page]).ExportContractSnapshotAsync(
            _root, "SPY", Expiry, Expiry, "inactive", GeneratedAt.AddHours(1), CancellationToken.None);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(first.DatasetId, second.DatasetId);
    }

    [Fact]
    public async Task ExportContractSnapshotRefusesToPublishAnEmptyUniverse()
    {
        OptionResearchDatasetExporter exporter = Exporter(contractPages: [ContractPage(null)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportContractSnapshotAsync(
            _root, "SPY", Expiry, Expiry, "inactive", GeneratedAt, CancellationToken.None));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ExportBarDatasetBindsToItsContractSnapshotAndUnderlyingDataset()
    {
        OptionResearchDatasetExporter exporter = Exporter(
            contractPages: [ContractPage(null, Contract("one", CallSymbol))],
            barPages: [BarPage(null, CallSymbol)]);
        OptionDatasetManifest snapshot = await exporter.ExportContractSnapshotAsync(
            _root, "SPY", Expiry, Expiry, "inactive", GeneratedAt, CancellationToken.None);

        OptionDatasetManifest bars = await exporter.ExportBarDatasetAsync(
            _root, snapshot, [CallSymbol], WindowStart, WindowEnd, "1Day",
            "sha256:spy-daily", GeneratedAt, CancellationToken.None);

        Assert.Equal("option-bar-dataset", bars.Kind);
        Assert.Equal(1, bars.RowCount);
        Assert.Equal("1Day", bars.Timeframe);
        Assert.Equal(WindowStart, bars.WindowStart);
        Assert.Equal(WindowEnd, bars.WindowEnd);
        Assert.Equal(snapshot.Sha256, bars.ContractSnapshotSha256);
        Assert.Equal("sha256:spy-daily", bars.UnderlyingDatasetSha256);
        Assert.Equal(snapshot.ExpirationStart, bars.ExpirationStart);
        Assert.True(File.Exists(Path.Combine(_root, "latest-spy-option-1day-manifest.json")));

        OptionDatasetManifest? persisted = JsonSerializer.Deserialize<OptionDatasetManifest>(
            await File.ReadAllTextAsync(Path.Combine(_root, "latest-spy-option-1day-manifest.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(bars.Sha256, persisted?.Sha256);
    }

    [Fact]
    public async Task ExportBarDatasetRefusesSilentGapsForARequestedContract()
    {
        OptionResearchDatasetExporter exporter = Exporter(
            contractPages: [ContractPage(null, Contract("one", CallSymbol))],
            barPages: [BarPage(null, CallSymbol)]);
        OptionDatasetManifest snapshot = await exporter.ExportContractSnapshotAsync(
            _root, "SPY", Expiry, Expiry, "inactive", GeneratedAt, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportBarDatasetAsync(
            _root, snapshot, [CallSymbol, PutSymbol], WindowStart, WindowEnd, "1Day",
            "sha256:spy-daily", GeneratedAt, CancellationToken.None));
    }

    [Fact]
    public async Task ExportBarDatasetRejectsAnOversizedContractUniverse()
    {
        OptionResearchDatasetExporter exporter = Exporter(
            contractPages: [ContractPage(null, Contract("one", CallSymbol))]);
        OptionDatasetManifest snapshot = await exporter.ExportContractSnapshotAsync(
            _root, "SPY", Expiry, Expiry, "inactive", GeneratedAt, CancellationToken.None);
        string[] tooMany = Enumerable.Range(0, OptionResearchDatasetExporter.MaximumSymbolsPerBarRequest + 1)
            .Select(index => $"SPY260904C{(index + 1) * 1000:00000000}")
            .ToArray();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => exporter.ExportBarDatasetAsync(
            _root, snapshot, tooMany, WindowStart, WindowEnd, "1Day",
            "sha256:spy-daily", GeneratedAt, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static OptionResearchDatasetExporter Exporter(
        string[]? contractPages = null,
        string[]? barPages = null)
    {
        var contractClient = new AlpacaOptionContractClient(
            new HttpClient(new PagedHandler(contractPages ?? [ContractPage(null)])), Options());
        var barClient = new AlpacaHistoricalOptionBarClient(
            new HttpClient(new PagedHandler(barPages ?? [BarPage(null)])), Options());
        return new OptionResearchDatasetExporter(contractClient, barClient);
    }

    private static string ContractPage(string? nextPageToken, params string[] contracts)
    {
        string token = nextPageToken is null ? "null" : $"\"{nextPageToken}\"";
        return $$"""{"option_contracts":[{{string.Join(',', contracts)}}],"next_page_token":{{token}}}""";
    }

    private static string Contract(string id, string symbol)
    {
        string strike = (int.Parse(symbol[^8..], CultureInfo.InvariantCulture) / 1000)
            .ToString(CultureInfo.InvariantCulture);
        string type = symbol[^9] == 'C' ? "call" : "put";
        return $$"""
            {"id":"{{id}}","symbol":"{{symbol}}","underlying_symbol":"SPY","root_symbol":"SPY",
             "expiration_date":"2026-09-04","type":"{{type}}","style":"american",
             "strike_price":"{{strike}}","multiplier":"100","size":"100",
             "status":"inactive","tradable":false}
            """;
    }

    private static string BarPage(string? nextPageToken, params string[] symbols)
    {
        const string Bar = "{\"t\":\"2026-08-31T13:30:00Z\",\"o\":1,\"h\":2,\"l\":1,\"c\":1.5," +
            "\"v\":10,\"n\":2,\"vw\":1.4}";
        string token = nextPageToken is null ? "null" : $"\"{nextPageToken}\"";
        string bars = string.Join(',', symbols.Select(symbol => $"\"{symbol}\":[{Bar}]"));
        return "{\"bars\":{" + bars + "},\"next_page_token\":" + token + "}";
    }

    private static AlpacaOptions Options() => new()
    {
        BaseUrl = new Uri("https://paper-api.alpaca.markets"),
        DataBaseUrl = new Uri("https://data.alpaca.markets/"),
        KeyId = "test-key",
        SecretKey = "test-secret"
    };

    private sealed class PagedHandler(params string[] pages) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    pages[Math.Min(_index++, pages.Length - 1)], Encoding.UTF8, "application/json")
            });
    }
}
