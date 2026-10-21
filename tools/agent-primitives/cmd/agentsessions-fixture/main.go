// Command agentsessions-fixture regenerates the committed AgentSessions parent/child export that the
// .NET verifier tests read. It runs only the pinned reference controller, a local deterministic model,
// and a temporary sqlite file. It makes no network or model-provider call.
//
//	go run ./cmd/agentsessions-fixture -out ../../tests/Agentweaver.Providers.Sessions.AgentSessions.Tests/Fixtures/AgentSessions
package main

import (
	"context"
	"encoding/json"
	"flag"
	"log"
	"os"
	"path/filepath"

	"github.com/sabbour/agentweaver/tools/agent-primitives/sessionsexport"
)

func main() {
	out := flag.String("out", ".", "directory that receives parent.json and child.json")
	flag.Parse()
	dir, err := os.MkdirTemp("", "agentsessions-fixture-")
	if err != nil {
		log.Fatal(err)
	}
	defer os.RemoveAll(dir)
	db := filepath.Join(dir, "journal.db")
	forkSeq, err := sessionsexport.BuildFixture(context.Background(), db)
	if err != nil {
		log.Fatal(err)
	}
	parent, child, err := sessionsexport.ExportFixture(db, forkSeq)
	if err != nil {
		log.Fatal(err)
	}
	if err := os.MkdirAll(*out, 0o755); err != nil {
		log.Fatal(err)
	}
	for name, doc := range map[string]sessionsexport.Document{"parent.json": parent, "child.json": child} {
		raw, err := json.MarshalIndent(doc, "", "  ")
		if err != nil {
			log.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(*out, name), append(raw, '\n'), 0o644); err != nil {
			log.Fatal(err)
		}
	}
	log.Printf("wrote parent (%d records) and child (%d records) forked at seq %d", len(parent.Records), len(child.Records), forkSeq)
}
