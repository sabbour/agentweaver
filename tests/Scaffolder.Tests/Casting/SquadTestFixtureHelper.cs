namespace Scaffolder.Tests.Casting;

/// <summary>
/// Static helper that creates test .squad/ fixture directories for casting tests.
/// All methods write real files to the filesystem — no mocks.
/// </summary>
public static class SquadTestFixtureHelper
{
    /// <summary>
    /// Creates a minimal valid .squad/ directory in the given path with:
    /// team.md, casting/policy.json, casting/registry.json (one agent), casting/history.json (empty),
    /// and one member charter (Lead Architect named "Alpha").
    /// </summary>
    public static void CreateMinimalSquad(string directory, string projectName = "test-project")
    {
        var squadDir    = Path.Combine(directory, ".squad");
        var castingDir  = Path.Combine(squadDir, "casting");
        var agentsDir   = Path.Combine(squadDir, "agents", "alpha");

        Directory.CreateDirectory(castingDir);
        Directory.CreateDirectory(agentsDir);

        File.WriteAllText(Path.Combine(squadDir, "team.md"),         BuildTeamMd(projectName));
        File.WriteAllText(Path.Combine(castingDir, "policy.json"),   BuildPolicyJson());
        File.WriteAllText(Path.Combine(castingDir, "registry.json"), BuildRegistryJson());
        File.WriteAllText(Path.Combine(castingDir, "history.json"),  BuildEmptyHistoryJson());
        File.WriteAllText(Path.Combine(agentsDir, "charter.md"),     BuildCharterMd("Alpha", "Lead Architect", projectName));
    }

    /// <summary>
    /// Creates a .squad/ directory with a canonical casting/ subfolder layout.
    /// </summary>
    public static void CreateCanonicalLayout(string directory, string projectName = "test-project")
    {
        CreateMinimalSquad(directory, projectName);
    }

    /// <summary>
    /// Creates a .squad/ directory with a legacy root-level casting files layout
    /// (registry.json and history.json at the .squad/ root rather than .squad/casting/).
    /// </summary>
    public static void CreateLegacyLayout(string directory, string projectName = "test-project")
    {
        var squadDir   = Path.Combine(directory, ".squad");
        var agentsDir  = Path.Combine(squadDir, "agents", "alpha");

        Directory.CreateDirectory(agentsDir);

        File.WriteAllText(Path.Combine(squadDir, "team.md"),         BuildTeamMd(projectName));
        File.WriteAllText(Path.Combine(squadDir, "policy.json"),     BuildPolicyJson());
        File.WriteAllText(Path.Combine(squadDir, "registry.json"),   BuildRegistryJson());
        File.WriteAllText(Path.Combine(squadDir, "history.json"),    BuildEmptyHistoryJson());
        File.WriteAllText(Path.Combine(agentsDir, "charter.md"),     BuildCharterMd("Alpha", "Lead Architect", projectName));
    }

    /// <summary>
    /// Creates a .squad/ directory that has both the canonical casting/ subfolder and
    /// legacy root-level files — used for conflict detection testing.
    /// </summary>
    public static void CreateConflictLayout(string directory)
    {
        // Write canonical layout first, then also write legacy root-level files.
        CreateCanonicalLayout(directory, "conflict-project");

        var squadDir = Path.Combine(directory, ".squad");
        File.WriteAllText(Path.Combine(squadDir, "registry.json"), BuildRegistryJson());
        File.WriteAllText(Path.Combine(squadDir, "history.json"),  BuildEmptyHistoryJson());
    }

    /// <summary>
    /// Creates a project with detectable signals for analysis-based casting tests:
    /// package.json (React framework), tests/ directory, src/ directory with TypeScript files.
    /// </summary>
    public static void CreateProjectWithSignals(string directory)
    {
        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "package.json"),
            """
            {
              "name": "signal-project",
              "version": "1.0.0",
              "dependencies": {
                "react": "^18.0.0",
                "react-dom": "^18.0.0"
              },
              "devDependencies": {
                "typescript": "^5.0.0",
                "jest": "^29.0.0"
              }
            }
            """);

        var srcDir   = Path.Combine(directory, "src");
        var testsDir = Path.Combine(directory, "tests");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(testsDir);

        File.WriteAllText(
            Path.Combine(srcDir, "App.tsx"),
            "import React from 'react';\nexport const App = () => <div>Hello</div>;\n");

        File.WriteAllText(
            Path.Combine(srcDir, "index.ts"),
            "export * from './App';\n");

        File.WriteAllText(
            Path.Combine(testsDir, "App.test.tsx"),
            "import { App } from '../src/App';\ntest('renders', () => {});\n");
    }

    /// <summary>
    /// Creates an empty project directory with no detectable signals.
    /// </summary>
    public static void CreateEmptyProject(string directory)
    {
        Directory.CreateDirectory(directory);
    }

    // -------------------------------------------------------------------------
    // Private builders
    // -------------------------------------------------------------------------

    private static string BuildTeamMd(string projectName) =>
        $"""
        # Squad Team

        > {projectName}

        ## Members

        | Name | Role | Charter | Status |
        |------|------|---------|--------|
        | Alpha | Lead Architect | .squad/agents/alpha/charter.md | active |

        ## Project Context

        - **Project:** {projectName}
        - **Universe:** Inception
        - **Created:** 2026-01-01
        - **Requested by:** test-user
        """;

    private static string BuildPolicyJson() =>
        """
        {
          "version": "1.0",
          "allowlist_universes": ["The Matrix", "Star Wars", "Inception", "Firefly"],
          "universe_capacity": {"The Matrix": 10, "Star Wars": 12, "Inception": 8, "Firefly": 10}
        }
        """;

    private static string BuildRegistryJson() =>
        """
        {
          "agents": {
            "lead-architect": {
              "name": "lead-architect",
              "persistent_name": "Alpha",
              "universe": "Inception",
              "default_model": "claude-opus-4.8",
              "status": "Active",
              "created_at": "2026-01-01T00:00:00Z",
              "previous_name": null,
              "succeeded_by": null,
              "retired_at": null,
              "charter_path": ".squad/agents/alpha/charter.md"
            }
          }
        }
        """;

    private static string BuildEmptyHistoryJson() =>
        """
        {
          "events": []
        }
        """;

    private static string BuildCharterMd(string name, string role, string projectName) =>
        $"""
        # {name} — {role}

        ## Project

        {projectName}

        ## Responsibilities

        - Lead architecture decisions
        - Review technical designs
        - Ensure code quality

        ## Capabilities

        - System design
        - Code review
        - Technical documentation

        ## Boundaries

        - Does not write production code directly
        - Escalates security concerns to team
        """;
}
