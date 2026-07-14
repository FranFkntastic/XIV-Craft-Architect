namespace FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;

public interface IWorkshopHostAcquisitionClient
{
    Task<WorkshopHostCapabilityResponse> GetCapabilitiesAsync(
        WorkshopHostConnectionOptions connection,
        CancellationToken cancellationToken = default);

    Task<WorkshopHostAcquisitionRequestView> CreateBatchAsync(
        WorkshopHostConnectionOptions connection,
        WorkshopHostAcquisitionBatchCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkshopHostAcquisitionTimeline> GetTimelineAsync(
        WorkshopHostConnectionOptions connection,
        string requestId,
        CancellationToken cancellationToken = default);
}
