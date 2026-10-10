using System.IO.Compression;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RuntimeSkillContentTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ProjectionAcceptsPinnedTextAndResourceBytes()
    {
        var projection = Projection(RuntimeCopilotSessionTests.Registration());

        SkillRuntimeContentContract.ValidateProjection(projection);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("nested//empty.txt")]
    public void ProjectionRejectsUnsafeResourcePaths(string path)
    {
        var projection = Projection(RuntimeCopilotSessionTests.Registration(), Skill(path));

        Assert.Throws<ArgumentException>(() => SkillRuntimeContentContract.ValidateProjection(projection));
    }

    [Fact]
    public void ProjectionRejectsResourceOrContentDigestMismatch()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var skill = Skill();
        var badResource = skill with
        {
            Resources = [skill.Resources[0] with { Sha256 = new string('0', 64) }]
        };
        var badContent = skill with { ContentDigest = new string('0', 64) };

        Assert.Throws<ArgumentException>(() => SkillRuntimeContentContract.ValidateProjection(
            Projection(registration, badResource)));
        Assert.Throws<ArgumentException>(() => SkillRuntimeContentContract.ValidateProjection(
            Projection(registration, badContent)));
    }

    [Fact]
    public async Task RuntimeClientReadsOnlyTheBoundRegistrationSkillsRoute()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var projection = Projection(registration);
        var handler = new OwnerHandler(projection);
        using var http = new HttpClient(handler);
        var bearer = new SecretCredential("runtime-actor-token", DateTimeOffset.UtcNow.AddMinutes(2));
        var actor = new RuntimeActorAuthorization(bearer, registration.Binding.TenantId);
        var client = new RuntimeSkillContentHttpClient(http, new Uri("https://orchestrator.test/"), actor);

        var result = await client.ReadAsync(registration, CancellationToken.None);

        Assert.Equal(projection.RuntimeInstanceId, result.RuntimeInstanceId);
        Assert.Equal(projection.AgentId, result.AgentId);
        Assert.Equal(projection.Skills[0].ContentDigest, result.Skills[0].ContentDigest);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal($"/internal/runtime/registrations/{registration.RuntimeInstanceId:D}/skills", handler.Path);
        Assert.Equal("Bearer runtime-actor-token", handler.Authorization);
        Assert.Equal(registration.Binding.TenantId, handler.Tenant);
        Assert.True(handler.NoStore);
        Assert.True(handler.NoCache);
        Assert.False(handler.HasBody);
    }

    [Fact]
    public async Task RuntimeClientAcceptsAnExplicitEmptyAcceptedSkillSet()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var bearer = new SecretCredential("runtime-actor-token", DateTimeOffset.UtcNow.AddMinutes(2));
        var client = Client(registration, new RuntimeActorAuthorization(bearer, registration.Binding.TenantId));

        var result = await client.ReadAsync(registration, CancellationToken.None);

        Assert.Empty(result.Skills);
    }

    [Fact]
    public async Task RuntimeClientRejectsContentBoundToAnotherRegistration()
    {
        var registration = RuntimeCopilotSessionTests.Registration();
        var projection = Projection(registration) with { AgentId = "foreign-agent" };
        var handler = new OwnerHandler(projection);
        using var http = new HttpClient(handler);
        var bearer = new SecretCredential("runtime-actor-token", DateTimeOffset.UtcNow.AddMinutes(2));
        var client = new RuntimeSkillContentHttpClient(http, new Uri("https://orchestrator.test/"),
            new RuntimeActorAuthorization(bearer, registration.Binding.TenantId));

        var error = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            client.ReadAsync(registration, CancellationToken.None));

        Assert.Equal("runtime_skill_content_binding_mismatch", error.Code);
    }

    [Fact]
    public async Task NativeSkillProviderExposesOnlyAcceptedInstructionsAndResourceLocation()
    {
        var projection = Projection(RuntimeCopilotSessionTests.Registration());
        var provider = new RuntimeAcceptedSkillProvider(projection);

        var descriptor = Assert.Single(await provider.ListSkillsAsync(CancellationToken.None));
        var markdown = await provider.ReadSkillAsync(descriptor.Name, CancellationToken.None);

        Assert.Equal("Pinned skill description.", descriptor.Description);
        Assert.StartsWith("Exact pinned instructions.", markdown);
        Assert.Contains("guide.txt", markdown);
        Assert.Contains("/workspace/.agentweaver/skills/", markdown);
        Assert.Null(await provider.ReadSkillAsync("unassigned-skill", CancellationToken.None));
    }

    [Fact]
    public async Task NativeSessionEnablesOnlyTheAcceptedSkillProvider()
    {
        await using var sdk = new ControlledCopilotRuntime();
        var registration = RuntimeCopilotSessionTests.Registration();
        var projection = Projection(registration);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await using var session = await RuntimeCopilotSessionTests.Factory(sdk).CreateAsync(
            registration,
            registration.Binding.ModelSelectionReference!,
            RuntimeCopilotSessionTests.SdkCredential(),
            _ => Task.CompletedTask,
            timeout.Token,
            projection);

        var create = Assert.Single(sdk.Requests, request => request.Method == "session.create");
        Assert.True(create.Parameters.GetProperty("enableSkills").GetBoolean());
    }

    [Fact]
    public void NativeSessionFilesContainExactPinnedResourceBytes()
    {
        var projection = Projection(RuntimeCopilotSessionTests.Registration());
        var skill = Assert.Single(projection.Skills);
        var runtimeName = RuntimeAcceptedSkillProvider.RuntimeName(skill.SkillId);
        var expectedPath = $"workspace/{RuntimeAcceptedSkillProvider.ResourceDirectory(runtimeName)}/guide.txt";
        var files = new RuntimeNativeSessionFiles();
        files.InstallSkillResources(projection);

        using var archive = new ZipArchive(new MemoryStream(files.Capture()), ZipArchiveMode.Read);
        var resource = Assert.Single(archive.Entries, entry => entry.FullName == expectedPath);
        using var reader = new StreamReader(resource.Open(), Encoding.UTF8);

        Assert.Equal("Exact resource bytes.", reader.ReadToEnd());
    }

    internal static SkillRuntimeContentProjectionV1 EmptyProjection(RuntimeRegistration registration) =>
        Projection(registration, includeSkill: false);

    private static SkillRuntimeContentProjectionV1 Projection(
        RuntimeRegistration registration, SkillRuntimeContentV1? skill = null, bool includeSkill = true)
    {
        var binding = registration.Binding;
        return new SkillRuntimeContentProjectionV1(
            SkillRuntimeContentContract.CurrentVersion,
            registration.RuntimeInstanceId,
            registration.Revision,
            binding.ProjectConfigurationRevision,
            binding.ExecutionFence,
            binding.TenantId,
            binding.ProjectId,
            binding.RunId,
            binding.SessionId,
            binding.AgentId,
            binding.AcceptedSelectionHash,
            includeSkill ? [skill ?? Skill()] : []);
    }

    internal static RuntimeSkillContentHttpClient Client(
        RuntimeRegistration registration, RuntimeActorAuthorization actor) =>
        new(new HttpClient(new OwnerHandler(EmptyProjection(registration))),
            new Uri("https://orchestrator.test/"), actor);

    private static SkillRuntimeContentV1 Skill(string path = "guide.txt")
    {
        var bytes = ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("Exact resource bytes."));
        ImmutableArray<SkillRuntimeContentResourceV1> resources =
            [new(path, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan())))];
        const string name = "PinnedSkill";
        const string description = "Pinned skill description.";
        const string instructions = "Exact pinned instructions.";
        return new("skill-1", 1, name, description, instructions,
            SkillRuntimeContentContract.ComputeContentDigest(name, description, instructions, resources), resources);
    }

    private sealed class OwnerHandler(SkillRuntimeContentProjectionV1 projection) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? Tenant { get; private set; }
        public bool NoStore { get; private set; }
        public bool NoCache { get; private set; }
        public bool HasBody { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            Tenant = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
            NoStore = request.Headers.CacheControl?.NoStore == true;
            NoCache = request.Headers.CacheControl?.NoCache == true;
            HasBody = request.Content is not null;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = JsonContent.Create(projection, options: JsonOptions)
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }
}
