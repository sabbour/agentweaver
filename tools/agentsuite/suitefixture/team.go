package suitefixture

import (
	"encoding/json"

	agentsuite "github.com/agent-primitives/suite"
)

// TeamAgent describes one agent in the multi-agent team fixture.
type TeamAgent struct {
	ID           string
	Description  string
	Instructions string
	Model        map[string]any
	Invokes      []any
	Policy       map[string]any
	PolicyType   string
}

// TeamAgents returns the team fixture agents: a lead with a capability-based model, invokes, and a v2
// policy; a writer with an exact model and a v1 policy; and a reviewer with an exact model and no policy.
func TeamAgents() []TeamAgent {
	// "exact" pins a specific model ID. The lead instead describes required capabilities and lets the
	// runtime pick any matching model.
	exact := map[string]any{"model": ExecutionModel, "protocol": ExecutionProtocol}
	return []TeamAgent{
		{
			ID: "lead", Description: "Team lead", Instructions: "Split the request and hand off parts.\n",
			Model: map[string]any{"protocol": "openai-compatible", "capabilities": map[string]any{
				"api": "openai-chat-completions-v1", "contextTokens": 128000, "outputTokens": 4096, "streaming": true, "toolCalling": true,
			}},
			// The lead may hand work to the writer and the reviewer. maxConcurrent and maxDepth of 1 allow one
			// call at a time and one level of delegation.
			Invokes: []any{
				map[string]any{"agent": "writer", "maxConcurrent": 1, "maxDepth": 1},
				map[string]any{"agent": "reviewer", "maxConcurrent": 1, "maxDepth": 1},
			},
			// A v2 policy that allows egress to one address and port. Import records it but does not enforce it.
			Policy: map[string]any{
				"version":              "agentsuite.policy/v2",
				"network":              map[string]any{"egressAllow": []any{map[string]any{"address": "10.0.0.2", "protocol": "TCP", "port": 443}}},
				"requiredCapabilities": []any{"network/egress-allow/v1"},
			},
			PolicyType: agentsuite.MediaTypePolicyV2,
		},
		{
			ID: "writer", Description: "Writer", Instructions: Instructions, Model: exact,
			Policy: map[string]any{
				"version": "agentsuite.policy/v1", "denyWritePaths": []any{"/etc"},
				"requiredCapabilities": []any{"filesystem/deny-write/v1"},
			},
			PolicyType: agentsuite.MediaTypePolicyV1,
		},
		{ID: "reviewer", Description: "Reviewer", Instructions: "Check the draft for errors.\n", Model: exact},
	}
}

// remoteSchema returns a small canonical JSON Schema for a remote tool input or output.
func remoteSchema(property, kind string) []byte {
	return canonical(map[string]any{
		"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object", "additionalProperties": false,
		"required": []any{property}, "properties": map[string]any{property: map[string]any{"type": kind}},
	})
}

// BuildTeam returns the content layer of a multi-agent team suite. Its catalog retains one remote-only
// Streamable HTTP tool provider with a header secret reference and delegated-authorization tools. Remote
// providers have no bundled variant, so the pinned spec does not let a composition resolve them; they stay
// catalog-level declarations.
func BuildTeam(agents []TeamAgent) Content {
	var files Content
	platform := map[string]any{"os": "linux", "architecture": "amd64"}
	var agentRefs, compositionRefs []any
	// For each agent, build its files from the leaves upward: instructions and policy first, then the agent
	// manifest that pins their digests, then a composition that pins the agent.
	for _, a := range agents {
		instructions := []byte(a.Instructions)
		manifest := map[string]any{
			"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeAgent,
			"id": a.ID, "description": a.Description, "model": a.Model,
			"instructions": map[string]any{"path": "instructions/" + a.ID + ".md", "descriptor": descriptor(agentsuite.MediaTypeInstructions, instructions)},
		}
		// Optional fields are added only when set, so an agent without them has no empty key in its JSON.
		if a.Invokes != nil {
			manifest["invokes"] = a.Invokes
		}
		if a.Policy != nil {
			policy := canonical(a.Policy)
			path := "policies/" + a.ID + ".json"
			// The descriptor media type says whether this is a v1 or v2 policy document.
			manifest["policy"] = map[string]any{"path": path, "descriptor": descriptor(a.PolicyType, policy)}
			files = append(files, File{Path: path, Data: policy})
		}
		agent := canonical(manifest)
		// Each composition binds no tool providers, because the remote provider declared below cannot be bound.
		composition := canonical(map[string]any{
			"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeComposition,
			"agent": a.ID, "platform": platform, "buildProfile": "default", "toolProviders": []any{},
		})
		compositionPath := "compositions/" + a.ID + "-linux-amd64.json"
		agentRefs = append(agentRefs, map[string]any{"id": a.ID, "path": "agents/" + a.ID + ".json", "descriptor": descriptor(agentsuite.MediaTypeAgent, agent)})
		compositionRefs = append(compositionRefs, map[string]any{
			"agent": a.ID, "platform": platform, "path": compositionPath, "descriptor": descriptor(agentsuite.MediaTypeComposition, composition),
		})
		files = append(files,
			File{Path: "agents/" + a.ID + ".json", Data: agent},
			File{Path: "instructions/" + a.ID + ".md", Data: instructions},
			File{Path: compositionPath, Data: composition},
		)
	}

	// schemaRef stores a JSON Schema file and returns a reference to it. It appends to files as a side effect.
	schemaRef := func(name string, data []byte) map[string]any {
		path := "schemas/" + name + ".json"
		files = append(files, File{Path: path, Data: data})
		return map[string]any{"path": path, "descriptor": descriptor("application/schema+json", data)}
	}
	// delegated declares that a tool acts for the user with the given scope. The credential is supplied by
	// the runtime through a named binding; the package never holds the credential itself.
	delegated := func(scope string) map[string]any {
		return map[string]any{"service": "search", "scope": scope,
			"credential": map[string]any{"resolution": "runtime-brokered", "bindingRef": "search-delegated-connection"}}
	}
	// One remote MCP provider with two tools: a read-only search and a mutating action that needs approval.
	// The endpoint, header secret, and network destination are names ("...Ref"), not real values; the
	// environment resolves them later.
	provider := canonical(map[string]any{
		"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeToolProvider,
		"id": "search", "version": "1.0.0", "retained": true, "protocol": "mcp", "revision": "2025-06-18",
		"tools": []any{
			map[string]any{
				"name": "search", "inputSchema": schemaRef("search-input", remoteSchema("query", "string")),
				"effect": "read-only", "approval": "policy-controlled", "delegatedAuthorization": delegated("results.read"),
			},
			map[string]any{
				"name": "flag_result", "inputSchema": schemaRef("flag-result-input", remoteSchema("resultId", "string")),
				"effect": "mutating", "approval": "required", "delegatedAuthorization": delegated("results.flag"),
			},
		},
		"remote": map[string]any{
			"transport": "streamable-http", "endpointRef": "search-mcp-endpoint",
			"headers":      []any{map[string]any{"name": "Authorization", "secretRef": map[string]any{"name": "search-mcp-auth", "key": "token"}}},
			"network":      []any{map[string]any{"destinationRef": "search-mcp-egress"}},
			"timeouts":     map[string]any{"connectMilliseconds": 5000, "requestMilliseconds": 60000},
			"cancellation": "propagate", "connection": "session-aware",
		},
	})
	providerPath := "tool-providers/search/1.0.0/tool-provider.json"
	catalog := canonical(map[string]any{
		"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeToolProviderCatalog,
		"toolProviders": []any{map[string]any{
			"id": "search", "version": "1.0.0", "path": providerPath, "descriptor": descriptor(agentsuite.MediaTypeToolProvider, provider),
		}},
	})
	// Reuse the build profile from the single-agent suite so both fixtures share the same placeholder images.
	base := Build()
	profile := base.Get("build-profiles/default.json")
	suite, err := json.MarshalIndent(map[string]any{
		"schemaVersion": agentsuite.SpecVersion, "mediaType": agentsuite.MediaTypeSuite, "name": "agentweaver-team",
		"agents":              agentRefs,
		"toolProviderCatalog": map[string]any{"id": "catalog", "path": "tool-providers/catalog.json", "descriptor": descriptor(agentsuite.MediaTypeToolProviderCatalog, catalog)},
		"compositions":        compositionRefs,
		"buildProfiles":       []any{map[string]any{"id": "default", "path": "build-profiles/default.json", "descriptor": descriptor(agentsuite.MediaTypeBuildProfile, profile)}},
	}, "", "  ")
	must(err)
	files = append(files,
		File{Path: "agentsuite.json", Data: append(suite, '\n')},
		File{Path: "tool-providers/catalog.json", Data: catalog},
		File{Path: providerPath, Data: provider},
		File{Path: "build-profiles/default.json", Data: profile},
	)
	return files.withDirectories()
}

// TeamVariants returns the accepted team suite and a variant whose writer names a model endpoint variable and
// secret references. The pinned validator accepts both; Agentweaver refuses the second because packages never
// carry secret slots.
func TeamVariants() []Variant {
	agents := TeamAgents()
	// Call TeamAgents again for an independent copy, so changing withSecrets does not change agents.
	withSecrets := TeamAgents()
	// Index 1 is the writer. endpointEnv and secretRefs are allowed by the spec but name host secrets.
	withSecrets[1].Model = map[string]any{
		"model": ExecutionModel, "protocol": ExecutionProtocol, "endpointEnv": "WRITER_ENDPOINT", "secretRefs": []any{"writer-key"},
	}
	return []Variant{
		{Name: "team", Content: BuildTeam(agents), Layout: DefaultLayout()},
		{Name: "team-model-secret-refs", Content: BuildTeam(withSecrets), Layout: DefaultLayout()},
	}
}
