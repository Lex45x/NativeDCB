using Grpc.Core;

using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

using ProtocolDiagnosticSeverity = NativeDCB.Protocol.V1.DiagnosticSeverity;
using ProtocolQueryItem = NativeDCB.Protocol.V1.QueryItem;

namespace NativeDCB.Server.Grpc;

public sealed class StatementGrpcService(IGrainFactory grains) : StatementService.StatementServiceBase
{
    public override async Task<ExplainStatementResponse> ExplainStatement(
        ExplainStatementRequest request,
        ServerCallContext context)
    {
        NdlValidationResultMessage result = await CallAsync(
                token => grains.GetGrain<IHandlerGrain>(request.Database).ValidateNdlAsync(
                    new ValidateNdlMessage(request.NdlSource, []), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);

        ExplainStatementResponse response = new() { Valid = result.Valid };
        response.Diagnostics.AddRange(result.Diagnostics.Select(ToDiagnostic));
        if (result.Plan is not null)
        {
            response.Plan = new PlanSummary
            {
                PlanFingerprint = result.Plan.Fingerprint,
                RedactedSummary = result.Plan.RedactedSummary
            };
            response.Plan.Operations.AddRange(result.Plan.Operations);
        }

        response.QueryTemplates.AddRange((result.QueryTemplates ?? []).Select(ToQuery));
        return response;
    }

    public override async Task ExecuteStatement(
        ExecuteStatementRequest request,
        IServerStreamWriter<StatementResult> responseStream,
        ServerCallContext context)
    {
        PublishStatementResultMessage result = await CallAsync(
                token => grains.GetGrain<IHandlerGrain>(request.Database).PublishStatementAsync(
                    new PublishStatementMessage(request.NdlSource), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);

        if (result.Diagnostics.Length > 0)
        {
            DiagnosticBatch batch = new();
            batch.Diagnostics.AddRange(result.Diagnostics.Select(ToDiagnostic));
            await responseStream.WriteAsync(
                    new StatementResult { StatementIndex = 0, Diagnostics = batch },
                    context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }

        if (!result.Valid)
        {
            await WriteCompletionAsync(
                    responseStream,
                    statementIndex: 0,
                    succeeded: false,
                    "Statement validation failed.",
                    context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        for (int index = 0; index < result.Registrations.Length; index++)
        {
            HandlerSummaryMessage registration = result.Registrations[index];
            await responseStream.WriteAsync(
                    new StatementResult
                    {
                        StatementIndex = checked((uint)index),
                        Registration = new RegistrationResult
                        {
                            Kind = "handler",
                            Name = registration.Name,
                            SourceFingerprint = registration.SourceFingerprint,
                            PlanFingerprint = registration.PlanFingerprint
                        }
                    },
                    context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }

        await WriteCompletionAsync(
                responseStream,
                checked((uint)result.Registrations.Length),
                succeeded: true,
                $"Registered {result.Registrations.Length} handler(s).",
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    private static Query ToQuery(EventQueryMessage value)
    {
        Query query = new();
        foreach (QueryItemMessage actorItem in value.Items)
        {
            ProtocolQueryItem item = new();
            item.EventTypes.Add(actorItem.EventTypes);
            item.Keys.AddRange(actorItem.Keys.Select(key => new KeyValue
            {
                Key = key.Name,
                Value = key.Value
            }));
            query.Items.Add(item);
        }

        return query;
    }

    private static Diagnostic ToDiagnostic(ActorDiagnosticMessage value)
    {
        Diagnostic result = new()
        {
            Code = value.Code,
            Severity = value.Severity switch
            {
                ActorDiagnosticSeverity.Info => ProtocolDiagnosticSeverity.Info,
                ActorDiagnosticSeverity.Warning => ProtocolDiagnosticSeverity.Warning,
                ActorDiagnosticSeverity.Error => ProtocolDiagnosticSeverity.Error,
                _ => ProtocolDiagnosticSeverity.Unspecified
            },
            Message = value.Message
        };
        if (value.SourceSpan is not null)
        {
            result.SourceSpan = new SourceSpan
            {
                Start = ToPosition(value.SourceSpan.Start),
                End = ToPosition(value.SourceSpan.End)
            };
        }

        return result;
    }

    private static SourcePosition ToPosition(ActorSourcePositionMessage value)
    {
        return new SourcePosition
        {
            Line = checked(value.Line + 1),
            Column = checked(value.Column + 1),
            Offset = value.Offset
        };
    }

    private static Task WriteCompletionAsync(
        IServerStreamWriter<StatementResult> responseStream,
        uint statementIndex,
        bool succeeded,
        string summary,
        CancellationToken cancellationToken)
    {
        return responseStream.WriteAsync(
            new StatementResult
            {
                StatementIndex = statementIndex,
                Completion = new StatementCompletion { Succeeded = succeeded, Summary = summary }
            },
            cancellationToken);
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
                DirectoryNotFoundException => ProtocolMapper.NotFound(root.Message),
                InvalidDataException => ProtocolMapper.DataLoss(root.Message),
                IOException => ProtocolMapper.Unavailable(root.Message),
                UnauthorizedAccessException => ProtocolMapper.Unavailable(root.Message),
                _ => exception
            };
        }
    }

    private static void ThrowIfError(CatalogErrorMessage? error)
    {
        if (error is null || error.Kind == CatalogErrorKind.None)
        {
            return;
        }

        throw error.Kind switch
        {
            CatalogErrorKind.InvalidArgument => ProtocolMapper.InvalidArgument(error.Message),
            _ => ProtocolMapper.Internal(error.Message)
        };
    }
}