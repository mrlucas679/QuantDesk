using System.Text.Json;
using QuantDesk.Domain.Agents;
using QuantDesk.Domain.Serialization;

namespace QuantDesk.Api.Agents;

public sealed record AgentRunRecord(
    string RunId,
    AgentRole Role,
    string EvidenceId,
    string State,
    string? ModelId,
    string? OutputJson,
    string? FailureReason,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed class AgentRunStore(string path)
{
    private readonly Lock _gate = new();

    public IReadOnlyList<AgentRunRecord> ListAll()
    {
        lock (_gate) return [.. Read().Values.OrderByDescending(item => item.StartedAt)];
    }

    public bool Contains(AgentRole role, string evidenceId)
    {
        lock (_gate) return Read().Values.Any(item => item.Role == role && item.EvidenceId == evidenceId && item.State == "complete");
    }

    public void Save(AgentRunRecord record)
    {
        lock (_gate)
        {
            Dictionary<string, AgentRunRecord> records = Read();
            records[record.RunId] = record;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(records, ContractJson.Web));
            File.Move(temporary, path, true);
        }
    }

    private Dictionary<string, AgentRunRecord> Read()
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        return JsonSerializer.Deserialize<Dictionary<string, AgentRunRecord>>(File.ReadAllText(path), ContractJson.Web)
            ?? new(StringComparer.Ordinal);
    }
}
