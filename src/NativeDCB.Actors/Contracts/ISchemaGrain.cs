using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface ISchemaGrain : IGrainWithStringKey
{
    Task<SchemaRegistrationResultMessage> RegisterAsync(
        RegisterSchemaMessage request,
        GrainCancellationToken cancellationToken);

    Task<SchemaRemoveResultMessage?> RemoveAsync(
        SchemaLookupMessage request,
        GrainCancellationToken cancellationToken);

    Task<SchemaRegistrationMessage?> GetAsync(
        SchemaLookupMessage request,
        GrainCancellationToken cancellationToken);

    Task<SchemaListMessage> ListAsync(
        ActorSchemaKind kind,
        GrainCancellationToken cancellationToken);

    Task<SchemaSnapshotMessage> GetSnapshotAsync(GrainCancellationToken cancellationToken);
}