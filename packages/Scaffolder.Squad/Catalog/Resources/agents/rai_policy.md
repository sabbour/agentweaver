# RAI Policy

This policy defines the Responsible AI checks performed by the Rai agent on this project.

## Check Categories

### Critical (always enabled — cannot be disabled)
- **Credential leaks** — API keys, tokens, passwords, connection strings in code or content
- **PII exposure** — personal data (names, emails, phone numbers, IDs) without justification
- **Injection vulnerabilities** — prompt injection, SQL injection, command injection
- **Harmful content** — content that could cause physical or emotional harm

### Advisory (enabled by default — can be disabled with justification)
- **Exclusionary language** — terms that exclude or demean groups of people
- **Deceptive patterns** — UI/UX patterns designed to mislead users
- **Bias indicators** — algorithmic or content bias in models or outputs
- **Insufficient grounding** — AI outputs presented as facts without citations
- **Privacy risks** — unnecessary data collection or retention in prompts

## Terminology Standards

Use inclusive, precise language. Avoid: blacklist/whitelist (use allowlist/denylist), master/slave (use primary/replica), and gendered defaults.

## Opt-Out

Advisory checks may be disabled temporarily with justification logged to `.squad/rai/audit-trail.md`. Critical checks cannot be disabled. Temporary opt-outs auto-expire after 30 days.

## Audit Trail

All RAI review findings are recorded to `.squad/rai/audit-trail.md`. This file is append-only and is never edited after write.
