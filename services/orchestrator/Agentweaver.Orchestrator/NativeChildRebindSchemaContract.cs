namespace Agentweaver.Orchestrator;

internal static class NativeChildRebindSchemaContract
{
    internal const string DispatchIdField = "dispatchId";
    internal const string RecoveryOperationIdField = "recoveryOperationId";
    internal const string DestinationFenceField = "destinationFence";
    internal const string PriorAuthorityKeyField = "priorAuthorityKey";
    internal const string PriorReceiptIdField = "priorReceiptId";
    internal const string PriorReceiptHashField = "priorReceiptHash";
    internal const string UuidRegex = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
    internal const string PositiveIntegerRegex = "^[1-9][0-9]*$";
    internal const string Sha256Regex = "^[0-9a-f]{64}$";
    internal const int UuidRegexOccurrences = 3;
    internal const int PositiveIntegerRegexOccurrences = 1;
    internal const int Sha256RegexOccurrences = 1;
    internal const int AndOperatorOccurrences = 13;
    internal const int OrOperatorOccurrences = 2;

    internal static readonly string[] RequiredMigrationFragments =
    [
        DispatchIdField,
        RecoveryOperationIdField,
        DestinationFenceField,
        PriorAuthorityKeyField,
        PriorReceiptIdField,
        PriorReceiptHashField,
        "IS TRUE",
        "'origin'",
        "'receipt:'",
        "||",
        $"receipt_json ? '{PriorReceiptIdField}'",
        $"receipt_json ? '{PriorReceiptHashField}'"
    ];

    internal static readonly string FieldsConstraintStructure = $$"""
        pg_get_constraintdef(oid) LIKE '%{{DispatchIdField}}%'
        AND pg_get_constraintdef(oid) LIKE '%{{RecoveryOperationIdField}}%'
        AND pg_get_constraintdef(oid) LIKE '%{{DestinationFenceField}}%'
        AND pg_get_constraintdef(oid) LIKE '%{{PriorAuthorityKeyField}}%'
        AND pg_get_constraintdef(oid) LIKE '%{{PriorReceiptIdField}}%'
        AND pg_get_constraintdef(oid) LIKE '%{{PriorReceiptHashField}}%'
        AND pg_get_constraintdef(oid) LIKE '%IS TRUE%'
        AND strpos(pg_get_constraintdef(oid),
            '{{UuidRegex}}') > 0
        AND array_length(string_to_array(pg_get_constraintdef(oid),
            '{{UuidRegex}}'), 1) - 1 = {{UuidRegexOccurrences}}
        AND array_length(string_to_array(pg_get_constraintdef(oid),
            '{{PositiveIntegerRegex}}'), 1) - 1 = {{PositiveIntegerRegexOccurrences}}
        AND array_length(string_to_array(pg_get_constraintdef(oid),
            '{{Sha256Regex}}'), 1) - 1 = {{Sha256RegexOccurrences}}
        AND pg_get_constraintdef(oid) LIKE '%''origin''%'
        AND pg_get_constraintdef(oid) LIKE '%''receipt:''%'
        AND pg_get_constraintdef(oid) LIKE '%||%'
        AND array_length(regexp_split_to_array(pg_get_constraintdef(oid), '\mAND\M'), 1) - 1 = {{AndOperatorOccurrences}}
        AND array_length(regexp_split_to_array(pg_get_constraintdef(oid), '\mOR\M'), 1) - 1 = {{OrOperatorOccurrences}}
        """;
}
