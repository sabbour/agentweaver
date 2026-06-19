using Agentweaver.Domain;

namespace Agentweaver.Api.Contracts;

public static class BacklogTaskStateExtensions
{
    public static string ToApiString(this BacklogTaskState state) => state switch
    {
        BacklogTaskState.Backlog => "backlog",
        BacklogTaskState.Ready   => "ready",
        BacklogTaskState.Claimed => "claimed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public static BacklogTaskState ParseState(string value) => value switch
    {
        "backlog" => BacklogTaskState.Backlog,
        "ready"   => BacklogTaskState.Ready,
        "claimed" => BacklogTaskState.Claimed,
        _ => throw new ArgumentException($"Unknown backlog task state: {value}", nameof(value))
    };
}
