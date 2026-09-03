namespace BackendLoyalty.Application.Members;

public sealed record MemberSessionContext(
    string MemberId,
    string BusinessId,
    string? Email,
    string Name,
    string MemberBarcode);

public interface IMemberSessionResolver
{
    Task<MemberSessionContext?> ResolveAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default);
}
