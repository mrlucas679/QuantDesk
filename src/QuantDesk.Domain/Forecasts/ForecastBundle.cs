namespace QuantDesk.Domain.Forecasts;

public sealed record ForecastBundle(
    int InstrumentSlot,
    long SourceStateVersion,
    DirectionalForecast? Direction);

