using QuantDesk.Api.Agents;
using QuantDesk.Domain.Agents;

namespace QuantDesk.Api.Tests;

/// <summary>
/// What the model is actually told to return.
///
/// The contract sent to the provider was <c>nameof(ReviewAgentOutput)</c>, so the prompt read
/// "Return only JSON matching: ReviewAgentOutput" -- a C# type name, to a model that has never seen
/// this repository. The agent plane had therefore never produced a single accepted output, and the
/// failure wore a different face on every model: a reasoning model returned empty content, an
/// instruct model returned well-formed JSON of its own invention that failed IsValid(). Both looked
/// like provider faults.
/// </summary>
public sealed class AgentOutputSchemaTests
{
    [Fact]
    public void TheSchemaNamesEveryFieldTheDeserialiserWillLookFor()
    {
        string schema = AgentOutputSchema.For<ReviewAgentOutput>();

        Assert.Contains("episodeId", schema, StringComparison.Ordinal);
        Assert.Contains("forecastAssessment", schema, StringComparison.Ordinal);
        Assert.Contains("strategyAssessment", schema, StringComparison.Ordinal);
        Assert.Contains("executionAssessment", schema, StringComparison.Ordinal);
        Assert.Contains("riskAssessment", schema, StringComparison.Ordinal);
        Assert.Contains("researchQuestions", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void FieldsAreCamelCasedToMatchTheReader()
    {
        // The response is deserialised with web defaults. Describing the shape in PascalCase would
        // ask the model for a document the reader then rejects, which is the same class of mistake
        // as sending it a type name.
        string schema = AgentOutputSchema.For<ReviewAgentOutput>();

        Assert.DoesNotContain("EpisodeId", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsNoLongerJustTheTypeName()
    {
        // The regression that matters. A name is not a schema, and no model can satisfy one.
        string schema = AgentOutputSchema.For<ReviewAgentOutput>();

        Assert.NotEqual(nameof(ReviewAgentOutput), schema);
        Assert.StartsWith("{", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedRecordIsDescribedInPlaceRatherThanLeftToGuesswork()
    {
        string schema = AgentOutputSchema.For<ReviewAgentOutput>();

        // ForecastAssessment is a list of records, and its fields have to be stated too.
        Assert.Contains("forecastId", schema, StringComparison.Ordinal);
        Assert.Contains("expertId", schema, StringComparison.Ordinal);
        Assert.Contains("supportedByOutcome", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnumIsDescribedByItsPermittedValues()
    {
        // "string" would invite anything; the reader only accepts a defined member.
        string schema = AgentOutputSchema.For<ReviewAgentOutput>();

        Assert.Contains(nameof(QuantDesk.Domain.Forecasts.ForecastType.DirectionalReturn), schema, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectionsAreDescribedAsArrays()
    {
        string schema = AgentOutputSchema.For<ReviewAgentOutput>();

        Assert.Contains("[", schema, StringComparison.Ordinal);
        Assert.Contains(", ...]", schema, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(ReviewAgentOutput))]
    [InlineData(typeof(ResearchHypothesisProposal))]
    [InlineData(typeof(PolicyAgentProposal))]
    public void EveryAgentOutputHasADescribableShape(Type type)
    {
        // All three call sites passed a type name; all three must now pass a shape.
        string schema = (string)typeof(AgentOutputSchema)
            .GetMethod(
                nameof(AgentOutputSchema.For),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .MakeGenericMethod(type)
            .Invoke(null, null)!;

        Assert.StartsWith("{", schema, StringComparison.Ordinal);
        Assert.True(schema.Length > type.Name.Length, $"{type.Name} produced no shape.");
    }
}
