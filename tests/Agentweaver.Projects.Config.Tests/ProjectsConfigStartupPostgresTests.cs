using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigStartupPostgresTests(ProjectsConfigPostgresFixture fixture)
    : IClassFixture<ProjectsConfigPostgresFixture>
{
    [Fact]
    public async Task ProductionHostStartsAndClosesLegacyPathBeforeAuthentication()
    {
        var logs = new ConcurrentQueue<string>();
        var runtimeConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtime = NpgsqlDataSource.Create(runtimeConnection);
        using var factory = new WebApplicationFactory<ProjectsConfigDbContext>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                foreach (var setting in new Dictionary<string, string>
                {
                    ["ConnectionStrings:ProjectsConfig"] = "Host=localhost;Database=test;Username=test",
                    ["ProjectsConfig:Knowledge:BaseAddress"] = "https://knowledge.test/",
                    ["ProjectsConfig:Database:WorkloadIdentity:TenantId"] = Guid.Empty.ToString(),
                    ["ProjectsConfig:Database:WorkloadIdentity:ClientId"] = Guid.Empty.ToString(),
                    ["ProjectsConfig:Database:WorkloadIdentity:TokenFilePath"] =
                        Path.Combine(AppContext.BaseDirectory, "not-read-token"),
                    ["ProjectsConfig:Authentication:Authority"] = "https://identity.test/",
                    ["ProjectsConfig:Authentication:Audience"] = "projects-test",
                })
                    builder.UseSetting(setting.Key, setting.Value);
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<NpgsqlDataSource>();
                    services.AddSingleton(runtime);
                });
                builder.ConfigureLogging(logging =>
                {
                    logging.AddProvider(new CaptureProvider(logs));
                    logging.AddFilter<CaptureProvider>("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information);
                });
            });
        using var client = factory.CreateClient();
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var response = await client.GetAsync("/internal/byok/credential-publications?token=sentinel");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"code":"credential_publication_unavailable"}""", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(logs, message => message.Contains("sentinel", StringComparison.Ordinal));
    }

    private sealed class CaptureProvider(ConcurrentQueue<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(logs);
        public void Dispose() { }
    }

    private sealed class CaptureLogger(ConcurrentQueue<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => logs.Enqueue(formatter(state, exception));
    }
}
