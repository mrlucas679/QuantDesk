namespace QuantDesk.Domain.Forecasts;

public sealed record ForecastBundle(
    int InstrumentSlot,
    long SourceStateVersion,
    DirectionalForecast? Direction,
    VolatilityForecast? Volatility = null,
    RegimeForecast? Regime = null,
    RelativeValueForecast? RelativeValue = null,
    JumpRiskForecast? JumpRisk = null,
    LiquidityCostForecast? LiquidityCost = null,
    OptionSurfaceForecast? OptionSurface = null,
    MicrostructureForecast? Microstructure = null)
{
    /// <summary>Returns the metadata for every family actually present in this bundle.</summary>
    public IReadOnlyList<ForecastMetadata> PublishedFamilies
    {
        get
        {
            List<ForecastMetadata> values = [];
            Add(Direction?.Metadata);
            Add(Volatility?.Metadata);
            Add(Regime?.Metadata);
            Add(RelativeValue?.Metadata);
            Add(JumpRisk?.Metadata);
            Add(LiquidityCost?.Metadata);
            Add(OptionSurface?.Metadata);
            Add(Microstructure?.Metadata);
            return values;

            void Add(ForecastMetadata? metadata)
            {
                if (metadata is { } value) values.Add(value);
            }
        }
    }
}
