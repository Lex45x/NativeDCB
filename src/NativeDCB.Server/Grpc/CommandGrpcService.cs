using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using NativeDCB.Actors.Audit;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

namespace NativeDCB.Server.Grpc;

public sealed class CommandGrpcService(IGrainFactory grains) : CommandService.CommandServiceBase
{
    public override async Task<ExecuteHandlerResponse> ExecuteHandler(
        ExecuteHandlerRequest request,
        ServerCallContext context)
    {
        Guid commandId = await ParseAuditedCommandIdAsync(request.HasCommandId ? request.CommandId : null)
            .ConfigureAwait(false);
        IDecisionGrain decision = Decision(request.Database, commandId);
        ExecuteDecisionResultMessage result = await CallAsync(
                token => decision.ExecuteAsync(new ExecuteDecisionMessage(
                    request.Database,
                    commandId,
                    request.HandlerName,
                    request.CommandJson.ToByteArray()), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.ErrorKind, result.Message);
        return ToResponse(result);
    }

    public override async Task<PrepareDecisionResponse> PrepareDecision(
        PrepareDecisionRequest request,
        ServerCallContext context)
    {
        Guid commandId = await ParseAuditedCommandIdAsync(request.HasCommandId ? request.CommandId : null)
            .ConfigureAwait(false);
        IDecisionGrain decision = Decision(request.Database, commandId);
        PrepareDecisionResultMessage result = await CallAsync(
                token => decision.PrepareAsync(new PrepareDecisionMessage(
                    request.Database,
                    commandId,
                    request.HandlerName,
                    request.CommandJson.ToByteArray()), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.ErrorKind, result.Message);
        return ToResponse(result);
    }

    public override async Task<CompleteDecisionResponse> CompleteDecision(
        CompleteDecisionRequest request,
        ServerCallContext context)
    {
        IRemoteDecisionRouterGrain router = grains.GetGrain<IRemoteDecisionRouterGrain>("complete-decision");
        CompleteDecisionResultMessage result = await CallAsync(
                token => router.CompleteAsync(new RouteRemoteDecisionMessage(
                    request.Database,
                    request.ModelSignature.ToByteArray(),
                    request.ProposedEvents.Select(value => new ProposedDecisionEventMessage(
                        value.Type, value.DataJson.ToByteArray())).ToArray()), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.ErrorKind, result.Message);
        return ToResponse(result);
    }

    public override async Task<GetEventsByCommandIdResponse> GetEventsByCommandId(
        GetEventsByCommandIdRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParseExact(request.CommandId, "D", out Guid commandId) || commandId == Guid.Empty)
        {
            throw ProtocolMapper.InvalidArgument("A canonical command UUID is required.");
        }

        IReadGrain reader = grains.GetGrain<IReadGrain>(request.Database);
        CommandReadSnapshotMessage result = await CallAsync(
                token => reader.ReadCommandSnapshotAsync(commandId, token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (result.Events.Length == 0)
        {
            throw ProtocolMapper.NotFound($"Command '{request.CommandId}' was not found.");
        }

        GetEventsByCommandIdResponse response = new();
        response.Events.AddRange(result.Events.Select(ToEnvelope));
        return response;
    }

    private IDecisionGrain Decision(string database, Guid commandId)
    {
        return grains.GetGrain<IDecisionGrain>($"{database}|{commandId:D}");
    }

    private static ExecuteHandlerResponse ToResponse(ExecuteDecisionResultMessage result)
    {
        ExecuteHandlerResponse response = new()
        {
            CommandId = result.CommandId.ToString("D"),
            CommandType = result.CommandType
        };
        switch (result.Outcome)
        {
            case ExecuteDecisionOutcome.Committed:
                response.Committed = Committed(result.Events);
                break;
            case ExecuteDecisionOutcome.AlreadyCommitted:
                response.AlreadyCommitted = AlreadyCommitted(result.Events);
                break;
            case ExecuteDecisionOutcome.Rejected:
                response.Rejected = new Rejected
                {
                    Code = result.Code,
                    Message = result.Message,
                    DetailsJson = ByteString.CopyFromUtf8("{}")
                };
                break;
            case ExecuteDecisionOutcome.Failed:
                response.Failed = Failed(result.Code, result.Message);
                break;
            default:
                throw ProtocolMapper.Internal("The decision actor returned an unknown execute outcome.");
        }

        return response;
    }

    private static PrepareDecisionResponse ToResponse(PrepareDecisionResultMessage result)
    {
        PrepareDecisionResponse response = new()
        {
            CommandId = result.CommandId.ToString("D"),
            CommandType = result.CommandType
        };
        switch (result.Outcome)
        {
            case PrepareDecisionOutcome.Prepared:
                response.Prepared = new PreparedDecision
                {
                    ModelJson = ByteString.CopyFrom(result.ModelJson),
                    ModelSignature = ByteString.CopyFrom(result.ModelSignature),
                    ExpiresUtc = Timestamp.FromDateTimeOffset(result.ExpiresUtc),
                    PlanFingerprint = result.PlanFingerprint
                };
                break;
            case PrepareDecisionOutcome.AlreadyCommitted:
                response.AlreadyCommitted = AlreadyCommitted(result.Events);
                break;
            case PrepareDecisionOutcome.Failed:
                response.Failed = Failed(result.Code, result.Message);
                break;
            default:
                throw ProtocolMapper.Internal("The decision actor returned an unknown prepare outcome.");
        }

        return response;
    }

    private static CompleteDecisionResponse ToResponse(CompleteDecisionResultMessage result)
    {
        CompleteDecisionResponse response = new()
        {
            CommandId = result.CommandId.ToString("D"),
            CommandType = result.CommandType
        };
        switch (result.Outcome)
        {
            case CompleteDecisionOutcome.Committed:
                response.Committed = Committed(result.Events);
                break;
            case CompleteDecisionOutcome.AlreadyCommitted:
                response.AlreadyCommitted = AlreadyCommitted(result.Events);
                break;
            case CompleteDecisionOutcome.Stale:
                response.Stale = new DecisionStale { CurrentHead = result.CurrentHead };
                break;
            case CompleteDecisionOutcome.Expired:
                response.Expired = new DecisionExpired
                {
                    ExpiresUtc = Timestamp.FromDateTimeOffset(result.ExpiresUtc)
                };
                break;
            case CompleteDecisionOutcome.Invalidated:
                response.Invalidated = new DecisionInvalidated
                {
                    Code = result.Code,
                    Message = result.Message
                };
                break;
            case CompleteDecisionOutcome.Failed:
                response.Failed = Failed(result.Code, result.Message);
                break;
            default:
                throw ProtocolMapper.Internal("The decision actor returned an unknown completion outcome.");
        }

        return response;
    }

    private static Committed Committed(SequencedEventMessage[] events)
    {
        Committed committed = new()
        {
            FirstEventId = events[0].EventId,
            LastEventId = events[^1].EventId
        };
        committed.Events.AddRange(events.Select(ToEnvelope));
        return committed;
    }

    private static AlreadyCommitted AlreadyCommitted(SequencedEventMessage[] events)
    {
        return new AlreadyCommitted
        {
            FirstEventId = events[0].EventId,
            LastEventId = events[^1].EventId
        };
    }

    private static CommandFailed Failed(string? code, string? message)
    {
        return new CommandFailed
        {
            Error = new ErrorDetail
            {
                Code = code ?? "CommandFailed",
                Message = message ?? "The command failed."
            }
        };
    }

    private static EventEnvelope ToEnvelope(SequencedEventMessage value)
    {
        return ProtocolMapper.ToEnvelope(ActorMessageMapper.ToModel(value));
    }

    private static void ThrowIfError(DecisionCallErrorKind kind, string? message)
    {
        if (kind == DecisionCallErrorKind.None)
        {
            return;
        }

        string detail = message ?? "The decision actor call failed.";
        throw kind switch
        {
            DecisionCallErrorKind.InvalidArgument => ProtocolMapper.InvalidArgument(detail),
            DecisionCallErrorKind.NotFound => ProtocolMapper.NotFound(detail),
            DecisionCallErrorKind.FailedPrecondition => ProtocolMapper.FailedPrecondition(detail),
            DecisionCallErrorKind.Unavailable => ProtocolMapper.Unavailable(detail),
            DecisionCallErrorKind.DataLoss => ProtocolMapper.DataLoss(detail),
            _ => ProtocolMapper.Internal(detail)
        };
    }

    private static Guid ParseCommandId(string? value)
    {
        if (value is null)
        {
            return Guid.NewGuid();
        }

        return Guid.TryParseExact(value, "D", out Guid commandId) && commandId != Guid.Empty
            ? commandId
             : throw ProtocolMapper.InvalidArgument("command_id must be a canonical non-empty UUID.");
    }

    private async Task<Guid> ParseAuditedCommandIdAsync(string? value)
    {
        try
        {
            return ParseCommandId(value);
        }
        catch (RpcException exception)
        {
            await AuditRecorder.OutcomeAsync(grains, "invalid", exception.StatusCode.ToString())
                .ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<T> CallAsync<T>(
        Func<GrainCancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GrainCall.RunAsync(call, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (Exception exception) when (exception is not RpcException and not OperationCanceledException)
        {
            Exception root = exception.GetBaseException();
            throw root switch
            {
                ArgumentException => ProtocolMapper.InvalidArgument(root.Message),
                DirectoryNotFoundException or KeyNotFoundException => ProtocolMapper.NotFound(root.Message),
                InvalidDataException => ProtocolMapper.DataLoss(root.Message),
                IOException or UnauthorizedAccessException => ProtocolMapper.Unavailable(root.Message),
                _ => exception
            };
        }
    }
}