namespace QuantDesk.Domain.Runtime;

public enum DeploymentMode
{
    ReplayOnly,
    ShadowOnly,
    PaperCanary,
    PaperNormal
}
