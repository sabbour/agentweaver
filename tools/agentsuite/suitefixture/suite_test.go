package suitefixture

import (
	"bytes"
	"encoding/json"
	"io/fs"
	"os"
	"path/filepath"
	"testing"
)

// committedFixtures is the checked-in copy consumed by the .NET parity tests.
const committedFixtures = "../../../tests/Agentweaver.Projects.Config.Tests/Fixtures/AgentSuite"

func TestCommittedFixturesMatchGenerator(t *testing.T) {
	root := t.TempDir()
	verdicts, err := Write(root)
	if err != nil {
		t.Fatal(err)
	}
	data, err := json.MarshalIndent(verdicts, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "parity.json"), append(data, '\n'), 0o644); err != nil {
		t.Fatal(err)
	}
	generated := readTree(t, root)
	committed := readTree(t, committedFixtures)
	for name, want := range generated {
		got, ok := committed[name]
		if !ok {
			t.Errorf("committed fixtures are missing %s; regenerate with cmd/agentsuite-fixture", name)
			continue
		}
		if !bytes.Equal(got, want) {
			t.Errorf("committed fixture %s differs from the generator; regenerate with cmd/agentsuite-fixture", name)
		}
	}
	for name := range committed {
		if _, ok := generated[name]; !ok {
			t.Errorf("committed fixture %s is not produced by the generator", name)
		}
	}
}

func readTree(t *testing.T, root string) map[string][]byte {
	t.Helper()
	files := map[string][]byte{}
	err := filepath.WalkDir(root, func(path string, entry fs.DirEntry, err error) error {
		if err != nil || entry.IsDir() {
			return err
		}
		rel, err := filepath.Rel(root, path)
		if err != nil {
			return err
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		files[filepath.ToSlash(rel)] = data
		return nil
	})
	if err != nil {
		t.Fatal(err)
	}
	return files
}

func TestVariantsMatchUpstreamVerdicts(t *testing.T) {
	first, err := Write(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	second, err := Write(filepath.Join(t.TempDir(), "again"))
	if err != nil {
		t.Fatal(err)
	}
	byName := map[string]Verdict{}
	for i, v := range first {
		byName[v.Name] = v
		if second[i] != v {
			t.Fatalf("variant %s is not deterministic: %+v != %+v", v.Name, v, second[i])
		}
		switch v.Name {
		case "valid", "valid-retagged", "team", "team-model-secret-refs":
			if !v.Accepted {
				t.Fatalf("%s rejected: %s", v.Name, v.Error)
			}
		default:
			if v.Accepted {
				t.Fatalf("%s accepted", v.Name)
			}
		}
	}
	if byName["valid"].ManifestDigest != byName["valid-retagged"].ManifestDigest {
		t.Fatal("a tag change must not change the manifest digest")
	}
}
