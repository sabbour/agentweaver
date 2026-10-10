using Agentweaver.Abstractions;
using Agentweaver.Identity.Broker;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RegistryPublicationGrantProducerTests
{
    [Theory]
    [InlineData("actor", "registry_publication_actor_mismatch")]
    [InlineData("project", "registry_publication_project_mismatch")]
    [InlineData("run", "registry_publication_run_mismatch")]
    [InlineData("target", "registry_publication_target_mismatch")]
    [InlineData("selection", "registry_publication_selection_mismatch")]
    [InlineData("fence", "registry_publication_fence_mismatch")]
    [InlineData("secret-id", "registry_publication_secret_id_mismatch")]
    [InlineData("secret-version", "registry_publication_secret_version_mismatch")]
    public void BindingMismatchesAreExplicitlyDenied(string field, string expectedCode)
    {
        var candidate = CreateCandidate();
        var comparison = field switch
        {
            "actor" => CreateCandidate(actorId: "actor-2"),
            "project" => CreateCandidate(projectId: "project-2"),
            "run" => CreateCandidate(runId: "run-2"),
            "target" => CreateCandidate(registryTarget: "https://other.example/"),
            "selection" => CreateCandidate(selection: "selection-2"),
            "fence" => CreateCandidate(environmentFence: 2),
            "secret-id" => CreateCandidate(secretId: "registry-secret-2"),
            "secret-version" => CreateCandidate(secretVersion: "version-2"),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var denied = Assert.Throws<RegistryPublicationGrantDeniedException>(() =>
            new RegistryPublicationGrantProducer().Validate(candidate, comparison));

        Assert.Equal(expectedCode, denied.Code);
    }

    [Fact]
    public void MatchingCallerSuppliedBindingsStillRequireRealOwnerAuthority()
    {
        var candidate = CreateCandidate();

        var denied = Assert.Throws<RegistryPublicationGrantDeniedException>(() =>
            new RegistryPublicationGrantProducer().Validate(candidate, CreateCandidate()));

        Assert.Equal(RegistryPublicationGrantContracts.AuthorityUnavailableCode, denied.Code);
        Assert.Equal("registry-publication", RegistryPublicationGrantContracts.Purpose);
    }

    [Fact]
    public void IncompleteBindingsAreRejectedBeforeComparison()
    {
        Assert.Throws<ArgumentException>(() => new RegistryPublicationGrantCandidate(
            " ", "project-1", "run-1", "https://registry.example/", "selection-1", 1,
            new SecretRef("registry-secret", "version-1")));
        Assert.Throws<ArgumentException>(() => new RegistryPublicationGrantCandidate(
            "actor-1", "project-1", "run-1", "https://registry.example/", " ", 1,
            new SecretRef("registry-secret", "version-1")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegistryPublicationGrantCandidate(
            "actor-1", "project-1", "run-1", "https://registry.example/", "selection-1", 0,
            new SecretRef("registry-secret", "version-1")));
    }

    private static RegistryPublicationGrantCandidate CreateCandidate(
        string actorId = "actor-1",
        string projectId = "project-1",
        string runId = "run-1",
        string registryTarget = "https://registry.example/",
        string selection = "selection-1",
        long environmentFence = 1,
        string secretId = "registry-secret",
        string secretVersion = "version-1") =>
        new(
            actorId,
            projectId,
            runId,
            registryTarget,
            selection,
            environmentFence,
            new SecretRef(secretId, secretVersion));
}
