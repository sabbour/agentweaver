-- Keep the provider binding unchanged after the message store verifies it under a row lock.
CREATE TRIGGER messaging_provider_bindings_no_update
    BEFORE UPDATE ON {schema}.messaging_provider_bindings
    FOR EACH ROW EXECUTE FUNCTION {schema}.reject_row_lock_record_update();
