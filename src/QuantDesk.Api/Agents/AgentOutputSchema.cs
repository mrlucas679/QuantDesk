using System.Collections;
using System.Reflection;
using System.Text;

namespace QuantDesk.Api.Agents;

/// <summary>
/// The JSON shape an agent is required to return, derived from the type it must deserialise into.
///
/// What this replaces
/// ------------------
/// The contract handed to the model was <c>nameof(ReviewAgentOutput)</c>. The prompt therefore read
/// "Return only JSON matching: ReviewAgentOutput" -- a C# type name, to a model that has never seen
/// this codebase. No model could satisfy that, and the agent plane had never produced a single
/// accepted output in consequence.
///
/// The failure looked different on every model, which is what kept it hidden. A reasoning model
/// returned an empty content with its thinking in a sibling field; an instruct model returned
/// well-formed JSON of its own invention that then failed <c>IsValid()</c> as
/// INVALID_REVIEW_OUTPUT. Both read as provider problems. Neither was.
///
/// Why it is derived rather than written out
/// -----------------------------------------
/// A hand-written schema beside a record is a second definition of the same thing, and the two drift
/// the first time a field is added -- silently, because the prompt is not compiled. Reflecting over
/// the primary constructor keeps the description and the type the same object, so a field that
/// exists is described and a field that is removed stops being asked for.
/// </summary>
internal static class AgentOutputSchema
{
    /// <summary>How deep to describe nested records before stopping.</summary>
    private const int MaximumDepth = 4;

    /// <summary>A compact JSON-shaped description of <typeparamref name="T"/>.</summary>
    internal static string For<T>() => Describe(typeof(T), 0);

    private static string Describe(Type type, int depth)
    {
        var builder = new StringBuilder();
        builder.Append('{');

        ParameterInfo[] parameters = PrimaryConstructorParameters(type);
        for (int index = 0; index < parameters.Length; index++)
        {
            if (index > 0) builder.Append(", ");

            // camelCase, because the deserialiser reads this with web defaults. Describing the
            // fields in PascalCase would ask the model for a document the reader then rejects.
            string name = CamelCase(parameters[index].Name ?? $"field{index}");
            builder.Append('"').Append(name).Append("\": ").Append(DescribeType(parameters[index].ParameterType, depth));
        }

        return builder.Append('}').ToString();
    }

    private static string DescribeType(Type type, int depth)
    {
        Type actual = Nullable.GetUnderlyingType(type) ?? type;

        if (actual == typeof(string)) return "\"string\"";
        if (actual == typeof(bool)) return "true|false";
        if (actual.IsEnum) return "\"" + string.Join("|", Enum.GetNames(actual)) + "\"";
        if (actual == typeof(DateTimeOffset) || actual == typeof(DateTime)) return "\"ISO-8601 timestamp\"";

        if (actual == typeof(long) || actual == typeof(int) || actual == typeof(short))
            return "integer";

        if (actual == typeof(double) || actual == typeof(float) || actual == typeof(decimal))
            return "number";

        if (ElementTypeOf(actual) is { } element)
            return "[" + DescribeType(element, depth + 1) + ", ...]";

        // A nested record, described in place so the model does not have to guess at it either.
        if (depth < MaximumDepth && PrimaryConstructorParameters(actual).Length > 0)
            return Describe(actual, depth + 1);

        return "\"value\"";
    }

    /// <summary>The element type of a collection, or nothing when this is not one.</summary>
    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        if (!typeof(IEnumerable).IsAssignableFrom(type)) return null;

        return type.IsGenericType ? type.GetGenericArguments().FirstOrDefault() : null;
    }

    /// <summary>
    /// The record's primary constructor, taken as the one with the most parameters.
    ///
    /// Records generate a copy constructor alongside the primary one, and picking the first would
    /// describe a single-parameter clone rather than the shape anybody wants.
    /// </summary>
    private static ParameterInfo[] PrimaryConstructorParameters(Type type)
    {
        ConstructorInfo? constructor = type
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(item => item.GetParameters().Length > 0)
            .OrderByDescending(item => item.GetParameters().Length)
            .FirstOrDefault();

        return constructor?.GetParameters() ?? [];
    }

    private static string CamelCase(string name) =>
        name.Length > 0 ? char.ToLowerInvariant(name[0]) + name[1..] : name;
}
