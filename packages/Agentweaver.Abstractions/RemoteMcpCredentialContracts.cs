using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>
/// Request to check that the caller's delegated OAuth credential for one Remote MCP connection
/// is current for the reviewed configuration revision and checksum. The request carries no
/// token material.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RemoteMcpCredentialValidityRequest(
    long ExpectedConfigurationRevision,
    string ExpectedConfigurationSha256);

/// <summary>Reason a delegated Remote MCP credential is not valid for use.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RemoteMcpCredentialDenial>))]
public enum RemoteMcpCredentialDenial
{
    None,
    NotConnected,
    ConsentPending,
    RefreshPending,
    RefreshIndeterminate,
    Disconnected,
    Revoked,
    Expired,
    ConfigurationChanged,
    ConnectionRemoved,
    CredentialChanged,
    CredentialValidityMissing
}

/// <summary>
/// Identity-owned answer to a credential validity request. It reports revisions and the access
/// token expiry, and never token values, secret identifiers, or secret versions.
/// </summary>
public sealed record RemoteMcpCredentialValidity(
    bool Valid,
    RemoteMcpCredentialDenial Denial,
    long ConnectionRevision,
    long CredentialRevision,
    long ConfigurationRevision,
    string ConfigurationSha256,
    DateTimeOffset? ExpiresAt);

/// <summary>Combined admission decision for one remote tool call.</summary>
public sealed record RemoteMcpRemoteUseAdmission(
    bool Allowed,
    RemoteMcpInvocationDenial InvocationDenial,
    RemoteMcpCredentialDenial CredentialDenial);

/// <summary>Pure rules that combine Environment invocation validity with Identity credential validity.</summary>
public static class RemoteMcpRemoteUse
{
    /// <summary>
    /// Allows a remote call only when Environment allows the reviewed connection and, for a
    /// delegated OAuth connection, Identity reports a valid credential at the same configuration
    /// revision and checksum. Both checks must be current; neither one replaces the other.
    /// </summary>
    public static RemoteMcpRemoteUseAdmission Admit(
        RemoteMcpInvocationValidity invocation,
        RemoteMcpCredentialValidity? credential)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!invocation.Allowed)
            return new(false, invocation.Denial, RemoteMcpCredentialDenial.None);
        if (!invocation.CredentialValidityRequired)
            return new(true, RemoteMcpInvocationDenial.None, RemoteMcpCredentialDenial.None);
        if (credential is null)
            return new(false, RemoteMcpInvocationDenial.None, RemoteMcpCredentialDenial.CredentialValidityMissing);
        if (!credential.Valid)
            return new(false, RemoteMcpInvocationDenial.None, credential.Denial);
        var reference = invocation.CurrentReference;
        if (reference is null ||
            reference.ConfigurationRevision != credential.ConfigurationRevision ||
            !string.Equals(reference.ConfigurationSha256, credential.ConfigurationSha256, StringComparison.Ordinal))
            return new(false, RemoteMcpInvocationDenial.None, RemoteMcpCredentialDenial.ConfigurationChanged);
        return new(true, RemoteMcpInvocationDenial.None, RemoteMcpCredentialDenial.None);
    }
}
