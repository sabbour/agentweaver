/** Creates an in-memory NuGet configuration for the selected authenticated package feed. */
function xmlAttribute(value) {
  return String(value).replace(/[<>&'"]/g, character => ({
    '<': '&lt;', '>': '&gt;', '&': '&amp;', "'": '&apos;', '"': '&quot;',
  })[character]);
}

/**
 * Returns NuGet XML with escaped feed credentials so it can be written to a temporary config file.
 * The caller owns the returned string and must control its lifetime and file permissions.
 */
export function createNugetConfig(feed, username, token) {
  return `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="agentweaver-publication" value="${xmlAttribute(feed)}" />
  </packageSources>
  <packageSourceCredentials>
    <agentweaver-publication>
      <add key="Username" value="${xmlAttribute(username)}" />
      <add key="ClearTextPassword" value="${xmlAttribute(token)}" />
    </agentweaver-publication>
  </packageSourceCredentials>
</configuration>
`;
}
