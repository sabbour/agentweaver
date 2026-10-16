namespace Agentweaver.Abstractions;

/// <summary>Identifies whether a run uses hosted Copilot models or a customer-supplied model provider.</summary>
public enum ModelSourceMode
{
    /// <summary>The platform uses its hosted Copilot model connection.</summary>
    HostedCopilot,
    /// <summary>The run uses a customer-configured bring-your-own-key model connection.</summary>
    Byok
}
