ALTER TABLE "{schema}".knowledge_records
    ADD COLUMN superseded_by_record_id uuid,
    ADD CONSTRAINT knowledge_records_superseded_by_fk
        FOREIGN KEY (project_id, superseded_by_record_id)
        REFERENCES "{schema}".knowledge_records (project_id, record_id) ON DELETE RESTRICT
        DEFERRABLE INITIALLY DEFERRED,
    DROP CONSTRAINT knowledge_records_state_check,
    ADD CONSTRAINT knowledge_records_state_check
        CHECK (state IN ('Pending', 'Active', 'Rejected', 'Archived', 'Promoted', 'Superseded')),
    ADD CONSTRAINT knowledge_records_supersession_state_check
        CHECK ((kind = 'Decision' AND state = 'Superseded' AND superseded_by_record_id IS NOT NULL)
            OR (state <> 'Superseded' AND superseded_by_record_id IS NULL));

ALTER TABLE "{schema}".knowledge_revisions
    ADD COLUMN superseded_by_record_id uuid,
    ADD COLUMN reason text,
    ALTER COLUMN actor_fingerprint DROP NOT NULL,
    ALTER COLUMN change_kind DROP NOT NULL,
    ADD CONSTRAINT knowledge_revisions_superseded_by_fk
        FOREIGN KEY (project_id, superseded_by_record_id)
        REFERENCES "{schema}".knowledge_records (project_id, record_id) ON DELETE RESTRICT
        DEFERRABLE INITIALLY DEFERRED,
    DROP CONSTRAINT knowledge_revisions_state_check,
    ADD CONSTRAINT knowledge_revisions_state_check
        CHECK (state IN ('Pending', 'Active', 'Rejected', 'Archived', 'Promoted', 'Superseded')),
    ADD CONSTRAINT knowledge_revisions_supersession_state_check
        CHECK ((kind = 'Decision' AND state = 'Superseded' AND superseded_by_record_id IS NOT NULL)
            OR (state <> 'Superseded' AND superseded_by_record_id IS NULL)),
    DROP CONSTRAINT knowledge_revisions_change_kind_check,
    ADD CONSTRAINT knowledge_revisions_change_kind_check
        CHECK (change_kind IS NULL OR change_kind IN (
            'created', 'updated', 'proposal_created', 'proposal_rejected', 'proposal_promoted',
            'decision_archived', 'decision_approved', 'decision_restored',
            'decision_superseded', 'imported'));

ALTER TABLE "{schema}".knowledge_write_idempotency
    DROP CONSTRAINT knowledge_write_idempotency_result_kind_check,
    ADD CONSTRAINT knowledge_write_idempotency_result_kind_check
        CHECK (result_kind IN ('record', 'promotion', 'transfer'));
