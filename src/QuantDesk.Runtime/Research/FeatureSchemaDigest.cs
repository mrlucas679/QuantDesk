using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace QuantDesk.Runtime.Research;

/// <summary>
/// Computes the feature-schema hash the research plane computes, from what this runtime feeds.
///
/// Why the runtime derives it rather than reading it
/// -------------------------------------------------
/// The schema hash is the check that a model is fed the feature set it was fitted on, and until now
/// the only place it existed was inside the artifact. Comparing the artifact's hash to the
/// artifact's hash is not a check. It passes for every artifact, including one fitted on a
/// different feature set, which is the failure the hash was introduced to make impossible.
///
/// So the runtime states what it computes -- the names, the dtypes, the warm-up, the sources -- and
/// derives the hash from that. A model whose schema does not match is refused because two
/// independent descriptions disagreed, which is a check that can fail.
///
/// Duplicating the recipe is the price
/// -----------------------------------
/// This reimplements a hash defined in Python, so the two can drift, and a drift would refuse every
/// model rather than accept a wrong one -- the safe direction, and loud. It is pinned by testing
/// against the committed fixtures: if this produces a different string than the artifacts those
/// fixtures carry, the recipe has moved and the test says so before a model does.
///
/// The serialisation has to match exactly, including whitespace. Python's <c>json.dumps</c> with
/// <c>sort_keys=True</c> and default separators writes <c>", "</c> between items and <c>": "</c>
/// after a key, and no trailing newline. That is what is written here.
/// </summary>
public static class FeatureSchemaDigest
{
    /// <summary>
    /// The hash for a feature schema, by the research plane's own recipe.
    /// </summary>
    /// <param name="schemaVersion">Names the feature set and the convention behind it.</param>
    /// <param name="featureNames">In order. The order is most of what the hash protects.</param>
    /// <param name="dtypes">Each feature's type, keyed by name.</param>
    /// <param name="lookbackPeriods">Bars of history the features need before they mean anything.</param>
    /// <param name="sourceRequirements">Which feeds the features are derived from.</param>
    public static string Compute(
        string schemaVersion,
        IReadOnlyList<string> featureNames,
        IReadOnlyDictionary<string, string> dtypes,
        int lookbackPeriods,
        IReadOnlyList<string> sourceRequirements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        ArgumentNullException.ThrowIfNull(featureNames);
        ArgumentNullException.ThrowIfNull(dtypes);
        ArgumentNullException.ThrowIfNull(sourceRequirements);

        var document = new StringBuilder();
        document.Append('{');

        // Keys in sorted order, because sort_keys=True on the other side. Written out one at a time
        // rather than assembled from a dictionary, so the order is visible here and cannot depend on
        // how a .NET dictionary happens to enumerate.
        AppendKey(document, "dtypes");
        AppendObject(document, dtypes);
        document.Append(", ");

        AppendKey(document, "feature_names");
        AppendArray(document, featureNames);
        document.Append(", ");

        AppendKey(document, "lookback_periods");
        document.Append(lookbackPeriods.ToString(CultureInfo.InvariantCulture));
        document.Append(", ");

        // Always empty here. The runtime applies no normalization to these features, and an empty
        // object is what the research plane writes for that -- not an omitted key.
        AppendKey(document, "normalization");
        document.Append("{}");
        document.Append(", ");

        AppendKey(document, "schema_version");
        AppendString(document, schemaVersion);
        document.Append(", ");

        AppendKey(document, "source_requirements");
        AppendArray(document, sourceRequirements);

        document.Append('}');

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(document.ToString())));
    }

    private static void AppendKey(StringBuilder document, string key)
    {
        AppendString(document, key);
        document.Append(": ");
    }

    private static void AppendObject(StringBuilder document, IReadOnlyDictionary<string, string> values)
    {
        document.Append('{');
        bool first = true;

        // Ordinal, matching Python's sort of str keys by code point.
        foreach (KeyValuePair<string, string> entry in values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!first) document.Append(", ");
            first = false;
            AppendKey(document, entry.Key);
            AppendString(document, entry.Value);
        }

        document.Append('}');
    }

    private static void AppendArray(StringBuilder document, IReadOnlyList<string> values)
    {
        document.Append('[');
        for (int index = 0; index < values.Count; index++)
        {
            if (index > 0) document.Append(", ");
            AppendString(document, values[index]);
        }

        document.Append(']');
    }

    /// <summary>
    /// A JSON string as Python writes one, for the characters these schemas contain.
    ///
    /// Feature names, dtypes and source identifiers are ASCII identifiers by convention, so the
    /// escaping needed here is small -- but it is written rather than assumed, and a character
    /// outside that set is refused instead of being emitted differently from the other side.
    /// </summary>
    private static void AppendString(StringBuilder document, string value)
    {
        document.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"': document.Append("\\\""); break;
                case '\\': document.Append("\\\\"); break;
                default:
                    if (character is < ' ' or > '~')
                    {
                        throw new InvalidDataException(
                            $"Feature schema field contains {character}, which this digest does not "
                            + "encode identically to the research plane. Keep schema identifiers ASCII.");
                    }

                    document.Append(character);
                    break;
            }
        }

        document.Append('"');
    }
}
