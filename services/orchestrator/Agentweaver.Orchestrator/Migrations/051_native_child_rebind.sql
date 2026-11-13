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
            'checkpoint-recovery-supersession',
            'native-child-rebind'));

ALTER TABLE {schema}.maf_runtime_evidence_receipts
    ADD CONSTRAINT maf_runtime_evidence_native_child_rebind_fields_check CHECK (
        receipt_kind <> 'native-child-rebind' OR ((
            receipt_json->>'dispatchId' IS NOT NULL AND
            receipt_json->>'dispatchId' ~
                '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' AND
            receipt_json->>'recoveryOperationId' IS NOT NULL AND
            receipt_json->>'recoveryOperationId' ~
                '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' AND
            receipt_json->>'destinationFence' IS NOT NULL AND
            receipt_json->>'destinationFence' ~ '^[1-9][0-9]*$' AND
            receipt_json ? 'priorReceiptId' AND
            receipt_json ? 'priorReceiptHash' AND
            receipt_json->>'priorAuthorityKey' IS NOT NULL AND
            (
                (receipt_json->>'priorAuthorityKey' = 'origin' AND
                 receipt_json->>'priorReceiptId' IS NULL AND
                 receipt_json->>'priorReceiptHash' IS NULL)
                OR
                (receipt_json->>'priorAuthorityKey' =
                    'receipt:' || (receipt_json->>'priorReceiptId') AND
                 receipt_json->>'priorReceiptId' ~
                   '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' AND
                 receipt_json->>'priorReceiptHash' ~ '^[0-9a-f]{64}$')
            ) IS TRUE));

CREATE UNIQUE INDEX uq_native_child_rebind_destination
    ON {schema}.maf_runtime_evidence_receipts
        (project_id, run_id, ((receipt_json->>'dispatchId')::uuid),
         ((receipt_json->>'destinationFence')::bigint))
    WHERE receipt_kind = 'native-child-rebind';

CREATE UNIQUE INDEX uq_native_child_rebind_prior
    ON {schema}.maf_runtime_evidence_receipts
        (project_id, run_id, ((receipt_json->>'dispatchId')::uuid),
         (receipt_json->>'priorAuthorityKey'))
    WHERE receipt_kind = 'native-child-rebind';
