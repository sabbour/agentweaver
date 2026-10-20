package suitefixture

import (
	"path/filepath"
	"testing"
)

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
		case "valid", "valid-retagged":
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
