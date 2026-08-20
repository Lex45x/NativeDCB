namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record PartitionListMessage(
    [property: Id(id: 0)] PartitionStatusMessage[] Partitions);