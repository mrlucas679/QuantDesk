using System.Globalization;
using System.Text.Json;
using QuantDesk.Domain.Contracts;

namespace QuantDesk.Runtime.Research;

/// <summary>
/// Reads the fitted-model artifacts the Python research plane writes.
///
/// Deliberately strict about shape rather than forgiving. Every field this reader requires exists
/// because a model missing it cannot be refused for the right reason later, and a reader that fills
/// in a plausible default for an absent field is a reader that turns a broken artifact into a
/// working-looking one.
///
/// The reader takes snake_case only, matching the emitted contract. It does not accept the runtime's
/// own casing, because an artifact written by something other than the research plane is an artifact
/// with no provenance, and provenance is most of what this contract is for.
/// </summary>
public static class FittedModelArtifactReader
{
    /// <summary>Reads one artifact, or throws with the field that made it unreadable.</summary>
    public static FittedModelContract Read(string json)
    {
        using JsonDocument document = Parse(json);
        JsonElement root = RequireObject(document.RootElement, "artifact");
        JsonElement producer = RequireObject(RequireProperty(root, "producer"), "producer");
        JsonElement parity = RequireObject(RequireProperty(root, "parity"), "parity");

        return new FittedModelContract(
            RequirePropertyString(root, "artifact_id"),
            RequirePropertyString(root, "model_id"),
            RequirePropertyString(root, "model_family"),
            RequirePropertyString(root, "model_version"),
            RequirePropertyString(root, "feature_schema_hash"),
            RequirePropertyString(root, "dataset_hash"),
            ReadParameters(RequireObject(RequireProperty(root, "parameters"), "parameters")),
            RequireInt(root, "random_seed"),
            RequirePropertyString(root, "evidence_grade"),
            RequirePropertyString(root, "promotion_state"),
            RequirePropertyString(root, "git_commit"),
            RequireTimestamp(root, "created_at"))
        {
            ArtifactSchemaVersion = RequirePropertyString(root, "artifact_schema_version"),
            ProducerLibrary = RequireString(RequireProperty(producer, "library"), "producer.library"),
            ProducerLibraryVersion = RequireString(
                RequireProperty(producer, "library_version"), "producer.library_version"),
            Variant = ReadVariant(RequireObject(RequireProperty(root, "variant"), "variant")),
            Trees = ReadTrees(root),
            ZeroThreshold = ReadZeroThreshold(root),
            ParityKind = ReadParityKind(RequireString(RequireProperty(parity, "kind"), "parity.kind")),
            Tolerance = new ParityTolerance(
                RequireDouble(parity, "absolute_tolerance"),
                RequireDouble(parity, "relative_tolerance")),
            ParityChecks = ReadParityChecks(parity),
            ArtifactHash = RequirePropertyString(root, "artifact_hash"),
            FeatureSemantics = ReadFeatureSemantics(
                RequireObject(RequireProperty(root, "feature_semantics"), "feature_semantics")),
        };
    }

    /// <summary>Reads an artifact from disk, so a caller need not decide the encoding.</summary>
    public static FittedModelContract ReadFile(string path) =>
        Read(File.ReadAllText(path ?? throw new ArgumentNullException(nameof(path))));

    /// <summary>
    /// What the fit says its features mean, which the schema hash does not cover.
    ///
    /// Required rather than optional. An artifact that does not state its units is one whose units
    /// cannot be checked, and the check exists precisely because the failure it catches -- a model
    /// fitted on percent returns fed decimals -- produces confident numbers nothing else questions.
    /// </summary>
    private static FeatureSemanticsContract ReadFeatureSemantics(JsonElement semantics)
    {
        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in
                 RequireObject(RequireProperty(semantics, "units"), "feature_semantics.units")
                     .EnumerateObject())
        {
            units[property.Name] = RequireString(property.Value, $"units.{property.Name}");
        }

        return new FeatureSemanticsContract(
            units,
            RequirePropertyString(semantics, "missing_policy"),
            RequireInt(semantics, "lookback_periods"),
            RequireInt(semantics, "bar_duration_minutes"));
    }

    private static IReadOnlyDictionary<string, double> ReadParameters(JsonElement parameters)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (JsonProperty property in parameters.EnumerateObject())
        {
            if (!property.Value.TryGetDouble(out double value))
                throw new InvalidDataException($"Parameter {property.Name} is not a number.");
            values[property.Name] = value;
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string> ReadVariant(JsonElement variant)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in variant.EnumerateObject())
            values[property.Name] = RequireString(property.Value, $"variant.{property.Name}");

        return values;
    }

    /// <summary>
    /// The trees, when the payload carries them. An artifact without them is not malformed -- only
    /// an ensemble has any.
    /// </summary>
    private static IReadOnlyList<DecisionTree> ReadTrees(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out JsonElement payload)
            || payload.ValueKind is not JsonValueKind.Object
            || !payload.TryGetProperty("trees", out JsonElement trees)
            || trees.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var read = new List<DecisionTree>();
        foreach (JsonElement tree in trees.EnumerateArray())
        {
            if (tree.ValueKind is not JsonValueKind.Array)
                throw new InvalidDataException("Each tree must be an array of nodes.");

            var nodes = new List<TreeNode>();
            foreach (JsonElement node in tree.EnumerateArray())
                nodes.Add(ReadNode(RequireObject(node, "tree node")));

            read.Add(new DecisionTree(nodes));
        }

        return read;
    }

    private static TreeNode ReadNode(JsonElement node) => new(
        RequireInt(node, "split_feature"),
        RequireDouble(node, "threshold"),
        ReadMissingType(RequirePropertyString(node, "missing_type")),
        RequireBoolean(node, "default_left"),
        RequireInt(node, "left"),
        RequireInt(node, "right"),
        RequireDouble(node, "leaf_value"));

    private static TreeMissingType ReadMissingType(string value) => value switch
    {
        "None" => TreeMissingType.None,
        "NaN" => TreeMissingType.NaN,
        "Zero" => TreeMissingType.Zero,

        // Not defaulted. The three conventions route the same input to different leaves, so an
        // unrecognised one silently scoring as "None" is the exact class of failure this reader is
        // supposed to make impossible.
        _ => throw new InvalidDataException($"Unknown missing_type {value}."),
    };

    private static double ReadZeroThreshold(JsonElement root)
    {
        if (!root.TryGetProperty("variant", out JsonElement variant)
            || !variant.TryGetProperty("zero_threshold", out JsonElement threshold)
            || threshold.ValueKind is not JsonValueKind.String)
        {
            return 1.0000000180025095e-35d;
        }

        return double.TryParse(
            threshold.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : throw new InvalidDataException("variant.zero_threshold is not a number.");
    }

    private static ParityKind ReadParityKind(string value) => value switch
    {
        "vector_to_scalar" => ParityKind.VectorToScalar,
        "sequence_to_vector" => ParityKind.SequenceToVector,
        _ => throw new InvalidDataException($"Unknown parity kind {value}."),
    };

    private static IReadOnlyList<ModelParityCheck> ReadParityChecks(JsonElement parity)
    {
        JsonElement cases = RequireArray(parity, "cases");
        var checks = new List<ModelParityCheck>();

        foreach (JsonElement item in cases.EnumerateArray())
        {
            JsonElement one = RequireObject(item, "parity case");
            var observations = new List<IReadOnlyList<double>>();
            foreach (JsonElement row in RequireArray(one, "inputs").EnumerateArray())
            {
                if (row.ValueKind is not JsonValueKind.Array)
                    throw new InvalidDataException("A parity observation must be an array.");

                var values = new List<double>();
                foreach (JsonElement value in row.EnumerateArray())
                {
                    // A missing feature is null on the wire, because JSON has no NaN literal and
                    // this parser rejects the bare token. It becomes NaN here, which is what the
                    // traversal reads as missing.
                    if (value.ValueKind is JsonValueKind.Null)
                    {
                        values.Add(double.NaN);
                        continue;
                    }

                    if (!value.TryGetDouble(out double number))
                        throw new InvalidDataException("A parity feature is not a number or null.");

                    values.Add(number);
                }

                observations.Add(values);
            }

            var expected = new List<double>();
            foreach (JsonElement value in RequireArray(one, "expected").EnumerateArray())
            {
                if (!value.TryGetDouble(out double number))
                    throw new InvalidDataException("A parity expectation is not a number.");
                expected.Add(number);
            }

            checks.Add(new ModelParityCheck(observations, expected));
        }

        return checks;
    }

    private static JsonDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Fitted model artifact is not valid JSON.", exception);
        }
    }

    private static JsonElement RequireObject(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object
            ? element
            : throw new InvalidDataException($"{name} must be an object.");

    private static JsonElement RequireProperty(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new InvalidDataException($"Fitted model artifact is missing {name}.");

    private static JsonElement RequireArray(JsonElement root, string name)
    {
        JsonElement value = RequireProperty(root, name);
        return value.ValueKind is JsonValueKind.Array
            ? value
            : throw new InvalidDataException($"{name} must be an array.");
    }

    private static string RequirePropertyString(JsonElement root, string name) =>
        RequireString(RequireProperty(root, name), name);

    private static string RequireString(JsonElement value, string name) =>
        value.ValueKind is JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException($"{name} must be a non-empty string.");

    private static DateTimeOffset RequireTimestamp(JsonElement root, string name) =>
        DateTimeOffset.TryParse(
            RequirePropertyString(root, name),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset value)
            ? value
            : throw new InvalidDataException($"{name} must be an ISO-8601 timestamp.");

    private static int RequireInt(JsonElement root, string name) =>
        RequireProperty(root, name).TryGetInt32(out int value)
            ? value
            : throw new InvalidDataException($"{name} must be an integer.");

    private static double RequireDouble(JsonElement root, string name) =>
        RequireProperty(root, name).TryGetDouble(out double value)
            ? value
            : throw new InvalidDataException($"{name} must be a number.");

    private static bool RequireBoolean(JsonElement root, string name)
    {
        JsonElement value = RequireProperty(root, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"{name} must be a boolean."),
        };
    }
}
