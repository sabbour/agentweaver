// Command agentsuite-fixture writes the AgentSuite import fixtures and the
// pinned upstream verdict for each one.
package main

import (
	"encoding/json"
	"flag"
	"log"
	"os"
	"path/filepath"

	"github.com/sabbour/agentweaver/tools/agent-primitives/suitefixture"
)

func main() {
	out := flag.String("out", "", "output directory")
	flag.Parse()
	if *out == "" {
		log.Fatal("-out is required")
	}
	verdicts, err := suitefixture.Write(*out)
	if err != nil {
		log.Fatal(err)
	}
	data, err := json.MarshalIndent(verdicts, "", "  ")
	if err != nil {
		log.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(*out, "parity.json"), append(data, '\n'), 0o644); err != nil {
		log.Fatal(err)
	}
}
