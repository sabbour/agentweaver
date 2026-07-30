namespace Agentweaver.Api.Memory;

public sealed class AuthModeEpochRecord
{
    public string Key { get; set; } = "current";
    public string AuthMode { get; set; } = string.Empty;
    public long Epoch { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
