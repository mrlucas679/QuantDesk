using System.Text.Json;
using QuantDesk.Domain.Portfolio;

namespace QuantDesk.Runtime.Persistence;

public sealed class PortfolioSnapshotStore(string path)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly Lock gate = new();

    public void Save(PortfolioSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        lock (gate)
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, Options));
            File.Move(temporary, path, true);
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
