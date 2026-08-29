### 2026-08-28: Validate .NET changes in draft stacked pull requests
**By:** Tank
**What:** The .NET shard plan and aggregate checks run whenever the .NET path filter matches, including draft PRs.
**Why:** The #986 and #987 classifiers both matched `dotnet`, but their draft-only guard skipped the plan, all seven real matrix shards, and the aggregate check.
