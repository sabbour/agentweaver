function xmlAttribute(value) {
  return String(value).replace(/[<>&'"]/g, character => ({
    '<': '&lt;', '>': '&gt;', '&': '&amp;', "'": '&apos;', '"': '&quot;',
  })[character]);
}

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
