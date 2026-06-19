using Agentweaver.Domain;

namespace Agentweaver.Api.Contracts;

public static class RunOriginExtensions
{
    public static string ToApiString(this RunOrigin origin) => origin switch
    {
        RunOrigin.Interactive   => "interactive",
        RunOrigin.BacklogPickup => "backlog_pickup",
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };

    public static RunOrigin ParseOrigin(string? value) => value switch
    {
        null or "interactive" => RunOrigin.Interactive,
        "backlog_pickup"      => RunOrigin.BacklogPickup,
        _ => throw new ArgumentException($"Unknown run origin: {value}", nameof(value))
    };
}
