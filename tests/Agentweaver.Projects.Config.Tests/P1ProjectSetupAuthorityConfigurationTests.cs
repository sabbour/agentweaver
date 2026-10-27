using Microsoft.Extensions.Configuration;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class P1ProjectSetupAuthorityConfigurationTests
{
    private const string RuntimeConnection =
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_runtime;SSL Mode=VerifyFull";
    private const string AuthorityConnection =
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_authority;SSL Mode=VerifyFull";
    private const string RuntimeClientId = "00000000-0000-0000-0000-000000000001";
    private const string AuthorityClientId = "00000000-0000-0000-0000-000000000002";
    private const string MigrationClientId = "00000000-0000-0000-0000-000000000003";

    [Fact]
    public void SeparateAuthorityCapabilityIsAccepted()
    {
        var authority = P1ProjectSetupAuthorityConfiguration.ReadOptional(
            Build(ConfiguredValues()), RuntimeConnection);

        Assert.NotNull(authority);
        Assert.Equal(AuthorityConnection, authority.ConnectionString);
        Assert.Equal("approved-subject", authority.Approval.ApprovedSubject);
        Assert.Equal("approved-tenant", authority.Approval.ApprovedTenantId);
    }

    [Fact]
    public void MissingAuthorityCapabilityIsOptional()
    {
        var configuration = CreateConfiguration(includeAuthority: false);

        Assert.Null(P1ProjectSetupAuthorityConfiguration.ReadOptional(
            configuration, RuntimeConnection));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PartialAuthorityConfigurationIsRejected(
        bool includeConnection,
        bool includeIdentity)
    {
        var configuration = CreateConfiguration(
            includeAuthority: false,
            includeConnection,
            includeIdentity);

        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(configuration, RuntimeConnection));
    }

    [Theory]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=other.db.example;Port=5432;Database=projects;Username=projects_authority;SSL Mode=VerifyFull")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=other;Username=projects_authority;SSL Mode=VerifyFull")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_runtime;SSL Mode=VerifyFull")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=projects;Username=postgres;SSL Mode=VerifyFull")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_migration;SSL Mode=VerifyFull")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_authority;Password=secret;SSL Mode=VerifyFull")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_authority;SSL Mode=Require")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_authority;SSL Mode=Disable")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example,other.db.example;Port=5432;Database=projects;Username=projects_authority;SSL Mode=VerifyFull")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_authority;SSL Mode=VerifyFull;Passfile=credentials.conf")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_authority;SSL Mode=VerifyFull;Service=foreign-service")]
    [InlineData(
        "ConnectionStrings:ProjectsConfigAuthority",
        "Host=projects.db.example;Port=5432;Database=projects;Username=projects_authority;SSL Mode=VerifyFull;Application Name=setup")]
    public void UnsafeAuthorityConnectionIsRejected(string key, string value)
    {
        var values = ConfiguredValues();
        values[key] = value;
        var configuration = Build(values);

        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(configuration, RuntimeConnection));
    }

    [Fact]
    public void RuntimeAuthorityIdentityReuseIsRejected()
    {
        var values = ConfiguredValues();
        values["ProjectsConfig:AuthorityDatabase:WorkloadIdentity:ClientId"] = RuntimeClientId;

        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(values), RuntimeConnection));
    }

    [Fact]
    public void AuthorityIdentityMustUseTheAdmittedRuntimeTenantAndTokenFile()
    {
        var foreignTenant = ConfiguredValues();
        foreignTenant["ProjectsConfig:AuthorityDatabase:WorkloadIdentity:TenantId"] =
            "00000000-0000-0000-0000-000000000011";
        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(foreignTenant), RuntimeConnection));

        var foreignToken = ConfiguredValues();
        foreignToken["ProjectsConfig:AuthorityDatabase:WorkloadIdentity:TokenFilePath"] =
            Path.GetFullPath("other-authority-token.jwt");
        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(foreignToken), RuntimeConnection));
    }

    [Theory]
    [InlineData("ProjectsConfig:Database:WorkloadIdentity:TenantId")]
    [InlineData("ProjectsConfig:Database:WorkloadIdentity:ClientId")]
    [InlineData("ProjectsConfig:AuthorityDatabase:WorkloadIdentity:TenantId")]
    [InlineData("ProjectsConfig:AuthorityDatabase:WorkloadIdentity:ClientId")]
    public void EmptyRuntimeOrAuthorityWorkloadIdentityGuidsAreRejected(string key)
    {
        var values = ConfiguredValues();
        values[key] = "00000000-0000-0000-0000-000000000000";

        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(values), RuntimeConnection));
    }

    [Fact]
    public void MigrationRoleAndIdentityReuseAreRejected()
    {
        var sameRole = ConfiguredValues();
        AddMigration(sameRole, "projects_authority", MigrationClientId);
        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(sameRole), RuntimeConnection));

        var sameIdentity = ConfiguredValues();
        AddMigration(sameIdentity, "projects_migration", AuthorityClientId);
        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(sameIdentity), RuntimeConnection));

        var emptyIdentity = ConfiguredValues();
        AddMigration(emptyIdentity, "projects_migration", "00000000-0000-0000-0000-000000000000");
        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(emptyIdentity), RuntimeConnection));

        var runtimeIdentity = ConfiguredValues();
        AddMigration(runtimeIdentity, "projects_migration", RuntimeClientId);
        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(runtimeIdentity), RuntimeConnection));
    }

    [Fact]
    public void PartialMigrationConfigurationIsRejected()
    {
        var values = ConfiguredValues();
        values["ConnectionStrings:ProjectsConfigMigration"] =
            "Host=projects.db.example;Port=5432;Database=projects;Username=projects_migration;SSL Mode=VerifyFull";

        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(values), RuntimeConnection));
    }

    [Fact]
    public void AuthorityIssuerMustMatchExistingAuthenticationAuthority()
    {
        var values = ConfiguredValues();
        values["ProjectsConfig:PrivilegedSetup:ApprovedIssuer"] = "https://other.example/";

        Assert.Throws<InvalidOperationException>(() =>
            P1ProjectSetupAuthorityConfiguration.ReadOptional(Build(values), RuntimeConnection));
    }

    private static IConfiguration CreateConfiguration(
        bool includeAuthority,
        bool includeConnection = false,
        bool includeIdentity = false)
    {
        var values = BaseValues();
        if (includeAuthority || includeConnection)
            values["ConnectionStrings:ProjectsConfigAuthority"] = AuthorityConnection;
        if (includeAuthority || includeIdentity)
            AddAuthorityIdentity(values);
        return Build(values);
    }

    private static Dictionary<string, string?> ConfiguredValues()
    {
        var values = BaseValues();
        values["ConnectionStrings:ProjectsConfigAuthority"] = AuthorityConnection;
        AddAuthorityIdentity(values);
        return values;
    }

    private static Dictionary<string, string?> BaseValues() => new(StringComparer.Ordinal)
    {
        ["ProjectsConfig:Database:WorkloadIdentity:TenantId"] =
            "00000000-0000-0000-0000-000000000010",
        ["ProjectsConfig:Database:WorkloadIdentity:ClientId"] = RuntimeClientId,
        ["ProjectsConfig:Database:WorkloadIdentity:TokenFilePath"] =
            Path.GetFullPath("runtime-token.jwt"),
        ["ProjectsConfig:Authentication:Authority"] = "https://broker.example/",
        ["ProjectsConfig:Authentication:Audience"] = "projects-api",
        ["ProjectsConfig:PrivilegedSetup:ApprovedIssuer"] = "https://broker.example/",
        ["ProjectsConfig:PrivilegedSetup:ApprovedSubject"] = "approved-subject",
        ["ProjectsConfig:PrivilegedSetup:ApprovedTenantId"] = "approved-tenant",
    };

    private static void AddAuthorityIdentity(IDictionary<string, string?> values)
    {
        values["ProjectsConfig:AuthorityDatabase:WorkloadIdentity:TenantId"] =
            "00000000-0000-0000-0000-000000000010";
        values["ProjectsConfig:AuthorityDatabase:WorkloadIdentity:ClientId"] = AuthorityClientId;
        values["ProjectsConfig:AuthorityDatabase:WorkloadIdentity:TokenFilePath"] =
            Path.GetFullPath("runtime-token.jwt");
    }

    private static void AddMigration(
        IDictionary<string, string?> values,
        string role,
        string clientId)
    {
        values["ConnectionStrings:ProjectsConfigMigration"] =
            $"Host=projects.db.example;Port=5432;Database=projects;Username={role};SSL Mode=VerifyFull";
        values["ProjectsConfig:Migration:WorkloadIdentity:TenantId"] =
            "00000000-0000-0000-0000-000000000010";
        values["ProjectsConfig:Migration:WorkloadIdentity:ClientId"] = clientId;
        values["ProjectsConfig:Migration:WorkloadIdentity:TokenFilePath"] =
            Path.GetFullPath("migration-token.jwt");
    }

    private static IConfiguration Build(IDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
