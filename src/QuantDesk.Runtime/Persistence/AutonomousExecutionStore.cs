using System.Text.Json;
using QuantDesk.Domain.Serialization;

namespace QuantDesk.Runtime.Persistence;

/// <summary>Durable ownership record for one autonomous strategy opportunity.</summary>
public sealed record AutonomousExecutionRecord(
    string ExecutionId,
    string StrategyId,
    string Symbol,
    string EntryClientOrderId,
    string ExitClientOrderId,
    DateTimeOffset ReservedAt)
{
    public string State { get; init; } = "EntryReserved";
    public string? EntryBrokerOrderId { get; init; }
    public string? ExitBrokerOrderId { get; init; }
    public decimal EntryQuantity { get; init; }
    public decimal ExitQuantity { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Atomic, duplicate-fenced persistence for autonomous executions.</summary>
public sealed class AutonomousExecutionStore(string path)
{
    private static readonly Lock Gate = new();

    public bool TryCreate(AutonomousExecutionRecord record)
    {
        lock (Gate)
        {
            List<AutonomousExecutionRecord> records = Load();
            if (records.Any(item => item.ExecutionId == record.ExecutionId ||
                item.EntryClientOrderId == record.EntryClientOrderId ||
                item.ExitClientOrderId == record.ExitClientOrderId)) return false;
            records.Add(record);
            Save(records);
            return true;
        }
    }

    public AutonomousExecutionRecord? Find(string executionId)
    {
        lock (Gate) return Load().SingleOrDefault(item => item.ExecutionId == executionId);
    }

    private List<AutonomousExecutionRecord> Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<List<AutonomousExecutionRecord>>(File.ReadAllBytes(path), ContractJson.Web) ?? []
        : [];

    private void Save(List<AutonomousExecutionRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(records, ContractJson.Web));
        File.Move(temporary, path, true);
    }
}
