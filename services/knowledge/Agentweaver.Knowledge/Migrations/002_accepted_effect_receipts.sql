CREATE OR REPLACE FUNCTION "{schema}".reject_accepted_effect_receipt_mutation()
RETURNS trigger LANGUAGE plpgsql AS $body$
BEGIN
    IF TG_OP = 'DELETE' THEN
        IF OLD.event_type = 'knowledge.accepted-effect' THEN
            RAISE EXCEPTION 'Accepted-effect receipts are immutable.' USING ERRCODE = '55000';
        END IF;
        RETURN OLD;
    END IF;
    IF OLD.event_type = 'knowledge.accepted-effect' OR NEW.event_type = 'knowledge.accepted-effect' THEN
        IF ROW(OLD.id, OLD.stream_id, OLD.sequence, OLD.idempotency_key, OLD.event_type,
                OLD.event_version, OLD.payload, OLD.occurred_at) IS DISTINCT FROM
            ROW(NEW.id, NEW.stream_id, NEW.sequence, NEW.idempotency_key, NEW.event_type,
                NEW.event_version, NEW.payload, NEW.occurred_at) THEN
            RAISE EXCEPTION 'Accepted-effect receipts are immutable.' USING ERRCODE = '55000';
        END IF;
    END IF;
    RETURN NEW;
END
$body$;

CREATE TRIGGER knowledge_accepted_effect_receipt_immutable
BEFORE UPDATE OR DELETE ON "{schema}".outbox_events
FOR EACH ROW EXECUTE FUNCTION "{schema}".reject_accepted_effect_receipt_mutation();
