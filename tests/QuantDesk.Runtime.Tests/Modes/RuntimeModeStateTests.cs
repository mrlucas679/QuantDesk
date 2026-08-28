using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Modes;

namespace QuantDesk.Runtime.Tests.Modes;

public sealed class RuntimeModeStateTests
{
    [Fact]
    public void NewRuntime_IsBooting()
    {
        var state = new RuntimeModeState();

        Assert.Equal(SystemMode.Booting, state.Snapshot().Mode);
    }

    [Fact]
    public void Transition_RecordsModeAndReason()
    {
        var state = new RuntimeModeState();
        state.Transition(SystemMode.Preflight, "paper account verification");

        var snapshot = state.Snapshot();
        Assert.Equal(SystemMode.Preflight, snapshot.Mode);
        Assert.Equal("paper account verification", snapshot.Reason);
    }
}
