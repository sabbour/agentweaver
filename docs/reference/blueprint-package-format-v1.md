# Blueprint package format v1

Blueprint package v1 is a **definitions-only** interchange contract. It describes reusable
blueprints, roles, workflows, and skills; it does not define an archive format, export/import
workflow, persistence model, execution state, secrets, credentials, run history, or files outside
the package definitions. Archive and storage implementations must validate this contract before
using it.

## Manifest and layout

The manifest is UTF-8 strict JSON at `manifest.json`. Its schema identifier is
`https://agentweaver.dev/schemas/blueprint-package-v1.json`, and `schema_version` is exactly `"1"`.
The exact schema is exposed by `BlueprintPackageSchema.Json`.

`manifest.json` is deliberately **not** an inventory entry or payload. The only payload paths are:

| Kind | Required path |
| --- | --- |
| `blueprint` | `definitions/blueprints/{id}.json` |
| `role` | `definitions/roles/{id}.json` |
| `workflow` | `definitions/workflows/{id}.yaml` |
| `skill` | `definitions/skills/{id}.md` |

`id` and the package id use lower-case kebab case. Paths cannot contain backslashes, traversal,
alternate extensions, or unlisted files. Every inventory entry must have one payload, and every
payload must have exactly one inventory entry.

```json
{
  "schema_version": "1",
  "package": { "id": "engineering", "version": "1.2.0" },
  "compatibility": { "minimum_agentweaver_version": "0.9.67" },
  "provenance": {
    "source": "catalog",
    "producer": "agentweaver.catalog",
    "repository": "https://github.com/example/blueprints",
    "revision": "0123456789abcdef"
  },
  "definitions": [
    {
      "kind": "blueprint",
      "id": "engineering",
      "path": "definitions/blueprints/engineering.json",
      "size": 42,
      "sha256": "..."
    }
  ]
}
```

The schema rejects unknown fields. Runtime validation also rejects duplicate JSON property names,
malformed UTF-8, unpaired escaped Unicode surrogates, duplicate `kind`/`id` or path entries, and
incompatible version ranges.

## Versions, limits, and provenance

Package and compatibility versions are SemVer 2.0.0. Numeric identifiers are compared as digit
strings, not as 32- or 64-bit numbers, so comparison is unbounded. A compatibility minimum cannot
be greater than its optional maximum.

Limits are 1 MiB manifest bytes, 256 definitions, 1 MiB per payload, 16 MiB total payload bytes,
240 path characters, and 64 identifier characters. Every JSON numeric token in the manifest and
JSON blueprint or role payloads is limited to 4,096 characters (including sign, decimal point, and
exponent) before exact canonicalization. This fail-closed bound prevents oversized exponents from
amplifying canonicalization work while preserving exact decimal semantics within the limit. The
optional provenance object is bounded to
the `catalog`, `generated`, or `imported` source, a bounded producer token, an HTTPS repository,
a lower-case hexadecimal revision, and an RFC 3339 timestamp.

## Digests and byte preservation

`BlueprintPackageValidator` preserves the supplied manifest byte sequence and returns:

| Digest | Definition |
| --- | --- |
| `RawManifestSha256` | SHA-256 of the exact supplied `manifest.json` bytes. No parse/serialize round trip occurs. |
| `PayloadSetSha256` | SHA-256 of sorted path UTF-8 bytes and exact raw payload bytes, each length-prefixed with an unsigned 64-bit big-endian length. |
| `SemanticSha256` | SHA-256 of package identity, compatibility, sorted definitions, and canonical payload semantics. JSON object keys are sorted; JSON numbers are normalized from their lexical tokens without floating-point conversion. Text payload line endings are normalized to LF. |
| `ContainerSha256` | Optional transport-provided SHA-256. It is compared with the optional manifest declaration but v1 does not define a container/archive format. |

Provenance, raw formatting, inventory hash fields, and optional container metadata do not alter the
semantic digest. Payload content changes do. This distinction allows reproducible semantic identity
while retaining exact raw-manifest integrity.

## Validation API

Use `BlueprintPackageValidator.Validate(BlueprintPackageSource)`. Supply raw manifest bytes and the
path-to-byte payload set. The source copies these inputs, and successful results expose immutable
manifest, byte, and collection snapshots. `CalculatePayloadSetDigest` accepts the same immutable
path-to-byte payload set and hashes the exact bytes; it does not trust inventory metadata. Validation
is pure: it performs no archive extraction, file I/O, network access, or persistence.
