BEGIN;

CREATE TABLE foundation_probe.probe_effects (
    effect_id uuid PRIMARY KEY,
    nonce char(32) NOT NULL UNIQUE,
    source_sha char(40) NOT NULL,
    source_tree char(40) NOT NULL,
    runtime_role name NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

GRANT SELECT, INSERT ON TABLE foundation_probe.probe_effects TO foundation_probe_runtime;

COMMIT;
