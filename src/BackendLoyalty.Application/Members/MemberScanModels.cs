namespace BackendLoyalty.Application.Members;

public sealed record MemberScanRequest(string MemberBarcode);

public sealed record MemberScanResult(
    string MemberId,
    string MemberName,
    string? MemberEmail,
    ActiveMemberCardResult? ActiveMemberCard);

public sealed record ActiveMemberCardResult(
    string MemberCardId,
    string? CardName,
    int CurrentStamps,
    int? RequiredStamps);
