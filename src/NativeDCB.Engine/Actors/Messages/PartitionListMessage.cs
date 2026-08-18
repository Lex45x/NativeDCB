namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record PartitionListMessage(
    [property: Id(id: 0)] PartitionStatusMessage[] Partitions);