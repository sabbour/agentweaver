using System.Globalization;

namespace Agentweaver.Api.Auth.OAuth;

internal static class OAuthConsentContentSecurityPolicy
{
    internal static bool TrySerializeCallbackSource(
        string validatedRedirectUri,
        out string callbackSource)
    {
        callbackSource = string.Empty;
        if (!OAuthRedirectUriValidator.IsValid(
                validatedRedirectUri,
                allowDynamicLoopbackPort: true)
            || !Uri.TryCreate(validatedRedirectUri, UriKind.Absolute, out var redirect))
        {
            return false;
        }

        if (string.Equals(redirect.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || string.Equals(redirect.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            if (redirect.HostNameType == UriHostNameType.IPv6)
                return false;

            callbackSource = $"{redirect.Scheme}://{redirect.IdnHost}";
            if (!redirect.IsDefaultPort)
                callbackSource += $":{redirect.Port.ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            callbackSource = $"{redirect.Scheme}:";
        }

        if (!IsSafeToken(callbackSource))
        {
            callbackSource = string.Empty;
            return false;
        }

        return true;
    }

    internal static string Create(string styleNonce, string callbackSource)
    {
        ValidateNonce(styleNonce);
        if (!IsSafeToken(callbackSource))
            throw new ArgumentException("Consent CSP callback source must be a serialized safe token.");

        return CreateCore(styleNonce, $" 'self' {callbackSource}");
    }

    internal static string CreateNoForm(string styleNonce)
    {
        ValidateNonce(styleNonce);
        return CreateCore(styleNonce, " 'none'");
    }

    private static string CreateCore(string styleNonce, string formActionSources) =>
        $"default-src 'none'; style-src 'nonce-{styleNonce}'; img-src 'self'; " +
        $"form-action{formActionSources}; base-uri 'none'; frame-ancestors 'none'";

    private static void ValidateNonce(string styleNonce)
    {
        if (!IsSafeToken(styleNonce))
            throw new ArgumentException("Consent CSP nonce must be a serialized safe token.");
    }

    private static bool IsSafeToken(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Any(char.IsWhiteSpace)
        && value.IndexOfAny([';', '\'', '"', '\r', '\n']) < 0;
}
