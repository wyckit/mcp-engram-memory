namespace McpEngramMemory.Core.Models.Constitution;

/// <summary>
/// Opaque proof that the Constitution kernel allowed and durably audited one exact commit
/// operation. Only the Core kernel can construct receipts; stores bind them to the promoted
/// tenant, target, payload hash, decision, and audit object.
/// </summary>
public sealed class ConstitutionCommitReceipt
{
    public OperationEnvelope Operation { get; }
    public ConstitutionDecision Decision { get; }
    public ConstitutionAuditRecord AuditRecord { get; }

    internal ConstitutionCommitReceipt(
        OperationEnvelope operation,
        ConstitutionDecision decision,
        ConstitutionAuditRecord auditRecord)
    {
        Operation = operation;
        Decision = decision;
        AuditRecord = auditRecord;
    }
}
