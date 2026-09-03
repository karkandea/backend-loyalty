namespace BackendLoyalty.Application.Members;

public interface IMemberScanService
{
    Task<MemberScanResult?> ScanAsync(string businessId, string memberBarcode, CancellationToken cancellationToken = default);
}
