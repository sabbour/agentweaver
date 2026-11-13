namespace Agentweaver.Orchestrator;

internal static class NativeChildRebindSchemaContract
{
    internal const string FieldsConstraintStructure = """
        pg_get_constraintdef(oid) LIKE '%dispatchId%'
        AND pg_get_constraintdef(oid) LIKE '%recoveryOperationId%'
        AND pg_get_constraintdef(oid) LIKE '%destinationFence%'
        AND pg_get_constraintdef(oid) LIKE '%priorAuthorityKey%'
        AND pg_get_constraintdef(oid) LIKE '%priorReceiptId%'
        AND pg_get_constraintdef(oid) LIKE '%priorReceiptHash%'
        AND pg_get_constraintdef(oid) LIKE '%IS TRUE%'
        AND strpos(pg_get_constraintdef(oid),
            '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') > 0
        AND array_length(string_to_array(pg_get_constraintdef(oid),
            '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'), 1) - 1 = 2
        AND strpos(pg_get_constraintdef(oid), '^[1-9][0-9]*$') > 0
        AND strpos(pg_get_constraintdef(oid), '^[0-9a-f]{64}$') > 0
        AND pg_get_constraintdef(oid) LIKE '%origin%'
        AND pg_get_constraintdef(oid) LIKE '%receipt:%'
        AND pg_get_constraintdef(oid) LIKE '%||%'
        AND array_length(regexp_split_to_array(pg_get_constraintdef(oid), '\mAND\M'), 1) - 1 = 13
        AND array_length(regexp_split_to_array(pg_get_constraintdef(oid), '\mOR\M'), 1) - 1 = 2
        """;
}
