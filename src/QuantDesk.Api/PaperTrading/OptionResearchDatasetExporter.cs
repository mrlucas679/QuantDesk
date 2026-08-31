using QuantDesk.Runtime.Persistence;
using System.Security.Cryptography;
using System.Text.Json;
using QuantDesk.Alpaca.MarketData;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Provenance for one immutable option-research export.</summary>
/// <param name="SourceUris">Every paginated request URI that produced the payload, in order.</param>
public sealed record OptionDatasetManifest(
    string DatasetId,
    string Kind,
    string Underlying,
    string Status,
    DateOnly ExpirationStart,
    DateOnly ExpirationEnd,
    string? Timeframe,
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    int RowCount,
    int PageCount,
    IReadOnlyList<string> SourceUris,
    string Sha256,
    string? ContractSnapshotSha256,
    string? UnderlyingDatasetSha256,
    DateTimeOffset GeneratedAt,
    string DataFile);

/// <summary>
/// Publishes immutable, hashed option-contract snapshots and option-bar datasets for the Python research
/// plane. Every export records the exact request that produced it, so a research result can be traced back
/// to a reproducible acquisition rather than to an unlabelled file. Nothing here mutates broker state.
/// </summary>
public sealed class OptionResearchDatasetExporter(
    AlpacaOptionContractClient contractClient,
    AlpacaHistoricalOptionBarClient barClient)
{
    /// <summary>Alpaca accepts at most 100 contract symbols per option-bar request.</summary>
    public const int MaximumSymbolsPerBarRequest = 100;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Discovers a contract universe and writes it as an immutable, hashed snapshot.</summary>
    public async Task<OptionDatasetManifest> ExportContractSnapshotAsync(
        string root,
        string underlying,
        DateOnly expirationStart,
        DateOnly expirationEnd,
        string status,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        OptionContractQuery query = await contractClient.ListAsync(
            underlying, expirationStart, expirationEnd, status, cancellationToken);
        if (query.Contracts.Count is 0)
        {
            throw new InvalidOperationException(
                $"Alpaca returned no {status} {query.Underlying} contracts expiring " +
                $"{expirationStart:yyyy-MM-dd}..{expirationEnd:yyyy-MM-dd}; refusing to publish an empty snapshot.");
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(query.Contracts, JsonOptions);
        string hash = Hash(payload);
        string datasetId = $"{query.Underlying.ToLowerInvariant()}-contracts-{status}-{hash[..16]}";
        var manifest = new OptionDatasetManifest(
            datasetId,
            "option-contract-snapshot",
            query.Underlying,
            query.Status,
            query.ExpirationStart,
            query.ExpirationEnd,
            Timeframe: null,
            WindowStart: null,
            WindowEnd: null,
            query.Contracts.Count,
            query.RequestUris.Count,
            query.RequestUris,
            $"sha256:{hash}",
            ContractSnapshotSha256: null,
            UnderlyingDatasetSha256: null,
            generatedAt,
            $"{datasetId}.json");

        await PublishAsync(root, manifest, payload, $"latest-{query.Underlying.ToLowerInvariant()}-option-contracts.json",
            cancellationToken);
        return manifest;
    }

    /// <summary>
    /// Acquires bars for exactly the contracts named by an existing snapshot and binds the export to that
    /// snapshot's hash and to the underlying equity dataset hash, so the three cannot drift apart silently.
    /// </summary>
    public async Task<OptionDatasetManifest> ExportBarDatasetAsync(
        string root,
        OptionDatasetManifest contractSnapshot,
        IReadOnlyList<string> contractSymbols,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        string timeframe,
        string underlyingDatasetSha256,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(contractSnapshot);
        ArgumentNullException.ThrowIfNull(contractSymbols);
        ArgumentException.ThrowIfNullOrWhiteSpace(underlyingDatasetSha256);
        if (contractSymbols.Count is 0 or > MaximumSymbolsPerBarRequest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractSymbols),
                $"One to {MaximumSymbolsPerBarRequest} contract symbols are required per option-bar dataset.");
        }

        OptionBarQuery query = await barClient.GetBarsAsync(
            contractSymbols, windowStart, windowEnd, timeframe, cancellationToken);
        string[] empty = query.Bars
            .Where(entry => entry.Value.Count is 0)
            .Select(entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (empty.Length is not 0)
        {
            throw new InvalidOperationException(
                $"Alpaca returned no {timeframe} bars for {empty.Length} requested contract(s) " +
                $"({string.Join(", ", empty)}); refusing to publish a dataset with silent gaps.");
        }

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            query.Bars.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal),
            JsonOptions);
        string hash = Hash(payload);
        string datasetId =
            $"{contractSnapshot.Underlying.ToLowerInvariant()}-option-{timeframe.ToLowerInvariant()}-{hash[..16]}";
        var manifest = new OptionDatasetManifest(
            datasetId,
            "option-bar-dataset",
            contractSnapshot.Underlying,
            contractSnapshot.Status,
            contractSnapshot.ExpirationStart,
            contractSnapshot.ExpirationEnd,
            timeframe,
            query.Start,
            query.End,
            query.Bars.Sum(entry => entry.Value.Count),
            query.RequestUris.Count,
            query.RequestUris,
            $"sha256:{hash}",
            contractSnapshot.Sha256,
            underlyingDatasetSha256,
            generatedAt,
            $"{datasetId}.json");

        await PublishAsync(root, manifest, payload,
            $"latest-{contractSnapshot.Underlying.ToLowerInvariant()}-option-{timeframe.ToLowerInvariant()}-manifest.json",
            cancellationToken);
        return manifest;
    }

    private static string Hash(byte[] payload) => Convert.ToHexStringLower(SHA256.HashData(payload));

    private static async Task PublishAsync(
        string root,
        OptionDatasetManifest manifest,
        byte[] payload,
        string latestManifestFile,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        await AtomicFile.WriteAllBytesAsync(Path.Combine(root, manifest.DataFile), payload, cancellationToken);
        byte[] manifestPayload = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        await AtomicFile.WriteAllBytesAsync(Path.Combine(root, $"{manifest.DatasetId}.manifest.json"), manifestPayload, cancellationToken);
        await AtomicFile.WriteAllBytesAsync(Path.Combine(root, latestManifestFile), manifestPayload, cancellationToken);
    }

}
