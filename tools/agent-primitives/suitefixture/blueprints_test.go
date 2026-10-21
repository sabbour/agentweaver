package suitefixture

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	agentsuite "github.com/agent-primitives/suite"
)

// blueprintFixtures holds the converted 0.x built-in blueprints written by
// AgentSuiteBlueprintConverterTests. They use test-only build profiles.
const blueprintFixtures = "../../../tests/Agentweaver.Projects.Config.Tests/Fixtures/AgentSuiteBlueprints"

// TestConvertedBlueprintsPassUpstreamValidation checks that every converted
// built-in blueprint is a conformant native AgentSuite package.
func TestConvertedBlueprintsPassUpstreamValidation(t *testing.T) {
	archives, err := filepath.Glob(filepath.Join(blueprintFixtures, "*.tar.gz"))
	if err != nil {
		t.Fatal(err)
	}
	if len(archives) != 5 {
		t.Fatalf("expected 5 converted blueprints, found %d", len(archives))
	}
	for _, archive := range archives {
		name := strings.TrimSuffix(filepath.Base(archive), ".tar.gz")
		t.Run(name, func(t *testing.T) {
			layer, err := os.ReadFile(archive)
			if err != nil {
				t.Fatal(err)
			}
			dir := t.TempDir()
			if _, err := WriteLayout(dir, layer, DefaultLayout()); err != nil {
				t.Fatal(err)
			}
			if _, err := agentsuite.ValidatePath(dir); err != nil {
				t.Fatalf("upstream validation rejected %s: %v", name, err)
			}
		})
	}
}
