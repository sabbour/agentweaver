// Package sessionsexport exports AgentSessions journal records exactly as the pinned reference host
// stored them. The export keeps each record's stored seq, prev_hash, content_hash, fence, and raw
// protobuf Event bytes. It never rehashes or repairs a record, so an independent verifier can detect
// tampering instead of receiving a silently rebuilt chain.
package sessionsexport

import (
	"database/sql"
	"encoding/base64"
	"errors"
	"fmt"
	"strconv"

	// The pinned reference store uses the pure-Go modernc.org/sqlite driver.
	_ "modernc.org/sqlite"
)

// Pinned upstream identity. These values change only when Agentweaver deliberately adopts a new
// AgentSessions source commit and contract version.
const (
	Format           = "agentweaver.agentsessions.export.v1"
	SourceRepository = "https://github.com/agent-primitives/sessions"
	SourceCommit     = "465e82175da7815db39ec14852deb3949ff82670"
	ContractVersion  = "0.2.0-draft"
)

// Document is one exported session. Only Records carry hashed data. Session, Producer, and
// SourceHead are unhashed metadata and do not authenticate the producer.
type Document struct {
	Format     string    `json:"format"`
	Upstream   Upstream  `json:"upstream"`
	Session    Session   `json:"session"`
	Producer   *Producer `json:"producer"`
	SourceHead Head      `json:"source_head"`
	Records    []Record  `json:"records"`
}

// Upstream pins the reference source and contract that produced the records.
type Upstream struct {
	Repository string `json:"repository"`
	Commit     string `json:"commit"`
	Contract   string `json:"contract"`
}

// Session is the upstream session identity and lineage. Identity is provenance, not authorization.
type Session struct {
	UID    string  `json:"uid"`
	Parent *Parent `json:"parent,omitempty"`
	Origin *Origin `json:"origin,omitempty"`
}

// Parent names the session and completed boundary a fork child was copied from.
type Parent struct {
	UID     string `json:"uid"`
	ForkSeq string `json:"fork_seq"`
}

// Origin mirrors the public agentsessions.v1.Origin message for producer context.
type Origin struct {
	Source     string            `json:"source,omitempty"`
	Subject    string            `json:"subject,omitempty"`
	URI        string            `json:"uri,omitempty"`
	Attributes map[string]string `json:"attributes,omitempty"`
}

// Producer keeps Agentweaver native identifiers next to, not inside, the upstream records.
type Producer struct {
	System     string            `json:"system"`
	References map[string]string `json:"references,omitempty"`
}

// Head is the source store head observed at export time. It is not an external anchor.
type Head struct {
	Seq  string `json:"seq"`
	Hash string `json:"hash"`
}

// Record is one stored LogRecord. Int64 values are decimal strings, and Event is the base64 of the
// stored agentsessions.v1.Event protobuf bytes.
type Record struct {
	Seq         string `json:"seq"`
	PrevHash    string `json:"prev_hash"`
	ContentHash string `json:"content_hash"`
	Fence       string `json:"fence"`
	Event       string `json:"event"`
}

// Options supplies the unhashed metadata attached to an export.
type Options struct {
	Parent   *Parent
	Origin   *Origin
	Producer *Producer
}

// ErrEmpty reports a session that has no stored records.
var ErrEmpty = errors.New("sessionsexport: session has no records")

// Export reads one session from a reference sqlite journal without decoding or rehashing records.
func Export(path, sessionUID string, opts Options) (Document, error) {
	db, err := sql.Open("sqlite", "file:"+path+"?mode=ro")
	if err != nil {
		return Document{}, err
	}
	defer db.Close()
	rows, err := db.Query(`SELECT seq, prev_hash, hash, fence, event FROM events WHERE session = ? ORDER BY seq`, sessionUID)
	if err != nil {
		return Document{}, fmt.Errorf("sessionsexport: read %s: %w", sessionUID, err)
	}
	defer rows.Close()
	doc := Document{
		Format:   Format,
		Upstream: Upstream{Repository: SourceRepository, Commit: SourceCommit, Contract: ContractVersion},
		Session:  Session{UID: sessionUID, Parent: opts.Parent, Origin: opts.Origin},
		Producer: opts.Producer,
		Records:  []Record{},
	}
	for rows.Next() {
		var seq, fence int64
		var prev, hash string
		var blob []byte
		if err := rows.Scan(&seq, &prev, &hash, &fence, &blob); err != nil {
			return Document{}, err
		}
		doc.Records = append(doc.Records, Record{
			Seq:         strconv.FormatInt(seq, 10),
			PrevHash:    prev,
			ContentHash: hash,
			Fence:       strconv.FormatInt(fence, 10),
			Event:       base64.StdEncoding.EncodeToString(blob),
		})
	}
	if err := rows.Err(); err != nil {
		return Document{}, err
	}
	if len(doc.Records) == 0 {
		return Document{}, ErrEmpty
	}
	last := doc.Records[len(doc.Records)-1]
	doc.SourceHead = Head{Seq: last.Seq, Hash: last.ContentHash}
	return doc, nil
}
