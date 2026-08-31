namespace QuantDesk.Domain.Runtime;

/// <summary>Declares the evidence authority allowed to drive autonomous paper execution.</summary>
public enum AutonomousTradingMode
{
    Disabled,
    /// <summary>Paper-only forward observation; never implies strategy qualification.</summary>
    ForwardResearch,
    ExperimentalPaper,
    ValidatedPaper
}
