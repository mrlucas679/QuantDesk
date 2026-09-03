namespace QuantDesk.Domain.Contracts;

/// <summary>
/// What the runtime computes, declared once so a fitted model can be checked against it.
///
/// Why this is not just the schema hash
/// ------------------------------------
/// The hash answers "does the runtime compute the same feature set, in the same order?" It is
/// built from the feature names, their dtypes, the normalization and the lookback -- and it is the
/// right tool for that question, which is why a mismatch is fatal rather than a warning.
///
/// It does not answer "does the runtime compute them in the same units?", because units are not in
/// it. Two models fitted on the same three lag features can hash identically while one was fitted
/// on percent returns and the other on decimals, and the difference is a factor of ten thousand in
/// the intercept. Nothing about the resulting forecast looks wrong. A dot product can be perfectly
/// implemented, verified against parity vectors, and fed the wrong quantity.
///
/// So the runtime states its units, its missing-value policy, and the bar it computes on, and the
/// loader compares. Both checks exist because they catch different failures: the hash catches a
/// different feature set, this catches the same feature set meaning something else.
/// </summary>
/// <param name="FeatureSchemaHash">Hash of the feature set and ordering the runtime feeds.</param>
/// <param name="Units">The unit of each feature, keyed by the schema's names.</param>
/// <param name="MissingPolicy">What the runtime does with an absent feature.</param>
/// <param name="BarDurationMinutes">The bar the runtime computes features on.</param>
public sealed record RuntimeFeatureContract(
    string FeatureSchemaHash,
    IReadOnlyDictionary<string, string> Units,
    string MissingPolicy,
    int BarDurationMinutes)
{
    /// <summary>
    /// A contract that checks only the schema hash.
    ///
    /// For a caller that genuinely has no semantics to declare -- a test exercising a refusal that
    /// happens before semantics are read, most often. Not a shortcut for production: a model whose
    /// units nobody checked is a model whose units nobody knows.
    /// </summary>
    public static RuntimeFeatureContract SchemaOnly(string featureSchemaHash) =>
        new(featureSchemaHash, EmptyUnits, string.Empty, 0);

    private static readonly IReadOnlyDictionary<string, string> EmptyUnits =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether this contract carries semantics to compare at all.</summary>
    public bool DeclaresSemantics => Units.Count > 0 && BarDurationMinutes > 0;
}

/// <summary>
/// What a fitted artifact says its features mean.
/// </summary>
/// <param name="Units">The unit of each feature, keyed by the schema's names.</param>
/// <param name="MissingPolicy">What the fit assumed about an absent feature.</param>
/// <param name="LookbackPeriods">Bars of history the features need before they are meaningful.</param>
/// <param name="BarDurationMinutes">The bar the features were computed on.</param>
public sealed record FeatureSemanticsContract(
    IReadOnlyDictionary<string, string> Units,
    string MissingPolicy,
    int LookbackPeriods,
    int BarDurationMinutes)
{
    /// <summary>
    /// Whether a runtime computing <paramref name="runtime"/> is feeding what this fit expects.
    ///
    /// Every declared feature must be present and identical. An absent unit on either side is a
    /// mismatch rather than a pass: silence is not agreement, and treating it as agreement is how a
    /// units check becomes decoration.
    /// </summary>
    public bool Accepts(RuntimeFeatureContract runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!runtime.DeclaresSemantics) return false;

        if (BarDurationMinutes != runtime.BarDurationMinutes) return false;

        if (!string.Equals(MissingPolicy, runtime.MissingPolicy, StringComparison.Ordinal))
            return false;

        if (Units.Count != runtime.Units.Count) return false;

        foreach (KeyValuePair<string, string> declared in Units)
        {
            if (!runtime.Units.TryGetValue(declared.Key, out string? computed)) return false;
            if (!string.Equals(declared.Value, computed, StringComparison.Ordinal)) return false;
        }

        return true;
    }
}
