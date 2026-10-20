package sessionsexport_test

import (
	"encoding/base64"
	"path/filepath"
	"strconv"
	"testing"

	"google.golang.org/protobuf/proto"

	v1 "github.com/agent-primitives/sessions/api/genpb"
	"github.com/agent-primitives/sessions/canon"
	"github.com/agent-primitives/sessions/eventlog"
	"github.com/agent-primitives/sessions/wire"

	"github.com/sabbour/agentweaver/tools/agent-primitives/sessionsexport"
)

func TestExportPreservesStoredParentAndChildChains(t *testing.T) {
	db := filepath.Join(t.TempDir(), "journal.db")
	forkSeq, err := sessionsexport.BuildFixture(t.Context(), db)
	if err != nil {
		t.Fatal(err)
	}
	parent, child, err := sessionsexport.ExportFixture(db, forkSeq)
	if err != nil {
		t.Fatal(err)
	}
	for _, doc := range []sessionsexport.Document{parent, child} {
		if doc.Upstream.Commit != sessionsexport.SourceCommit || doc.Upstream.Contract != "0.2.0-draft" {
			t.Fatalf("unexpected upstream pin %+v", doc.Upstream)
		}
		if err := eventlog.VerifyChain("", decode(t, doc)); err != nil {
			t.Fatalf("%s: reference verifier rejected export: %v", doc.Session.UID, err)
		}
		if doc.SourceHead.Hash != doc.Records[len(doc.Records)-1].ContentHash {
			t.Fatalf("%s: source head does not match last stored record", doc.Session.UID)
		}
	}
	// The child copies the parent prefix with identical seq, prev_hash, content_hash, and event bytes.
	for i := int64(0); i < forkSeq; i++ {
		p, c := parent.Records[i], child.Records[i]
		if p.Seq != c.Seq || p.PrevHash != c.PrevHash || p.ContentHash != c.ContentHash || p.Event != c.Event {
			t.Fatalf("fork prefix differs at seq %d", i+1)
		}
	}
	marker := decode(t, child)[forkSeq]
	if marker.Event.Lifecycle == nil || marker.Event.Lifecycle.Detail != "parent@"+strconv.FormatInt(forkSeq, 10) {
		t.Fatalf("child seq %d is not the FORK marker: %+v", forkSeq+1, marker.Event)
	}
	if child.Session.Parent == nil || child.Session.Parent.ForkSeq != strconv.FormatInt(forkSeq, 10) {
		t.Fatalf("child lineage metadata = %+v", child.Session.Parent)
	}
}

func decode(t *testing.T, doc sessionsexport.Document) []eventlog.Record {
	t.Helper()
	var recs []eventlog.Record
	for _, r := range doc.Records {
		blob, err := base64.StdEncoding.DecodeString(r.Event)
		if err != nil {
			t.Fatal(err)
		}
		var ev v1.Event
		if err := proto.Unmarshal(blob, &ev); err != nil {
			t.Fatal(err)
		}
		seq, _ := strconv.ParseInt(r.Seq, 10, 64)
		event := wire.EventFromProto(&ev)
		hash, err := canon.HashRecord(r.PrevHash, seq, event)
		if err != nil {
			t.Fatal(err)
		}
		if hash != r.ContentHash {
			t.Fatalf("%s seq %d: stored hash %s, recomputed %s", doc.Session.UID, seq, r.ContentHash, hash)
		}
		recs = append(recs, eventlog.Record{Seq: seq, PrevHash: r.PrevHash, Hash: r.ContentHash, Event: event})
	}
	return recs
}
