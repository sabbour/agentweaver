ALTER TABLE {schema}.maf_runtime_evidence_receipts
    DROP CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check;

ALTER TABLE {schema}.maf_runtime_evidence_receipts
    ADD CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check CHECK (
        receipt_kind IN (
            'numeric-limit-denial',
            'replacement-comparison',
            'numeric-limit-native-no-send',
            'native-root-turn-completion',
            'native-root-turn-replay',
            'checkpoint-recovery-supersession'));

ALTER TABLE {schema}.maf_runtime_evidence_receipts
    ADD CONSTRAINT maf_runtime_evidence_supersession_fields_check CHECK (
        receipt_kind <> 'checkpoint-recovery-supersession' OR (
            receipt_json->>'supersedesOperationId' IS NOT NULL AND
            receipt_json->>'successorOperationId' IS NOT NULL AND
            receipt_json->>'supersedesOperationId' = attempt_id::text AND
            receipt_json->>'supersedesOperationId' <> receipt_json->>'successorOperationId'));

CREATE UNIQUE INDEX uq_checkpoint_recovery_supersession_source
    ON {schema}.maf_runtime_evidence_receipts
        (project_id, run_id, ((receipt_json->>'supersedesOperationId')::uuid))
    WHERE receipt_kind = 'checkpoint-recovery-supersession';

CREATE UNIQUE INDEX uq_checkpoint_recovery_supersession_successor
    ON {schema}.maf_runtime_evidence_receipts
        (project_id, run_id, ((receipt_json->>'successorOperationId')::uuid))
    WHERE receipt_kind = 'checkpoint-recovery-supersession';
