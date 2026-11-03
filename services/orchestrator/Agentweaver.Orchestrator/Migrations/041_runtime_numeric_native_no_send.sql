ALTER TABLE {schema}.maf_runtime_evidence_receipts
    DROP CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check;

ALTER TABLE {schema}.maf_runtime_evidence_receipts
    ADD CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check CHECK (
        receipt_kind IN (
            'numeric-limit-denial',
            'replacement-comparison',
            'numeric-limit-native-no-send'));
