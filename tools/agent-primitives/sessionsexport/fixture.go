package sessionsexport

import (
	"context"
	"fmt"
	"strconv"

	"github.com/agent-primitives/sessions/api"
	"github.com/agent-primitives/sessions/controller"
	"github.com/agent-primitives/sessions/harness/echoagent"
	"github.com/agent-primitives/sessions/sqlitelog"
)

// Fixture session names used by the committed reference export.
const (
	FixtureParent = "agentweaver-fixture-parent"
	FixtureChild  = "agentweaver-fixture-child"
)

// FixtureConfig is the opaque Start config the fixture records. The cursor is above 2^53 so the
// export proves exact int64 handling.
var (
	FixtureConfig       = []byte(`{"profile":"agentweaver-p2-poc-fixture"}`)
	FixtureResumeCursor = int64(9007199254740993)
)

// BuildFixture runs the pinned reference controller against the stateless echo harness, stores two
// parent turns in sqlite at dbPath, forks after the first completed turn, and runs one child-only turn.
// It returns the fork boundary. The model is deterministic and local; no provider is called.
func BuildFixture(ctx context.Context, dbPath string) (forkSeq int64, err error) {
	store, err := sqlitelog.Open(dbPath)
	if err != nil {
		return 0, err
	}
	defer func() {
		if closeErr := store.Close(); err == nil {
			err = closeErr
		}
	}()
	parent := store.Session(FixtureParent)
	for _, text := range []string{"first", "second"} {
		live, err := controller.New(parent, echoagent.Model, controller.WithStart(FixtureConfig, FixtureResumeCursor), controller.WithSessionUID(FixtureParent))
		if err != nil {
			return 0, err
		}
		head, err := parent.Head()
		if err != nil {
			return 0, err
		}
		if err := live.Exec(ctx, echoagent.Harness{}, []api.Message{*api.TextMessage("user", text)}, head); err != nil {
			return 0, fmt.Errorf("parent turn %q: %w", text, err)
		}
		if forkSeq == 0 {
			if forkSeq, err = parent.Head(); err != nil {
				return 0, err
			}
		}
	}
	child := store.Session(FixtureChild)
	if err := controller.Fork(parent, child, forkSeq); err != nil {
		return 0, err
	}
	next, err := controller.New(child, echoagent.Model, controller.WithStart(FixtureConfig, 0), controller.WithSessionUID(FixtureChild))
	if err != nil {
		return 0, err
	}
	head, err := child.Head()
	if err != nil {
		return 0, err
	}
	if err := next.Exec(ctx, echoagent.Harness{}, []api.Message{*api.TextMessage("user", "child")}, head); err != nil {
		return 0, fmt.Errorf("child turn: %w", err)
	}
	if err := parent.Verify(); err != nil {
		return 0, err
	}
	return forkSeq, child.Verify()
}

// ExportFixture exports the parent and child produced by BuildFixture with producer metadata kept
// outside the hashed records.
func ExportFixture(dbPath string, forkSeq int64) (parent, child Document, err error) {
	producer := func(run string) *Producer {
		return &Producer{System: "agentweaver", References: map[string]string{"projectId": "fixture-project", "runId": run}}
	}
	origin := &Origin{Source: "agentweaver", Subject: "fixture-project"}
	if parent, err = Export(dbPath, FixtureParent, Options{Origin: origin, Producer: producer("fixture-parent-run")}); err != nil {
		return
	}
	child, err = Export(dbPath, FixtureChild, Options{
		Parent:   &Parent{UID: FixtureParent, ForkSeq: strconv.FormatInt(forkSeq, 10)},
		Origin:   origin,
		Producer: producer("fixture-child-run"),
	})
	return
}
