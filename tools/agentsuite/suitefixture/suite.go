// Package suitefixture builds the Agentweaver proof-of-concept AgentSuite
// artifact and a set of invalid variants. The pinned upstream validator
// decides each verdict, so the .NET importer tests can compare against it.
package suitefixture

import (
	"archive/tar"
	"bytes"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	agentsuite "github.com/agent-primitives/suite"
)

// File is one entry in the suite content layer.
type File struct {
	Path string
	Data []byte
	Mode int64
	Type byte
	Link string
}

// Content is an ordered set of content-layer entries.
type Content []File

const (
	// Instructions are literal Markdown bytes. They are not JSON and are not canonicalized.
	Instructions = "Repeat the user's request in one short sentence.\n"
	// ExecutionModel is the deterministic proof-of-concept model identity. It is never a paid model.
	ExecutionModel = "agentweaver-echo-1"
	// ExecutionProtocol names the example echo harness protocol. It is fixture data only.
	ExecutionProtocol = "example-echo"
)

func digest(data []byte) string {
	sum := sha256.Sum256(data)
	return "sha256:" + hex.EncodeToString(sum[:])
}

func canonical(value any) []byte {
	// The upstream Marshal writes RFC 8785 canonical JSON. Normative suite documents must be stored in this
	// exact form, otherwise the pinned validator rejects them.
	data, err := agentsuite.Marshal(value)
	if err != nil {
		// Inputs are fixed fixture data, so an error here is a programming mistake, not a runtime condition.
		panic(err)
	}
	return data
}

func descriptor(mediaType string, data []byte) map[string]any {
	// A descriptor pins a referenced file by media type, SHA-256 digest, and byte size.
	// The validator recomputes all three from the stored bytes.
	return map[string]any{"mediaType": mediaType, "digest": digest(data), "size": len(data)}
}

// Cases returns the authored evaluation cases in index order.
func Cases() []map[string]any {
	// The cases cover each assertion type the importer must read: contains, notContains, regex, toolCalled,
	// and an "x-" extension assertion marked non-critical. Only "greeting" sets runs and minPasses, so the
	// other cases exercise the defaults.
	return []map[string]any{
		{
			"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeEvaluationCase,
			"id": "greeting", "input": "hello from agentweaver", "runs": 3, "minPasses": 2,
			"assertions": []any{
				map[string]any{"id": "echo", "type": "contains", "value": "hello from agentweaver"},
				map[string]any{"id": "no-error", "type": "notContains", "value": "error"},
			},
		},
		{
			"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeEvaluationCase,
			"id": "pattern", "input": "order 42 shipped",
			"assertions": []any{
				map[string]any{"id": "order-number", "type": "regex", "pattern": `\border \d+\b`},
				map[string]any{"id": "missing", "type": "contains", "value": "cancelled"},
			},
		},
		{
			"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeEvaluationCase,
			"id": "tooling", "input": "look up the weather",
			"assertions": []any{
				map[string]any{"id": "weather-tool", "type": "toolCalled", "tool": "get_weather"},
				map[string]any{"id": "tone", "type": "x-tone", "critical": false, "value": "friendly"},
			},
		},
	}
}

// Build returns the content layer of the proof-of-concept suite.
func Build() Content {
	// Files are built from the leaves upward. Each parent document embeds the digest of its children, so a
	// child's bytes must be final before the parent that references it is created.
	instructions := []byte(Instructions)
	var caseRefs []any
	var files Content
	for _, c := range Cases() {
		data := canonical(c)
		p := "evals/writer/cases/" + c["id"].(string) + ".json"
		files = append(files, File{Path: p, Data: data})
		caseRefs = append(caseRefs, map[string]any{"path": p, "descriptor": descriptor(agentsuite.MediaTypeEvaluationCase, data)})
	}
	// The evaluation index lists every case by path and digest, so it is built after the cases.
	index := canonical(map[string]any{
		"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeEvaluationIndex,
		"agent": "writer", "cases": caseRefs, "limits": map[string]any{"maxCases": 16, "maxCaseBytes": 65536},
	})
	// The agent manifest references the instructions and the evaluation index by digest.
	agent := canonical(map[string]any{
		"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeAgent,
		"id": "writer", "description": "Restates a request. Agentweaver P2 proof-of-concept agent.",
		"instructions": map[string]any{"path": "instructions/writer.md", "descriptor": descriptor(agentsuite.MediaTypeInstructions, instructions)},
		"model":        map[string]any{"model": ExecutionModel, "protocol": ExecutionProtocol},
		"evals":        []any{map[string]any{"path": "evals/writer/index.json", "descriptor": descriptor(agentsuite.MediaTypeEvaluationIndex, index)}},
	})
	// An empty catalog: the proof-of-concept agent uses no tool providers.
	catalog := canonical(map[string]any{
		"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeToolProviderCatalog, "toolProviders": []any{},
	})
	platform := map[string]any{"os": "linux", "architecture": "amd64"}
	// image returns a placeholder image reference with a fixed, made-up digest ("000...1" or "000...2").
	// Nothing is pulled; the digest only has to be well formed and stable so the fixture stays deterministic.
	image := func(name string) map[string]any {
		d := strings.Repeat("0", 63) + map[string]string{"runtime-base": "1", "harness": "2"}[name]
		return map[string]any{
			"platform": platform,
			"imageRef": "registry.example/agentweaver/" + name + "@sha256:" + d,
			"image":    map[string]any{"mediaType": "application/vnd.oci.image.manifest.v1+json", "digest": "sha256:" + d, "size": 1},
		}
	}
	// sourceEpoch is a fixed timestamp (2026-01-01 UTC) rather than the current time, so output never changes.
	profile := canonical(map[string]any{
		"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeBuildProfile,
		"id": "default", "sourceEpoch": 1767225600,
		"runtimeBase": []any{image("runtime-base")}, "harness": []any{image("harness")},
	})
	composition := canonical(map[string]any{
		"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeComposition,
		"agent": "writer", "platform": platform, "buildProfile": "default", "toolProviders": []any{},
	})
	// The pinned validator does not require the root agentsuite.json to be canonical, so it is written indented
	// for readability. Go's encoder sorts map keys, so this output is still deterministic.
	suite, err := json.MarshalIndent(map[string]any{
		"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeSuite, "name": "agentweaver-poc",
		"agents":              []any{map[string]any{"id": "writer", "path": "agents/writer.json", "descriptor": descriptor(agentsuite.MediaTypeAgent, agent)}},
		"toolProviderCatalog": map[string]any{"id": "catalog", "path": "tool-providers/catalog.json", "descriptor": descriptor(agentsuite.MediaTypeToolProviderCatalog, catalog)},
		"compositions": []any{map[string]any{
			"agent": "writer", "platform": platform, "path": "compositions/writer-linux-amd64.json",
			"descriptor": descriptor(agentsuite.MediaTypeComposition, composition),
		}},
		"buildProfiles": []any{map[string]any{"id": "default", "path": "build-profiles/default.json", "descriptor": descriptor(agentsuite.MediaTypeBuildProfile, profile)}},
	}, "", "  ")
	if err != nil {
		panic(err)
	}
	files = append(files,
		File{Path: "agentsuite.json", Data: append(suite, '\n')},
		File{Path: "agents/writer.json", Data: agent},
		File{Path: "instructions/writer.md", Data: instructions},
		File{Path: "tool-providers/catalog.json", Data: catalog},
		File{Path: "compositions/writer-linux-amd64.json", Data: composition},
		File{Path: "build-profiles/default.json", Data: profile},
		File{Path: "evals/writer/index.json", Data: index},
	)
	return files.withDirectories()
}

func (c Content) withDirectories() Content {
	// Add an explicit directory entry for every parent folder, once each, so the tar has a complete tree.
	seen := map[string]bool{}
	out := Content{}
	for _, f := range c {
		// Walk up from the file's folder to the root ("."), adding each folder not seen yet.
		for dir := filepath.ToSlash(filepath.Dir(f.Path)); dir != "."; dir = filepath.ToSlash(filepath.Dir(dir)) {
			if !seen[dir] {
				seen[dir] = true
				out = append(out, File{Path: dir, Type: tar.TypeDir, Mode: 0o755})
			}
		}
		out = append(out, f)
	}
	// Sort by path so the tar entry order, and therefore the layer digest, does not depend on build order.
	sort.SliceStable(out, func(i, j int) bool { return out[i].Path < out[j].Path })
	return out
}

// Replace returns a copy of c with data at path replaced.
func (c Content) Replace(path string, data []byte) Content {
	// Copy the slice so the shared base content is not changed for other variants.
	// Only the File values are copied; Data slices are replaced, never written in place.
	out := append(Content(nil), c...)
	for i := range out {
		if out[i].Path == path {
			out[i].Data = data
		}
	}
	return out
}

// Get returns the data stored at path.
func (c Content) Get(path string) []byte {
	for _, f := range c {
		if f.Path == path {
			return f.Data
		}
	}
	// A missing path is a bug in the fixture code, so fail loudly.
	panic("missing " + path)
}

func (c Content) remove(path string) Content {
	out := Content{}
	for _, f := range c {
		if f.Path != path {
			out = append(out, f)
		}
	}
	return out
}

// Tar writes a deterministic tar+gzip content layer.
func (c Content) Tar() []byte {
	var raw bytes.Buffer
	writer := tar.NewWriter(&raw)
	for _, f := range c {
		// Fixed ModTime (Unix epoch) and no owner or user names, so the same content always gives the same bytes.
		header := &tar.Header{Name: f.Path, Mode: f.Mode, ModTime: time.Unix(0, 0), Format: tar.FormatPAX, Linkname: f.Link}
		switch f.Type {
		case tar.TypeDir:
			header.Typeflag = tar.TypeDir
			header.Name += "/"
		// Links carry no data; only Linkname matters. Invalid variants use this to add an escaping symlink.
		case tar.TypeSymlink, tar.TypeLink:
			header.Typeflag = f.Type
		default:
			header.Typeflag = tar.TypeReg
			header.Size = int64(len(f.Data))
			// Mode 0 means "not set"; default regular files to rw-r--r--.
			if header.Mode == 0 {
				header.Mode = 0o644
			}
		}
		must(writer.WriteHeader(header))
		if header.Typeflag == tar.TypeReg {
			_, err := writer.Write(f.Data)
			must(err)
		}
	}
	must(writer.Close())
	// The gzip header has no name or time set, so compression is deterministic too.
	var compressed bytes.Buffer
	gz := gzip.NewWriter(&compressed)
	_, err := gz.Write(raw.Bytes())
	must(err)
	must(gz.Close())
	return compressed.Bytes()
}

// Layout describes how to write an OCI image layout.
type Layout struct {
	// Config is the config blob. The spec requires exactly the two bytes "{}".
	Config       []byte
	ArtifactType string
	LayerType    string
	// ExtraLayer lists the content layer twice; a suite must have exactly one layer.
	ExtraLayer   bool
	// RefName is the tag. It lives only in index.json, so changing it does not change the manifest digest.
	RefName      string
	// CorruptLayer stores different bytes than the layer descriptor's digest promises.
	CorruptLayer bool
	// WrongSize makes the layer descriptor's size one byte larger than the stored blob.
	WrongSize    bool
}

// DefaultLayout is the valid native AgentSuite OCI envelope.
func DefaultLayout() Layout {
	return Layout{Config: []byte("{}"), ArtifactType: agentsuite.MediaTypeArtifact, LayerType: agentsuite.MediaTypeContent, RefName: "v1"}
}

type ociDescriptor struct {
	MediaType    string            `json:"mediaType"`
	Digest       string            `json:"digest"`
	Size         int64             `json:"size"`
	ArtifactType string            `json:"artifactType,omitempty"`
	Annotations  map[string]string `json:"annotations,omitempty"`
}

// WriteLayout writes layer as an OCI image layout at root and returns the manifest digest.
func WriteLayout(root string, layer []byte, l Layout) (string, error) {
	if err := os.MkdirAll(filepath.Join(root, "blobs", "sha256"), 0o755); err != nil {
		return "", err
	}
	// blob stores data under blobs/sha256/<hex digest>, the standard OCI content-addressed path,
	// and returns its descriptor.
	blob := func(mediaType string, data []byte) (ociDescriptor, error) {
		d := ociDescriptor{MediaType: mediaType, Digest: digest(data), Size: int64(len(data))}
		return d, os.WriteFile(filepath.Join(root, "blobs", "sha256", strings.TrimPrefix(d.Digest, "sha256:")), data, 0o644)
	}
	config, err := blob(agentsuite.MediaTypeEmptyConfig, l.Config)
	if err != nil {
		return "", err
	}
	layerDescriptor, err := blob(l.LayerType, layer)
	if err != nil {
		return "", err
	}
	// Overwrite the stored layer with one flipped byte. The descriptor keeps the original digest and size,
	// so only a digest check can catch it.
	if l.CorruptLayer {
		corrupt := append([]byte(nil), layer...)
		corrupt[len(corrupt)-1] ^= 0xff
		if err := os.WriteFile(filepath.Join(root, "blobs", "sha256", strings.TrimPrefix(layerDescriptor.Digest, "sha256:")), corrupt, 0o644); err != nil {
			return "", err
		}
	}
	if l.WrongSize {
		layerDescriptor.Size++
	}
	layers := []ociDescriptor{layerDescriptor}
	if l.ExtraLayer {
		layers = append(layers, layerDescriptor)
	}
	// The manifest names the artifact type, the config, and the layers. Its digest is the artifact identity.
	manifest, err := json.Marshal(map[string]any{
		"schemaVersion": 2, "mediaType": "application/vnd.oci.image.manifest.v1+json",
		"artifactType": l.ArtifactType, "config": config, "layers": layers,
	})
	if err != nil {
		return "", err
	}
	manifestDescriptor, err := blob("application/vnd.oci.image.manifest.v1+json", manifest)
	if err != nil {
		return "", err
	}
	manifestDescriptor.ArtifactType = l.ArtifactType
	// The tag is an annotation on the index entry, outside the manifest bytes, so retagging keeps the digest.
	if l.RefName != "" {
		manifestDescriptor.Annotations = map[string]string{"org.opencontainers.image.ref.name": l.RefName}
	}
	index, err := json.Marshal(map[string]any{
		"schemaVersion": 2, "mediaType": "application/vnd.oci.image.index.v1+json", "manifests": []ociDescriptor{manifestDescriptor},
	})
	if err != nil {
		return "", err
	}
	if err := os.WriteFile(filepath.Join(root, "oci-layout"), []byte(`{"imageLayoutVersion":"1.0.0"}`), 0o644); err != nil {
		return "", err
	}
	return manifestDescriptor.Digest, os.WriteFile(filepath.Join(root, "index.json"), index, 0o644)
}

// Variant is one fixture layout and the expected upstream verdict.
type Variant struct {
	Name    string
	Content Content
	Layout  Layout
}

// Verdict is the pinned upstream validator result for one variant.
type Verdict struct {
	Name           string `json:"name"`
	Accepted       bool   `json:"accepted"`
	Error          string `json:"error,omitempty"`
	ManifestDigest string `json:"manifestDigest"`
}

// Variants returns the valid artifact followed by invalid and identity-changing variants.
func Variants() []Variant {
	base := Build()
	layout := DefaultLayout()
	// with returns a copy of the default layout with one change applied. Layout is a value, so the
	// shared default is never changed.
	with := func(mutate func(*Layout)) Layout { l := layout; mutate(&l); return l }
	// pretty re-encodes one file as indented JSON. The value is the same but the bytes are not canonical.
	// Descriptors still hold the old digest, so the validator also sees a digest mismatch.
	pretty := func(path string) Content {
		var value any
		must(json.Unmarshal(base.Get(path), &value))
		data, err := json.MarshalIndent(value, "", "  ")
		must(err)
		return base.Replace(path, data)
	}
	// edit applies a text change to one file. Text edits are used because they can create JSON that a
	// Go map cannot represent, such as a duplicate key.
	edit := func(path string, change func(string) string) Content {
		return base.Replace(path, []byte(change(string(base.Get(path)))))
	}
	// withEntry adds one extra archive entry to a copy of the base content.
	withEntry := func(f File) Content { return append(append(Content(nil), base...), f) }
	return []Variant{
		// Accepted: the unchanged suite.
		{Name: "valid", Content: base, Layout: layout},
		// Accepted: same manifest with a different tag. The test checks that its manifest digest equals "valid".
		{Name: "valid-retagged", Content: base, Layout: with(func(l *Layout) { l.RefName = "latest" })},
		// Rejected envelope cases. "{ }" has the same JSON value as "{}" but not the exact required bytes.
		{Name: "config-not-empty", Content: base, Layout: with(func(l *Layout) { l.Config = []byte("{ }") })},
		{Name: "wrong-artifact-type", Content: base, Layout: with(func(l *Layout) { l.ArtifactType = "application/vnd.example.other.v1" })},
		// A generic OCI image layer type instead of the AgentSuite content layer type.
		{Name: "wrong-layer-type", Content: base, Layout: with(func(l *Layout) { l.LayerType = "application/vnd.oci.image.layer.v1.tar+gzip" })},
		{Name: "two-layers", Content: base, Layout: with(func(l *Layout) { l.ExtraLayer = true })},
		{Name: "layer-digest-mismatch", Content: base, Layout: with(func(l *Layout) { l.CorruptLayer = true })},
		{Name: "layer-size-mismatch", Content: base, Layout: with(func(l *Layout) { l.WrongSize = true })},
		// Rejected content cases. Normative documents must be canonical JSON.
		{Name: "noncanonical-agent", Content: pretty("agents/writer.json"), Layout: layout},
		// Canonical keys are sorted, so the agent JSON starts with "description". Inserting "id" there
		// repeats the existing "id" key.
		{Name: "agent-duplicate-key", Content: edit("agents/writer.json", func(s string) string {
			return strings.Replace(s, `{"description"`, `{"id":"writer","description"`, 1)
		}), Layout: layout},
		// The root document still references the agent file, but the file is gone.
		{Name: "missing-agent-file", Content: base.remove("agents/writer.json"), Layout: layout},
		// The instructions bytes no longer match the digest recorded in the agent manifest.
		{Name: "instructions-mismatch", Content: base.Replace("instructions/writer.md", []byte("Changed.\n")), Layout: layout},
		// The first "agent" key in the indented root is in the composition entry. It now names an agent
		// that the suite does not declare.
		{Name: "composition-missing-agent", Content: edit("agentsuite.json", func(s string) string {
			return strings.Replace(s, `"agent": "writer"`, `"agent": "reviewer"`, 1)
		}), Layout: layout},
		// The first "id": "default" in the root is the build profile entry. Renaming it leaves the
		// composition pointing at a "default" profile that no longer exists.
		{Name: "missing-build-profile", Content: edit("agentsuite.json", func(s string) string {
			return strings.Replace(s, `"id": "default"`, `"id": "other"`, 1)
		}), Layout: layout},
		// Rejected archive safety cases: a path that leaves the archive root, two paths that differ only by
		// letter case, a symlink that points outside the archive, and a file anyone can write.
		{Name: "unsafe-path", Content: withEntry(File{Path: "../escape.txt", Data: []byte("x")}), Layout: layout},
		{Name: "case-collision", Content: withEntry(File{Path: "Agents/Writer.json", Data: []byte("{}")}), Layout: layout},
		{Name: "symlink-escape", Content: withEntry(File{Path: "agents/link", Type: tar.TypeSymlink, Link: "../../etc/passwd", Mode: 0o777}), Layout: layout},
		{Name: "world-writable", Content: withEntry(File{Path: "notes.txt", Data: []byte("x"), Mode: 0o666}), Layout: layout},
	}
}

// Write writes every variant layout below root and returns upstream verdicts.
func Write(root string) ([]Verdict, error) {
	var verdicts []Verdict
	// The order is fixed (single-agent variants, then team variants), so parity.json is stable.
	for _, variant := range append(Variants(), TeamVariants()...) {
		dir := filepath.Join(root, variant.Name)
		// Start from an empty folder so files left by an older run cannot change the result.
		if err := os.RemoveAll(dir); err != nil {
			return nil, err
		}
		manifestDigest, err := WriteLayout(dir, variant.Content.Tar(), variant.Layout)
		if err != nil {
			return nil, err
		}
		// The pinned upstream validator decides the verdict; this code never decides it.
		verdict := Verdict{Name: variant.Name, Accepted: true, ManifestDigest: manifestDigest}
		if _, err := agentsuite.ValidatePath(dir); err != nil {
			verdict.Accepted = false
			// A validation error can list several problems, one per line. Sort the lines so the recorded
			// text does not depend on the order in which the validator found them.
			lines := strings.Split(err.Error(), "\n")
			sort.Strings(lines)
			verdict.Error = strings.Join(lines, "\n")
		}
		verdicts = append(verdicts, verdict)
	}
	return verdicts, nil
}

func must(err error) {
	if err != nil {
		panic(fmt.Sprint(err))
	}
}
