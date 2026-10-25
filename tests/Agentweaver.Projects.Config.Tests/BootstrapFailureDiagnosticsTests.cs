using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class BootstrapFailureDiagnosticsTests
{
    [Fact]
    public void IncludesValidatedPostgresMetadataWithoutServerMessage()
    {
        var postgres = new PostgresException(
            "password=\"secret with spaces\"; https://user:pass@example.test/path\nBearer multiline-secret",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            tableName: "project_role_assignments",
            constraintName: "ux_platform_admin");
        var exception = new DbUpdateException("Save failed.", postgres);

        var diagnostic = BootstrapFailureDiagnostics.Format(exception);

        Assert.Contains("ExceptionType=DbUpdateException", diagnostic, StringComparison.Ordinal);
        Assert.Contains("InnerExceptionType=PostgresException", diagnostic, StringComparison.Ordinal);
        Assert.Contains("SqlState=23505", diagnostic, StringComparison.Ordinal);
        Assert.Contains("ConstraintName=ux_platform_admin", diagnostic, StringComparison.Ordinal);
        Assert.Contains("TableName=project_role_assignments", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("MessageText", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret with spaces", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass@", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("multiline-secret", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void OmitsUntrustedConfigurationMessages()
    {
        var diagnostics = new[]
        {
            BootstrapFailureDiagnostics.Format(
                new ArgumentException("password=\"secret with spaces\"")),
            BootstrapFailureDiagnostics.Format(
                new InvalidOperationException("authority=https://user:pass@example.test/path")),
            BootstrapFailureDiagnostics.Format(
                new InvalidOperationException("token=first-line\nsecond-line-secret")),
            BootstrapFailureDiagnostics.Format(
                new InvalidOperationException(
                    "issuer=https://agentweaver.synthetic.example/; subject=aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee; tenant=11111111-2222-4333-8444-555555555555")),
        };
        var combined = string.Join(';', diagnostics);

        Assert.Contains("ExceptionType=ArgumentException; Category=invalid-bootstrap-configuration",
            diagnostics[0], StringComparison.Ordinal);
        Assert.Contains("ExceptionType=InvalidOperationException; Category=invalid-bootstrap-configuration",
            diagnostics[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Message=", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("secret with spaces", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pass@", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("second-line-secret", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("agentweaver.synthetic.example", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111-2222-4333-8444-555555555555", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacesInvalidPostgresMetadataWithUnknown()
    {
        var postgres = new PostgresException(
            "unsafe metadata must not be copied from arbitrary server text",
            "ERROR",
            "ERROR",
            "23505\n",
            tableName: "project_role_assignments\n",
            constraintName: "uq_platform_admin\n");

        var diagnostic = BootstrapFailureDiagnostics.Format(postgres);

        Assert.Contains("SqlState=unknown", diagnostic, StringComparison.Ordinal);
        Assert.Contains("ConstraintName=unknown", diagnostic, StringComparison.Ordinal);
        Assert.Contains("TableName=unknown", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', diagnostic);
    }
}
