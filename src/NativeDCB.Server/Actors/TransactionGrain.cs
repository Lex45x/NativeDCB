using System.Text.Json;

using NativeDCB.Engine.Actors;
using NativeDCB.Server.Runtime;
using NativeDCB.Server.Storage;

namespace NativeDCB.Server.Actors;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class TransactionGrain(IGrainFactory grains) : Grain, ITransactionGrain
{
    private readonly NdlDecisionRuntime _runtime = new();

    public async Task<DecisionResultMessage> ExecuteAsync(
        TransactionRequestMessage request,
        GrainCancellationToken cancellationToken)
    {
        try
        {
            IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(request.Database);
            IReadGrain reader = grains.GetGrain<IReadGrain>(request.Database);
            WriterStateMessage writerState = await writer.GetStateAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            EventListMessage existing = await reader.ReadByCommandIdAsync(
                    request.CommandId, writerState.Head, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (existing.Events.Length > 0)
            {
                return new DecisionResultMessage(
                    DecisionResultOutcome.AlreadyCommitted,
                    request.CommandId,
                    existing.Events[0].CommandType,
                    existing.Events,
                    Code: null,
                    Message: null);
            }

            using JsonDocument document = JsonDocument.Parse(request.CommandJson);
            Dictionary<string, RegisteredJsonSchema> eventSchemas = request.EventSchemas.ToDictionary(
                schema => schema.Name,
                schema => RegisteredJsonSchema.Parse(schema.Name, schema.DocumentJson, eventSchema: true),
                StringComparer.Ordinal);
            if (request.CommandSchema is { } commandSchema)
            {
                RegisteredJsonSchema.Parse(
                        commandSchema.Name, commandSchema.DocumentJson, eventSchema: false)
                    .Validate(document.RootElement);
            }

            HandlerCatalogEntry handler = new(
                request.HandlerName,
                request.CommandType,
                request.NdlSource,
                request.SourceFingerprint,
                request.PlanFingerprint,
                request.PlanJson);
            DecisionExecution execution = await _runtime.ExecuteAsync(
                grains,
                request.Database,
                handler,
                eventSchemas,
                request.CommandId,
                document.RootElement,
                cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            return new DecisionResultMessage(
                execution.Outcome switch
                {
                    DecisionOutcome.Committed => DecisionResultOutcome.Committed,
                    DecisionOutcome.AlreadyCommitted => DecisionResultOutcome.AlreadyCommitted,
                    DecisionOutcome.Rejected => DecisionResultOutcome.Rejected,
                    _ => throw new ArgumentOutOfRangeException(nameof(execution))
                },
                request.CommandId,
                request.CommandType,
                execution.Events.Select(ActorMessageMapper.ToMessage).ToArray(),
                execution.RejectionCode,
                execution.RejectionMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            return Result(DecisionResultOutcome.DataLoss, request, [], "DataLoss", exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(DecisionResultOutcome.Unavailable, request, [], "WriterUnavailable", exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
                                              or DivideByZeroException or JsonException)
        {
            return Result(DecisionResultOutcome.Failed, request, [], "InvalidCommand", exception.Message);
        }
    }

    private static DecisionResultMessage Result(
        DecisionResultOutcome outcome,
        TransactionRequestMessage request,
        SequencedEventMessage[] events,
        string? code = null,
        string? message = null)
    {
        return new DecisionResultMessage(
            outcome,
            request.CommandId,
            request.CommandType,
            events,
            code,
            message);
    }
}