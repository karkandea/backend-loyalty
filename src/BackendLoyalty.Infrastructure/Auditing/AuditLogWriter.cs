using BackendLoyalty.Application.Auditing;
using BackendLoyalty.Domain.Entities;
using BackendLoyalty.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace BackendLoyalty.Infrastructure.Auditing;

public sealed class AuditLogWriter(
    LoyaltyDbContext dbContext,
    ILogger<AuditLogWriter> logger) : IAuditLogWriter
{
    public async Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(auditEvent.BusinessId, out var businessId))
        {
            logger.LogWarning(
                "Skipping audit log {ActionType}: business id is not a UUID",
                auditEvent.ActionType);
            return;
        }

        var userId = TryParseNullableGuid(auditEvent.UserId);
        var outletId = TryParseNullableGuid(auditEvent.OutletId);

        try
        {
            dbContext.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                UserId = userId,
                Role = auditEvent.Role,
                OutletId = outletId,
                ActionType = auditEvent.ActionType,
                ResourceType = auditEvent.ResourceType,
                ResourceId = auditEvent.ResourceId,
                Summary = auditEvent.Summary,
                MetadataJson = auditEvent.MetadataJson,
                Ip = auditEvent.Ip,
                UserAgent = auditEvent.UserAgent,
                CreatedAt = DateTime.UtcNow,
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Failed to write audit log {ActionType}",
                auditEvent.ActionType);
        }
    }

    private static Guid? TryParseNullableGuid(string? value) =>
        Guid.TryParse(value, out var parsed) ? parsed : null;
}
