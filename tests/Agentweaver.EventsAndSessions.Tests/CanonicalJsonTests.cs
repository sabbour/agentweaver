using Xunit;
using System.Text;
using Agentweaver.Abstractions;

namespace Agentweaver.EventsAndSessions.Tests;

public sealed class CanonicalJsonTests
{
    [Theory]
    [InlineData("0", "0")]
    [InlineData("-0", "0")]
    [InlineData("1e21", "1e+21")]
    [InlineData("1e20", "100000000000000000000")]
    [InlineData("1e-7", "1e-7")]
    [InlineData("0.000001", "0.000001")]
    [InlineData("123456789012345680000", "123456789012345680000")]
    [InlineData("5e-324", "5e-324")]
    [InlineData("1.7976931348623157e308", "1.7976931348623157e+308")]
    [InlineData("333333333.33333329", "333333333.3333333")]
    [InlineData("4.50", "4.5")]
    [InlineData("2e-3", "0.002")]
    [InlineData("-1.5E+2", "-150")]
    public void NumbersUseTheEcmaScriptShortestForm(string input, string expected) =>
        Assert.Equal(expected, Encoding.UTF8.GetString(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(input))));

    [Fact]
    public void ObjectMembersSortByUtf16CodeUnitsAndStringsUseMinimalEscaping()
    {
        // RFC 8785 section 3.2.3 sorting sample.
        var input = "{\"\\u20ac\":\"Euro\",\"\\r\":\"cr\",\"\\ufb33\":\"h\",\"1\":\"one\",\"\\ud83d\\ude00\":\"smile\",\"\\u0080\":\"c\",\"\\u00f6\":\"o\"}";
        var expected = "{\"\\r\":\"cr\",\"1\":\"one\",\"\u0080\":\"c\",\"\u00f6\":\"o\",\"\u20ac\":\"Euro\",\"\ud83d\ude00\":\"smile\",\"\ufb33\":\"h\"}";
        Assert.Equal(expected, Encoding.UTF8.GetString(CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(input))));
        Assert.Equal("[\"\\u001f/\\\"\\\\\\b\\f\\n\\r\\t\"]",
            Encoding.UTF8.GetString(CanonicalJson.Canonicalize("[\"\\u001f\\/\\\"\\\\\\b\\f\\n\\r\\t\"]"u8)));
        Assert.Equal("{\"a\":[true,false,null,{}],\"b\":{\"c\":1}}",
            Encoding.UTF8.GetString(CanonicalJson.Canonicalize(" { \"b\" : { \"c\" : 1.0 } , \"a\" : [ true , false , null , { } ] } "u8)));
    }

    [Theory]
    [InlineData("{\"a\":1,\"a\":2}", "duplicate_key")]
    [InlineData("{\"a\":{\"b\":1,\"b\":1}}", "duplicate_key")]
    [InlineData("\"\\ud800\"", "lone_surrogate")]
    [InlineData("{\"\\udc00\":1}", "lone_surrogate")]
    [InlineData("1e400", "invalid_number")]
    [InlineData("[1,]", "invalid_json")]
    [InlineData("{} {}", "invalid_json")]
    [InlineData("", "invalid_json")]
    public void InvalidInputFailsExplicitly(string input, string code) =>
        Assert.Equal(code, Assert.Throws<CanonicalJsonException>(() => CanonicalJson.Canonicalize(Encoding.UTF8.GetBytes(input))).Code);

    [Fact]
    public void InvalidUtf8AndLimitsFailExplicitly()
    {
        Assert.Equal("invalid_utf8", Assert.Throws<CanonicalJsonException>(() => CanonicalJson.Canonicalize([0x22, 0xC3, 0x28, 0x22])).Code);
        Assert.Equal("too_large", Assert.Throws<CanonicalJsonException>(() => CanonicalJson.Canonicalize("[1]"u8, new CanonicalJsonLimits(MaxBytes: 2))).Code);
        Assert.Equal("invalid_json", Assert.Throws<CanonicalJsonException>(() => CanonicalJson.Canonicalize("[[[1]]]"u8, new CanonicalJsonLimits(MaxDepth: 2))).Code);
    }

    [Fact]
    public void IsCanonicalComparesExactBytes()
    {
        Assert.True(CanonicalJson.IsCanonical("{\"a\":1,\"b\":[2]}"u8));
        Assert.False(CanonicalJson.IsCanonical("{\"b\":[2],\"a\":1}"u8));
        Assert.False(CanonicalJson.IsCanonical("{\"a\":1.0}"u8));
        Assert.False(CanonicalJson.IsCanonical("{\"a\":1}\n"u8));
    }
}
