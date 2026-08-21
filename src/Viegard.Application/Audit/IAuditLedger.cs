using Viegard.Domain.Audit;

namespace Viegard.Application.Audit;

/// <summary>
/// Append-only audit ledger.  Every pipeline stage records its transitions
/// here; records are never updated or deleted by application code (retention
/// is a separately configured concern).
/// </summary>
public interface IAuditLedger
{
    ValueTask AppendAsync(AuditRecord record, CancellationToken cancellationToken = default);
}
