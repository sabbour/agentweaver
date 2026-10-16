namespace Agentweaver.Identity.Broker;

/// <summary>
/// Current publication-grant producer boundary. It compares supplied binding candidates
/// to reject mismatches, then deliberately fails closed because the inputs do not prove a
/// publisher principal or a fresh Environment publication-operation authorization.
/// </summary>
internal sealed class RegistryPublicationGrantProducer
{
    // Neither argument proves current owner state. This slice can reject inconsistent
    // bindings, but must fail closed until Identity has a real publisher principal and
    // a fresh Environment publication-operation proof.
    /// <summary>
    /// Rejects the first unequal binding component. Even equal candidates are denied until
    /// authenticated publisher and Environment operation evidence are available.
    /// </summary>
    public void Validate(
        RegistryPublicationGrantCandidate candidate,
        RegistryPublicationGrantCandidate comparison)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(comparison);

        var mismatch = FindMismatch(candidate, comparison);
        if (mismatch is not null)
            throw new RegistryPublicationGrantDeniedException(
                RegistryPublicationGrantContracts.MismatchCode(mismatch.Value));

        throw new RegistryPublicationGrantDeniedException(
            RegistryPublicationGrantContracts.AuthorityUnavailableCode);
    }

    private static RegistryPublicationGrantBindingField? FindMismatch(
        RegistryPublicationGrantCandidate candidate,
        RegistryPublicationGrantCandidate comparison)
    {
        if (!string.Equals(candidate.ActorId, comparison.ActorId, StringComparison.Ordinal))
            return RegistryPublicationGrantBindingField.Actor;
        if (!string.Equals(candidate.ProjectId, comparison.ProjectId, StringComparison.Ordinal))
            return RegistryPublicationGrantBindingField.Project;
        if (!string.Equals(candidate.RunId, comparison.RunId, StringComparison.Ordinal))
            return RegistryPublicationGrantBindingField.Run;
        if (!string.Equals(candidate.RegistryTarget, comparison.RegistryTarget, StringComparison.Ordinal))
            return RegistryPublicationGrantBindingField.RegistryTarget;
        if (!string.Equals(candidate.Selection, comparison.Selection, StringComparison.Ordinal))
            return RegistryPublicationGrantBindingField.Selection;
        if (candidate.EnvironmentFence != comparison.EnvironmentFence)
            return RegistryPublicationGrantBindingField.Fence;
        if (!string.Equals(candidate.RegistryCredential.Id, comparison.RegistryCredential.Id, StringComparison.Ordinal))
            return RegistryPublicationGrantBindingField.SecretId;
        if (!string.Equals(candidate.RegistryCredential.Version, comparison.RegistryCredential.Version, StringComparison.Ordinal))
            return RegistryPublicationGrantBindingField.SecretVersion;
        return null;
    }
}
