namespace BackendLoyalty.Application.Auditing;

public sealed record AuditEvent(
    string BusinessId,
    string? UserId,
    string? Role,
    string? OutletId,
    string ActionType,
    string? ResourceType,
    string? ResourceId,
    string? Summary,
    string? MetadataJson,
    string? Ip,
    string? UserAgent);

public interface IAuditLogWriter
{
    Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
