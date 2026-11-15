using System.Text;
using System.Text.Json;
using Agentweaver.Projects.Config;
using Xunit;
using Npgsql;

namespace Agentweaver.Projects.Config.Tests;

public sealed class NativeAcsPolicyHistoryRequestTests
{
    [Fact]
    public void PageSizeUsesDefaultAndAcceptsInclusiveBounds()
    {
        Assert.Equal(20, NativeAcsPolicyStore.ParseHistoryPageSize(null));
        Assert.Equal(1, NativeAcsPolicyStore.ParseHistoryPageSize("1"));
        Assert.Equal(100, NativeAcsPolicyStore.ParseHistoryPageSize("100"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData(" 2")]
    [InlineData("x")]
    [InlineData("2147483648")]
    public void PageSizeRejectsInvalidValues(string value) =>
        AssertCode(
            "native_acs_policy_history_request_invalid",
            () => NativeAcsPolicyStore.ParseHistoryPageSize(value));

    [Theory]
    [InlineData("1", 1)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void RevisionAcceptsCanonicalPositiveInt64(string value, long expected) =>
        Assert.Equal(expected, NativeAcsPolicyStore.ParseRevision(value));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("9223372036854775808")]
    [InlineData("１２")]
    public void RevisionRejectsNonCanonicalOrOutOfRangeValues(string value) =>
        AssertCode(
            "native_acs_policy_revision_request_invalid",
            () => NativeAcsPolicyStore.ParseRevision(value));

    [Fact]
    public void CursorRoundTripsAndBindsPageSizeAndLineage()
    {
        var encoded = NativeAcsPolicyStore.EncodeHistoryCursor("stable-policy", 12, 4, 8);

        var decoded = NativeAcsPolicyStore.DecodeHistoryCursor(encoded, 4);

        Assert.Equal("stable-policy", decoded.PolicyId);
        Assert.Equal(12, decoded.SnapshotRevision);
        Assert.Equal(8, decoded.LastRevision);
        Assert.Equal(4, decoded.PageSize);
        Assert.Throws<ProjectConfigException>(() => NativeAcsPolicyStore.DecodeHistoryCursor(encoded, 5));
    }

    [Theory]
    [InlineData("""{"version":2,"order":"revision-descending","scopeType":"platform","scopeId":"default","policyId":"p","snapshotRevision":4,"pageSize":2,"lastRevision":2}""")]
    [InlineData("""{"version":1,"order":"revision-descending","scopeType":"project","scopeId":"default","policyId":"p","snapshotRevision":4,"pageSize":2,"lastRevision":2}""")]
    [InlineData("""{"version":1,"order":"revision-descending","scopeType":"platform","scopeId":"other","policyId":"p","snapshotRevision":4,"pageSize":2,"lastRevision":2}""")]
    [InlineData("""{"version":1,"order":"revision-descending","scopeType":"platform","scopeId":"default","policyId":"p","snapshotRevision":4,"pageSize":2,"lastRevision":5}""")]
    [InlineData("""{"version":1,"order":"revision-descending","scopeType":"platform","scopeId":"default","policyId":"p","snapshotRevision":4,"pageSize":2,"lastRevision":2,"extra":true}""")]
    [InlineData("""{"version":1,"order":"revision-descending","scopeType":"platform","scopeId":"default","policyId":"p","snapshotRevision":4,"pageSize":2,"lastRevision":2,"lastRevision":1}""")]
    public void CursorRejectsInvalidLineageFields(string json) =>
        AssertCode(
            "native_acs_policy_history_request_invalid",
            () => NativeAcsPolicyStore.DecodeHistoryCursor(Encode(json), 2));

    [Fact]
    public void CursorRejectsOversizedAndMalformedValues()
    {
        AssertCode(
            "native_acs_policy_history_request_invalid",
            () => NativeAcsPolicyStore.DecodeHistoryCursor(new string('a', 2049), 2));
        AssertCode(
            "native_acs_policy_history_request_invalid",
            () => NativeAcsPolicyStore.DecodeHistoryCursor("?", 2));
    }

    [Fact]
    public void EfConnectionFailureWrapperRequiresAnUnavailableProviderException()
    {
        Assert.True(NativeAcsPolicyEndpoints.IsSourceUnavailable(
            new InvalidOperationException("EF query failed", new NpgsqlException(
                "provider failed", new IOException("connection closed")))));
        Assert.False(NativeAcsPolicyEndpoints.IsSourceUnavailable(
            new InvalidOperationException("unrelated", new NpgsqlException("provider error"))));
        Assert.False(NativeAcsPolicyEndpoints.IsSourceUnavailable(
            new InvalidOperationException("unrelated", new IOException("connection closed"))));
    }

    private static string Encode(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void AssertCode(string code, Action action)
    {
        var exception = Assert.Throws<ProjectConfigException>(action);
        Assert.Equal(code, exception.Code);
    }
}
