using System.Text.Json;
using QuantDesk.Domain.Portfolio;

namespace QuantDesk.Runtime.Persistence;

public sealed class PortfolioSnapshotStore(string path)
{
    private static readonly JsonSerializerOptions Options = QuantDesk.Domain.Serialization.ContractJson.Web;
    private readonly Lock gate = new();

    public void Save(PortfolioSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        lock (gate)
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot, Options));
        }
    }

    public PortfolioSnapshot Load()
    {
        lock (gate)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Portfolio snapshot is unavailable.", path);
            PortfolioSnapshot? snapshot = JsonSerializer.Deserialize<PortfolioSnapshot>(File.ReadAllText(path), Options);
            return snapshot ?? throw new InvalidDataException("Portfolio snapshot is invalid.");
        }
    }
}
