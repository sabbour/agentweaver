using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Auth;

/// <summary>
/// Covers the multi-provider BYOK settings endpoints: listing, adding, editing (including
/// keep-existing-key-on-blank), removing, and switching the single active provider. GitHub
/// Copilot itself is implicit (active_provider_id: null) and is never part of this list.
/// </summary>
public sealed class ByokProviderSettingsEndpointsTests : IClassFixture<AgentweaverWebApplicationFactory>
{
    private readonly AgentweaverWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ByokProviderSettingsEndpointsTests(AgentweaverWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
    }

    private async Task ResetAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
        foreach (var provider in await settings.ListAsync(CancellationToken.None))
            await settings.RemoveAsync(provider.Id, CancellationToken.None);
    }

    [Fact]
    public async Task List_IsEmpty_AndCopilotIsImplicitlyActive_ByDefault()
    {
        await ResetAsync();

        var response = await _client.GetAsync("/api/admin/byok-providers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("active_provider_id").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("providers").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Add_CreatesProvider_NotActiveByDefault_AndNeverReturnsTheApiKey()
    {
        await ResetAsync();

        var response = await _client.PostAsJsonAsync("/api/admin/byok-providers", new
        {
            name = "My custom endpoint",
            type = "openai",
            base_url = "https://api.example.com/v1",
            model = "my-model",
            api_key = "sk-secret",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("name").GetString().Should().Be("My custom endpoint");
        doc.RootElement.GetProperty("has_api_key").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("is_active").GetBoolean().Should().BeFalse();
        doc.RootElement.TryGetProperty("api_key", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Add_CustomEndpoint_AllowsBlankApiKey()
    {
        await ResetAsync();

        var response = await _client.PostAsJsonAsync("/api/admin/byok-providers", new
        {
            name = "Unauthenticated vLLM",
            type = "openai",
            base_url = "https://vllm.example.com/v1",
            model = "llama",
            api_key = (string?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("has_api_key").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Add_Azure_RequiresApiKey_AndBareHostBaseUrl()
    {
        await ResetAsync();

        var noKey = await _client.PostAsJsonAsync("/api/admin/byok-providers", new
        {
            name = "Azure prod",
            type = "azure",
            base_url = "https://my-resource.openai.azure.com",
            model = "gpt-4o",
            api_key = (string?)null,
        });
        noKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var pathInUrl = await _client.PostAsJsonAsync("/api/admin/byok-providers", new
        {
            name = "Azure prod",
            type = "azure",
            base_url = "https://my-resource.openai.azure.com/openai",
            model = "gpt-4o",
            api_key = "azure-key",
        });
        pathInUrl.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetActive_MakesProviderActive_AndOnlyOneCanBeActiveAtATime()
    {
        await ResetAsync();

        var first = await CreateAsync("Provider A");
        var second = await CreateAsync("Provider B");

        var activate = await _client.PostAsync($"/api/admin/byok-providers/{first}/activate", null);
        activate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterFirst = await GetListAsync();
        afterFirst.RootElement.GetProperty("active_provider_id").GetString().Should().Be(first);

        var activateSecond = await _client.PostAsync($"/api/admin/byok-providers/{second}/activate", null);
        activateSecond.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterSecond = await GetListAsync();
        afterSecond.RootElement.GetProperty("active_provider_id").GetString().Should().Be(second);
        var providers = afterSecond.RootElement.GetProperty("providers").EnumerateArray().ToList();
        providers.Single(p => p.GetProperty("id").GetString() == second).GetProperty("is_active").GetBoolean().Should().BeTrue();
        providers.Single(p => p.GetProperty("id").GetString() == first).GetProperty("is_active").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Deactivate_SwitchesBackToGitHubCopilot_WithoutDeletingTheProvider()
    {
        await ResetAsync();

        var id = await CreateAsync("Provider A");
        await _client.PostAsync($"/api/admin/byok-providers/{id}/activate", null);

        var deactivate = await _client.PostAsync("/api/admin/byok-providers/deactivate", null);
        deactivate.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDeactivate = await GetListAsync();
        afterDeactivate.RootElement.GetProperty("active_provider_id").ValueKind.Should().Be(JsonValueKind.Null);
        afterDeactivate.RootElement.GetProperty("providers").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Update_WithBlankApiKey_KeepsThePreviouslySavedKey()
    {
        await ResetAsync();
        var id = await CreateAsync("Provider A");

        var update = await _client.PutAsJsonAsync($"/api/admin/byok-providers/{id}", new
        {
            name = "Provider A renamed",
            type = "openai",
            base_url = "https://api.example.com/v1",
            model = "renamed-model",
            api_key = (string?)null,
        });
        update.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await update.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("name").GetString().Should().Be("Provider A renamed");
        doc.RootElement.GetProperty("model").GetString().Should().Be("renamed-model");
        doc.RootElement.GetProperty("has_api_key").GetBoolean().Should().BeTrue("blank api_key on update must keep the existing saved key");
    }

    [Fact]
    public async Task Remove_DeletesProvider_AndClearsActiveIfItWasActive()
    {
        await ResetAsync();
        var id = await CreateAsync("Provider A");
        await _client.PostAsync($"/api/admin/byok-providers/{id}/activate", null);

        var remove = await _client.DeleteAsync($"/api/admin/byok-providers/{id}");
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterRemove = await GetListAsync();
        afterRemove.RootElement.GetProperty("active_provider_id").ValueKind.Should().Be(JsonValueKind.Null);
        afterRemove.RootElement.GetProperty("providers").GetArrayLength().Should().Be(0);
    }

    private async Task<string> CreateAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/admin/byok-providers", new
        {
            name,
            type = "openai",
            base_url = "https://api.example.com/v1",
            model = "gpt-4o",
            api_key = "sk-secret",
        });
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private async Task<JsonDocument> GetListAsync()
    {
        var response = await _client.GetAsync("/api/admin/byok-providers");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
