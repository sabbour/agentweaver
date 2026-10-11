namespace Agentweaver.P1PostgresInitializer;

internal static class InitializerChecks
{
    internal static void AssertPreflight(PreflightSnapshot state)
    {
        var boundary = state.Boundary;
        if (boundary is null || boundary.DatabaseOid == 0 || boundary.OwnerOid == 0 || state.OperatorOid == 0 ||
            boundary.ConnectGrantorOid == 0 || !state.OperatorCanGrantConnect ||
            boundary.Grants.Any(grant => grant.Grantee == 0 &&
                (grant.Privilege != "CONNECT" || grant.Grantable)) ||
            state.ForeignPublicAuthority || state.IncompatibleDefaults)
            throw new InvalidOperationException(
                "Existing public, inherited, or default privileges are incompatible with the planned runtime limits. No initialization writes are permitted.");
    }

    internal static void AssertBoundary(ExistingBoundary before, ExistingBoundary after, uint[] connectedRoles)
    {
        var expected = before.Grants.Concat(connectedRoles.Select(role =>
            new AclGrant(before.ConnectGrantorOid, role, "CONNECT", false))).ToArray();
        if (connectedRoles.Any(role => role == 0) || connectedRoles.Distinct().Count() != connectedRoles.Length ||
            before.DatabaseOid != after.DatabaseOid || before.OwnerOid != after.OwnerOid ||
            before.ConnectGrantorOid != after.ConnectGrantorOid || before.ProtectedState != after.ProtectedState ||
            !SameGrants(expected, after.Grants))
            throw new InvalidOperationException(
                "Existing ownership or privileges changed, or the database CONNECT delta differs from the exact planned roles. The current grant stage is not committed.");
    }

    internal static void AssertPrincipals(ServicePlan[] services, RuntimePrincipal[] principals,
        uint operatorOid, string tenantId)
    {
        if (services.Length != 5 || principals.Length != services.Length || operatorOid == 0 ||
            principals.Select(item => item.Oid).Distinct().Count() != services.Length)
            throw new InvalidOperationException("The five runtime principal mappings are not exact.");
        foreach (var service in services)
        {
            var matches = principals.Where(item => item.Role == service.Role).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("A planned runtime principal is missing or duplicated.");
            var principal = matches[0];
            var membership = new RoleMembership(principal.Oid, operatorOid, operatorOid, true, false, false);
            if (principal.Oid == 0 || !Guid.TryParse(principal.ObjectId, out var actualObject) ||
                !Guid.TryParse(service.PrincipalObjectId, out var expectedObject) || actualObject != expectedObject ||
                !Guid.TryParse(principal.TenantId, out var actualTenant) ||
                !Guid.TryParse(tenantId, out var expectedTenant) || actualTenant != expectedTenant ||
                principal.Type != "service" || principal.Admin || principal.Mfa || !principal.CanLogin ||
                principal.Superuser || principal.CreateDatabase || principal.CreateRole ||
                principal.Replication || principal.Inherit || principal.BypassRls ||
                principal.Memberships.Length != 1 || principal.Memberships[0] != membership)
                throw new InvalidOperationException($"The {service.Service} runtime principal has an unexpected mapping, flag, or membership. Role creation is not committed.");
        }
    }

    internal static void AssertFunctions(ServicePlan service, FunctionState[] functions, uint ownerOid, uint runtimeOid)
    {
        if (ownerOid == 0 || runtimeOid == 0 || functions.Length != service.Functions.Length ||
            functions.Select(item => item.Oid).Distinct().Count() != functions.Length)
            throw new InvalidOperationException("The exact function inventory is missing or duplicated.");
        foreach (var planned in service.Functions)
        {
            if (planned.Execute && (service.Schema != "projects_config" ||
                planned.Signature != "lock_casting_authority(uuid, text, text, text, bigint, text, boolean)"))
                throw new InvalidOperationException("Only the existing casting authority function may receive runtime EXECUTE.");
            var matches = functions.Where(item => item.Signature == planned.Signature).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("A planned function is missing or duplicated.");
            var function = matches[0];
            AclGrant[] expected = planned.Execute
                ? [new(ownerOid, ownerOid, "EXECUTE", false), new(ownerOid, runtimeOid, "EXECUTE", false)]
                : [new(ownerOid, ownerOid, "EXECUTE", false)];
            if (function.Oid == 0 || function.OwnerOid != ownerOid ||
                function.RuntimeCanExecute != planned.Execute || !SameGrants(expected, function.Grants))
                throw new InvalidOperationException($"Unexpected owner or EXECUTE privileges on {service.Schema}.{planned.Signature}.");
        }
    }

    private static bool SameGrants(AclGrant[] expected, AclGrant[] actual) =>
        expected.Length == actual.Length && expected.Distinct().Count() == expected.Length &&
        actual.Distinct().Count() == actual.Length && expected.ToHashSet().SetEquals(actual);

    internal static async Task RunStageAsync(Func<Task> preflight, Func<Task> mutate,
        Func<Task> verify, Func<Task> commit, Func<Task> record)
    {
        await preflight();
        await mutate();
        await verify();
        await commit();
        await record();
    }

    internal static async Task ReportFailureAsync(Exception original, string receiptPath,
        Func<string, Task> record, TextWriter errors, string? sqlState = null)
    {
        await errors.WriteLineAsync(
            $"P1 PostgreSQL initialization stopped: {original.GetType().Name}. Earlier receipts remain in {receiptPath}; do not retry or reset partial state. A failed receipt does not prove rollback.");
        if (sqlState is not null)
            await errors.WriteLineAsync($"PostgreSQL SQLSTATE: {sqlState}");
        try
        {
            await record("failed_requires_explicit_reconciliation");
        }
        catch (Exception receiptError)
        {
            await errors.WriteLineAsync(
                $"The failure receipt could not be written: {receiptError.GetType().Name}. The original initialization failure above still applies; committed state requires explicit reconciliation.");
        }
    }
}

internal sealed record AclGrant(uint Grantor, uint Grantee, string Privilege, bool Grantable);
internal sealed record ExistingBoundary(uint DatabaseOid, uint OwnerOid, uint ConnectGrantorOid,
    AclGrant[] Grants, string ProtectedState);
internal sealed record PreflightSnapshot(ExistingBoundary? Boundary, uint OperatorOid,
    bool OperatorCanGrantConnect, bool ForeignPublicAuthority, bool IncompatibleDefaults);
internal sealed record RoleMembership(uint RoleOid, uint MemberOid, uint GrantorOid,
    bool Admin, bool Inherit, bool Set);
internal sealed record RuntimePrincipal(uint Oid, string Role, string? ObjectId, string? Type,
    bool Admin, bool CanLogin, bool Superuser, bool CreateDatabase, bool CreateRole,
    bool Replication, bool Inherit, bool BypassRls, string? TenantId, bool Mfa, RoleMembership[] Memberships);
internal sealed record FunctionState(uint Oid, string Signature, uint OwnerOid,
    AclGrant[] Grants, bool RuntimeCanExecute);
internal sealed record PostgresPlan(int FormatVersion, TargetPlan Target, ServicePlan[] Services, int[] CoreVersions);
internal sealed record TargetPlan(string TenantId, string PostgresHost, string Database, string OperatorName,
    string OperatorObjectId, string ConnectionHost, int ConnectionPort);
internal sealed record ServicePlan(string Service, string Schema, string Role, string PrincipalObjectId,
    TablePlan[] Tables, FunctionPlan[] Functions);
internal sealed record TablePlan(string Table, string[] Privileges, string? LockColumn, bool MutationDenialRequired);
internal sealed record FunctionPlan(string Signature, bool Execute);
