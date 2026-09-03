---
"agentweaver": patch
---

Fix "Repository authorization is currently unavailable. Try again later." appearing after authorizing the GitHub Repo App from Create-from-GitHub, project settings, or account settings. `RepoAppUserAuthorizationService` was looking up the authenticated GitHub identity (and revoking grants) against the OAuth-authorize host (`https://github.com`) instead of the REST API host (`https://api.github.com`), which silently returned a 406 and discarded an otherwise-valid access token. Both call sites now use a new `Auth:RepoApp:ApiUrl` config key (defaulting to `https://api.github.com`, mirroring the existing `Auth:CopilotApp:ApiUrl` pattern), and a failure of this kind now logs a redacted diagnostic event instead of failing silently.
