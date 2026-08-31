using System.Text.Json;

namespace QuantDesk.Domain.Serialization;

/// <summary>Canonical web-contract serialization settings shared across application boundaries.</summary>
public static class ContractJson
{
    // Treat this instance as immutable. Contract consumers must not add per-call converters,
    // because that would make durable stores and broker readers disagree on the same payload.
    public static JsonSerializerOptions Web { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Canonical contract settings for human-auditable persisted manifests.</summary>
    public static JsonSerializerOptions Indented { get; } = new(Web) { WriteIndented = true };
}
