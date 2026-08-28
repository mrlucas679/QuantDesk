using QuantDesk.Domain.Capabilities;

namespace QuantDesk.Alpaca.Capabilities;

public interface IAlpacaCapabilityProbe
{
    Task<CapabilityReport> ProbeAsync(CancellationToken cancellationToken);
}

