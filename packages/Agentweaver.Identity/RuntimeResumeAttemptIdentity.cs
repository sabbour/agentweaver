using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Identity;

public static class RuntimeResumeAttemptIdentity
{
    public static Guid Initial(Guid operationId) => Derive(operationId, Guid.Empty, 0, "attempt");

    public static Guid Replacement(Guid operationId, RuntimeRegistration revoked)
    {
        ArgumentNullException.ThrowIfNull(revoked);
        RuntimeContractValidation.Validate(revoked);
        if (revoked.State != RuntimeRegistrationState.Revoked || revoked.Revision < 2 ||
            revoked.Binding.ResumeOperationId != operationId || revoked.Binding.ResumeAttemptId is null)
            throw new RuntimeAuthorizationException("runtime_resume_prior_attempt_invalid");
        return Derive(operationId, revoked.RuntimeInstanceId, revoked.Revision, "attempt");
    }

    public static Guid Bootstrap(Guid operationId, Guid attemptId) => Effect(operationId, attemptId, "bootstrap");
    public static Guid Consume(Guid operationId, Guid attemptId) => Effect(operationId, attemptId, "consume");
    public static Guid Exchange(Guid operationId, Guid attemptId) => Effect(operationId, attemptId, "exchange");

    private static Guid Effect(Guid operationId, Guid attemptId, string purpose)
    {
        if (attemptId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_resume_attempt_invalid");
        return Derive(operationId, attemptId, 0, purpose);
    }

    private static Guid Derive(Guid operationId, Guid sourceId, long revision, string purpose)
    {
        if (operationId == Guid.Empty)
            throw new RuntimeAuthorizationException("runtime_resume_attempt_invalid");
        var bytes = Encoding.UTF8.GetBytes(
            FormattableString.Invariant(
                $"agentweaver.runtime.resume.v1\0{purpose}\0{operationId:D}\0{sourceId:D}\0{revision}"));
        var digest = SHA256.HashData(bytes);
        var result = new Guid(digest.AsSpan(0, 16));
        return result == Guid.Empty ? new Guid(digest.AsSpan(16, 16)) : result;
    }
}
