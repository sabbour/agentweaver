using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Providers.Tests;

public sealed class RemoteMcpToolCallBytesTests
{
    [Theory]
    [InlineData("\"rpc-local-id\"")]
    [InlineData("\"native-call-1\"")]
    [InlineData("\"\"")]
    [InlineData("0")]
    [InlineData("-42")]
    [InlineData("9007199254740993")]
    [InlineData("1.5")]
    public void RequestIdentityIsIndependentOfReviewedNativeIdentity(string rpcId)
    {
        var request = Request();
        var proofHash = SessionMaterialValidation.RemoteToolResultProofHash(request.Proof);

        Validate(request, Wire(rpcId));

        Assert.Equal("native-call-1", request.CallBinding.NativeCallId);
        Assert.Equal(proofHash, SessionMaterialValidation.RemoteToolResultProofHash(request.Proof));
    }

    [Fact]
    public void CanonicalArgumentsIgnoreObjectOrderWhitespaceAndEquivalentStringEscapes()
    {
        var request = Request("""{"a":[true,null,{"a":1.0,"z":"\u00E9"}],"z":"x"}""");
        var wire = """
            {"params":{"arguments":{"z":"\u0078","a":[true,null,{"z":"\u00e9","a":1.0}]},"name":"remote.lookup"},
             "method":"tools/call","id":"local","jsonrpc":"2.0"}
            """;

        Validate(request, wire);
    }

    [Theory]
    [InlineData("\"progress-1\"")]
    [InlineData("5")]
    public void LegitimateMetadataDoesNotReplaceReviewedAuthority(string progressToken)
    {
        var request = Request();
        var wire = """
            {"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},
            "_meta":{"progressToken":
            """ + progressToken + ""","example.com/context":{"actor":"untrusted","revision":99}}}}""";
        var binding = request.CallBinding;

        Validate(request, wire);

        Assert.Equal(binding, request.CallBinding);
        Assert.Equal("actor-1", request.Proof.ActorId);
    }

    [Theory]
    [InlineData("""{"name":"remote.lookup"}""")]
    [InlineData("""{"name":"remote.lookup","arguments":{}}""")]
    public void OmittedArgumentsAgreeOnlyWithTheReviewedEmptyObject(string parameters)
    {
        var wire = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":""" + parameters + "}";

        Validate(Request("{}"), wire);
        Assert.Throws<ArgumentException>(() => Validate(Request(), wire));
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"other","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"y"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x","extra":true}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":null}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":[]}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":42,"arguments":{"query":"x"}}}""")]
    public void ChangedOperationToolOrArgumentsCannotMatchTheProof(string wire) =>
        Assert.Throws<ArgumentException>(() => Validate(Request(), wire));

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"value":1.0}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"value":1e0}}}""")]
    public void NumericSpellingRetainsTheExistingReviewedDigestSemantics(string wire) =>
        Assert.Throws<ArgumentException>(() => Validate(Request("""{"value":1}"""), wire));

    [Fact]
    public void ArrayOrderMustMatchTheReviewedArguments() =>
        Assert.Throws<ArgumentException>(() => Validate(Request("""{"query":["a","b"]}"""),
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":["b","a"]}}}"""));

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","m\u0065thod":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x","query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},"_meta":{"nested":[{"x":1,"x":1}]}}}""")]
    public void DuplicatePropertiesAreRejectedAtEveryDepth(string wire) =>
        Assert.Throws<ArgumentException>(() => Validate(Request(), wire));

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""null""")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":true,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":{},"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"result":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":[]}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},"actor":"actor-1"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},"argumentsDigest":"caller-digest"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}},"proof":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},"_meta":null}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},"_meta":{"progressToken":null}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},"_meta":{"progressToken":true}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},}}""")]
    [InlineData("""{"jsonrpc":"2.0",/*comment*/"id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}} {}""")]
    public void NonRequestUnsupportedOrMalformedJsonIsRejected(string wire) =>
        Assert.Throws<ArgumentException>(() => Validate(Request(), wire));

    [Theory]
    [InlineData("GET", "https://mcp.example.test/api")]
    [InlineData("post", "https://mcp.example.test/api")]
    [InlineData("POST", "https://other.example.test/api")]
    [InlineData("POST", "https://mcp.example.test/other")]
    public void ActualHttpMethodAndTargetMustAgreeWithTheReviewedBinding(string method, string target) =>
        Assert.Throws<ArgumentException>(() => Validate(Request(), Wire(), method, target));

    [Fact]
    public void ChangedProofOrBindingIsRejectedBeforeWireAgreement()
    {
        var request = Request();
        Assert.Throws<ArgumentException>(() => Validate(request with
        {
            CallBinding = request.CallBinding with { NativeCallId = "rpc-local-id" }
        }, Wire()));
        Assert.Throws<ArgumentException>(() => Validate(request with
        {
            Proof = request.Proof with { ArgumentsDigest = Hash("untrusted") }
        }, Wire()));
    }

    [Fact]
    public void ExactBodyByteLimitIsAcceptedAndOneMoreByteIsRejected()
    {
        var request = Request();
        var wire = Encoding.UTF8.GetBytes(Wire());
        var bounded = new byte[SessionMaterialValidation.MaximumBytes];
        Array.Fill(bounded, (byte)' ');
        wire.CopyTo(bounded, 0);
        byte[] oversized = [.. bounded, (byte)' '];

        Validate(request, bounded);
        Assert.Throws<ArgumentException>(() => Validate(request, oversized));
        Assert.Throws<ArgumentException>(() => Validate(request, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void LimitsCountUtf8BytesRatherThanCharacters()
    {
        var multibyte = new string('\u00e9', SessionMaterialValidation.MaximumBytes / 2);
        var wire = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},"_meta":{"note":"""
            + "\"" + multibyte + "\"}}}";
        Assert.True(wire.Length < SessionMaterialValidation.MaximumBytes);
        Assert.True(Encoding.UTF8.GetByteCount(wire) > SessionMaterialValidation.MaximumBytes);

        Assert.Throws<ArgumentException>(() => Validate(Request(), wire));
    }

    [Fact]
    public void ArgumentByteLimitPreservesTheExactReviewedBoundary()
    {
        const string prefix = "{\"query\":\"";
        const string suffix = "\"}";
        var text = new string('x', SessionMaterialValidation.MaximumRemoteToolArgumentsBytes - prefix.Length - suffix.Length);
        var arguments = prefix + text + suffix;
        var wire = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":"""
            + arguments + "}}";

        var request = Request(arguments);
        Validate(request, wire);
        var oversizedWire = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":"""
            + prefix + text + "x" + suffix + "}}";
        Assert.Throws<ArgumentException>(() => Validate(request, oversizedWire));
        Assert.Throws<ArgumentException>(() => Request(prefix + text + "x" + suffix));
    }

    [Theory]
    [InlineData("rpc-local-id")]
    [InlineData("metadata-value")]
    public void InvalidUtf8IsRejectedEvenOutsideToolArguments(string value)
    {
        var wire = """{"jsonrpc":"2.0","id":"rpc-local-id","method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},"_meta":{"note":"metadata-value"}}}""";
        var bytes = Encoding.UTF8.GetBytes(wire);
        bytes[wire.IndexOf(value, StringComparison.Ordinal)] = 0xff;

        Assert.Throws<ArgumentException>(() => Validate(Request(), bytes));
    }

    [Fact]
    public void InvalidUtf8AndExcessiveJsonDepthAreRejected()
    {
        var bytes = Encoding.UTF8.GetBytes(Wire());
        bytes[Array.IndexOf(bytes, (byte)'x')] = 0xff;
        Assert.Throws<ArgumentException>(() => Validate(Request(), bytes));
        var nested = new string('[', 65) + "0" + new string(']', 65);
        var wire = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"},"_meta":{"nested":"""
            + nested + "}}}";
        Assert.Throws<ArgumentException>(() => Validate(Request(), wire));
    }

    private static string Wire(string rpcId = "\"rpc-local-id\"") =>
        """{"jsonrpc":"2.0","id":""" + rpcId
            + ""","method":"tools/call","params":{"name":"remote.lookup","arguments":{"query":"x"}}}""";

    private static void Validate(
        RemoteMcpCurrentCallVerificationRequest request, string wire,
        string method = "POST", string target = "https://mcp.example.test/api") =>
        RemoteMcpCurrentCallContract.ValidateToolCallBytes(request, method, target, Encoding.UTF8.GetBytes(wire));

    private static void Validate(RemoteMcpCurrentCallVerificationRequest request, ReadOnlyMemory<byte> bytes) =>
        RemoteMcpCurrentCallContract.ValidateToolCallBytes(request, "POST", "https://mcp.example.test/api", bytes);

    private static RemoteMcpCurrentCallVerificationRequest Request(string arguments = """{"query":"x"}""")
    {
        var eventId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var proof = new RemoteToolResultProof(1, eventId, Hash("input"), "native-call-1",
            new("project-1", Guid.Parse("44444444-4444-4444-4444-444444444444"), Hash("snapshot"), "agent", "node"),
            new("connection-1", 1, 2, 3, "Enabled", Hash("configuration"),
                "https://mcp.example.test/api", "https://mcp.example.test/resource", "None", null,
                "StreamableHttp20250618", null, null, null, "catalog-1", Hash("catalog"),
                "remote.lookup", "tool-1", Hash("tool"), "schema-1", Hash("schema"),
                "tool.read", "runtime.execution", Hash("permission")),
            "remote.lookup", arguments, Hash(arguments),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 1,
            "https://identity.test/", "actor-1", "tenant-1", "project-1", "run-1", "session-1", "step-1",
            1, 1, 1, "context-1", Hash("selection"), 1, "tool.read", "runtime.execution",
            1, true, eventId.ToString("N"), "1", eventId, PolicyEvaluationOutcome.Allow,
            PolicyEvaluationReasonCode.Allowed, SessionMaterialResultContentClass.TextOnly);
        return RemoteMcpCurrentCallContract.CreateRequest(proof);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
