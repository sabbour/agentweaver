using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Helpers;

internal static class AutomationTestServices
{
    public static EffectiveModelProviderResolver CreateModelProviderResolver(
        MemoryDbContext db,
        ISecretStore? secrets = null,
        ByokProviderConfigurationService? byokSettings = null)
    {
        secrets ??= new InMemorySecretStore();
        SeedBindingCredentials(db, secrets);
        byokSettings ??= new ByokProviderConfigurationService(secrets);
        return new EffectiveModelProviderResolver(
            new GitHubConnectionsPersistenceStore(db, byokSettings: byokSettings),
            byokSettings,
            secrets);
    }

    public static AutomationInvocationService CreateInvocationService(MemoryDbContext db)
    {
        var persistence = new GitHubConnectionsPersistenceStore(db);
        return new AutomationInvocationService(
            db,
            persistence,
            CreateModelProviderResolver(db));
    }

    public static AutomationActivationSnapshotService CreateActivationService(
        MemoryDbContext db,
        IProjectRoleAssignmentStore roles)
    {
        var persistence = new GitHubConnectionsPersistenceStore(db);
        return new AutomationActivationSnapshotService(
            persistence,
            roles,
            CreateModelProviderResolver(db));
    }

    private static void SeedBindingCredentials(MemoryDbContext db, ISecretStore secrets)
    {
        var references = db.ProjectCopilotBindings.AsNoTracking()
            .Where(binding => binding.Status == GitHubBindingStatus.Active)
            .Select(binding => binding.CredentialReference)
            .Concat(db.PlatformDefaultCopilotBindings.AsNoTracking()
                .Where(binding => binding.Status == GitHubBindingStatus.Active)
                .Select(binding => binding.CredentialReference))
            .Distinct()
            .ToList();
        var credential = JsonSerializer.Serialize(new
        {
            status = "signed-in",
            accessToken = "test-token",
            expiresAt = DateTimeOffset.UtcNow.AddHours(1),
            githubLogin = "test-user",
        });
        foreach (var reference in references)
            secrets.SetSecretAsync(reference, credential).GetAwaiter().GetResult();
    }
}
