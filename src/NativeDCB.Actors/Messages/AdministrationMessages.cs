// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Actors.Messages;

public enum AdministrationErrorKind
{
    None,
    InvalidArgument,
    NotFound,
    FailedPrecondition,
    Unavailable,
    DataLoss
}

[GenerateSerializer]
[Immutable]
public sealed record AdministrationErrorMessage(
    [property: Id(0)] AdministrationErrorKind Kind,
    [property: Id(1)] string Message);

[GenerateSerializer]
[Immutable]
public sealed record AdministrationOperationResultMessage(
    [property: Id(0)] AdministrationErrorMessage? Error = null);

[GenerateSerializer]
[Immutable]
public sealed record StateFileStatusMessage(
    [property: Id(0)] bool Present,
    [property: Id(1)] bool Locked,
    [property: Id(2)] bool JsonValid,
    [property: Id(3)] bool SchemaValid,
    [property: Id(4)] int SourcePartition,
    [property: Id(5)] string? Error);

[GenerateSerializer]
[Immutable]
public sealed record PartitionStateStatusMessage(
    [property: Id(0)] PartitionStatusMessage Partition,
    [property: Id(1)] StateFileStatusMessage StateFile);

[GenerateSerializer]
[Immutable]
public sealed record PartitionStateListMessage(
    [property: Id(0)] PartitionStateStatusMessage[] Partitions,
    [property: Id(1)] AdministrationErrorMessage? Error = null);

[GenerateSerializer]
[Immutable]
public sealed record StateFileStatusResultMessage(
    [property: Id(0)] StateFileStatusMessage? Status,
    [property: Id(1)] AdministrationErrorMessage? Error = null);

[GenerateSerializer]
[Immutable]
public sealed record IndexListResultMessage(
    [property: Id(0)] IndexStatusMessage[] Indexes,
    [property: Id(1)] AdministrationErrorMessage? Error = null);