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

The distributed schema names the Agentweaver metaschema
`https://agentweaver.dev/metaschemas/blueprint-package-v1`. Its required
`https://agentweaver.dev/vocab/blueprint-package-v1` vocabulary declares these assertions:
`x-agentweaver-canonical-definition-path`, `x-agentweaver-https-repository-uri`, and
`x-agentweaver-rfc3339-timestamp`. They require the definition path to equal the path in the table
for its `kind` and `id`, and enforce the repository URI and timestamp profiles below.

An implementation that supports this metaschema and required vocabulary must evaluate all three
keywords. A generic Draft 2020-12 validator that does not support the vocabulary must fail schema
loading or report that it cannot validate this contract; it must not claim that unknown keywords
were enforced. Such consumers must use `BlueprintPackageSchema.ValidateCustomKeywords` (or
`BlueprintPackageValidator.Validate`, which invokes it) in addition to their standard validation.

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
the `catalog`, `generated`, or `imported` source, a bounded producer token, an absolute HTTPS
repository URI with RFC 3986 percent escapes, a lower-case hexadecimal revision, and a timestamp.
Repository URIs use ASCII DNS names with nonempty letter-or-digit-bounded labels (or bracketed
IPv6), reject credentials, whitespace, controls, malformed percent escapes, invalid domains, and
ports outside 0 through 65535. The timestamp profile is an exact proleptic Gregorian RFC 3339
date and time with seconds `00` through `59`, an optional unbounded decimal fraction, and either
`Z` or a numeric offset from `-14:00` through `+14:00`; `14:00` is the only permitted offset at
hour 14. Leap seconds and year `0000` are not supported. The timestamp is validated textually and
preserved as supplied, so valid precision beyond `DateTimeOffset` ticks (for example,
`9999-12-31T23:59:59.99999999Z`) is accepted without rounding.

Before hashing, decoding, parsing, or canonicalizing any payload, validation preflights the
definition count, every declared and raw payload size, and the aggregate raw payload bytes. A
payload set over 16 MiB fails without inspecting its payload contents.

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
is pure: it performs no archive extraction, file I/O, network access, or persistence. The public
payload-set helper uses strict UTF-8 for path strings and rejects invalid UTF-16 rather than
replacement-encoding it before hashing.
