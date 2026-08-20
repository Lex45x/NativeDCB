using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IDatabaseDirectoryGrain : IGrainWithIntegerKey
{
    const long SingletonKey = 0;

    Task<ListDatabasesActorResponse> ListAsync(
        ListDatabasesActorRequest request,
        GrainCancellationToken cancellationToken);

    Task<CreateDatabaseActorResponse> CreateAsync(
        CreateDatabaseActorRequest request,
        GrainCancellationToken cancellationToken);

    Task<GetDatabaseInfoActorResponse> GetInfoAsync(
        GetDatabaseInfoActorRequest request,
        GrainCancellationToken cancellationToken);

    Task<GetHealthActorResponse> GetHealthAsync(
        GetHealthActorRequest request,
        GrainCancellationToken cancellationToken);

    Task<GetCapabilitiesActorResponse> GetCapabilitiesAsync(
        GetCapabilitiesActorRequest request,
        GrainCancellationToken cancellationToken);

    Task<GetMainHeadActorResponse> GetMainHeadAsync(
        GetMainHeadActorRequest request,
        GrainCancellationToken cancellationToken);
}