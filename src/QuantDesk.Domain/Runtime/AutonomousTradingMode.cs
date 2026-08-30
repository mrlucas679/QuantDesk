namespace QuantDesk.Domain.Runtime;

/// <summary>Declares the evidence authority allowed to drive autonomous paper execution.</summary>
public enum AutonomousTradingMode
{
    Disabled,
    ExperimentalPaper,
    ValidatedPaper
}
