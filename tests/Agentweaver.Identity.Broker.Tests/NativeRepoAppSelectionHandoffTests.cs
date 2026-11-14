using System.Text;
using System.Text.Json.Nodes;
using Agentweaver.Identity.Broker;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class NativeRepoAppSelectionHandoffTests
{
    [Theory]
    [InlineData("connectionId")]
    [InlineData("repositoryFullName")]
    public void TryReadIntentRejectsNullStringFields(string fieldName)
    {
        JsonObject intent = new()
        {
            ["version"] = 1,
            ["actorId"] = "11111111-1111-4111-8111-111111111111",
            ["tenantId"] = "22222222-2222-4222-8222-222222222222",
            ["projectId"] = "33333333-3333-4333-8333-333333333333",
            ["connectionId"] = "connection_1",
            ["connectionRevision"] = 1,
            ["installationId"] = 22,
            ["repositoryId"] = 33,
            ["repositoryFullName"] = "octo/widget",
            ["operationId"] = "readWorkspaceRepositoryFile",
        };
        intent[fieldName] = null;
        string encoded = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(intent.ToJsonString()));

        Assert.False(NativeRepoAppSelectionHandoff.TryReadIntent(encoded, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void TryReadIntentAcceptsResolvedIntentWithSevenFields()
    {
        Assert.True(NativeRepoAppSelectionHandoff.TryReadIntent(Encode(ResolvedIntent()), out var parsed));

        Assert.NotNull(parsed);
        Assert.Equal(2, parsed.Version);
        Assert.Equal("octo/widget", parsed.RepositoryFullName);
        Assert.Null(parsed.ConnectionRevision);
        Assert.Null(parsed.InstallationId);
        Assert.Null(parsed.RepositoryId);
    }

    [Theory]
    [InlineData("connectionRevision")]
    [InlineData("installationId")]
    [InlineData("repositoryId")]
    public void TryReadIntentRejectsResolvedIntentWithResolvedValue(string fieldName)
    {
        var intent = ResolvedIntent();
        intent[fieldName] = 5;

        Assert.False(NativeRepoAppSelectionHandoff.TryReadIntent(Encode(intent), out var parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void TryReadIntentRejectsSevenFieldsForOtherVersions(int version)
    {
        var intent = ResolvedIntent();
        intent["version"] = version;

        Assert.False(NativeRepoAppSelectionHandoff.TryReadIntent(Encode(intent), out var parsed));
        Assert.Null(parsed);
    }

    private static JsonObject ResolvedIntent() => new()
    {
        ["version"] = 2,
        ["actorId"] = "11111111-1111-4111-8111-111111111111",
        ["tenantId"] = "22222222-2222-4222-8222-222222222222",
        ["projectId"] = "33333333-3333-4333-8333-333333333333",
        ["connectionId"] = "connection_1",
        ["repositoryFullName"] = "octo/widget",
        ["operationId"] = "readWorkspaceRepositoryFile",
    };

    private static string Encode(JsonObject intent) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(intent.ToJsonString()));
}
