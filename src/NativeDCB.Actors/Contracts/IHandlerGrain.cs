using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IHandlerGrain : IGrainWithStringKey
{
    Task<HandlerRegistrationResultMessage> RegisterAsync(
        RegisterHandlerMessage request,
        GrainCancellationToken cancellationToken);

    Task<HandlerRemoveResultMessage?> RemoveAsync(
        string name,
        GrainCancellationToken cancellationToken);

    Task<HandlerDescriptionMessage?> GetAsync(
        GetHandlerMessage request,
        GrainCancellationToken cancellationToken);

    Task<HandlerListMessage> ListAsync(GrainCancellationToken cancellationToken);

    Task<NdlValidationResultMessage> ValidateNdlAsync(
        ValidateNdlMessage request,
        GrainCancellationToken cancellationToken);

    Task<PublishStatementResultMessage> PublishStatementAsync(
        PublishStatementMessage request,
        GrainCancellationToken cancellationToken);
}