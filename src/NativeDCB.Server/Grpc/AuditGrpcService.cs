using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

namespace NativeDCB.Server.Grpc;

internal sealed class AuditGrpcService(IGrainFactory grains) : AuditService.AuditServiceBase
{
    public override async Task<ListAuditRecordsResponse> ListAuditRecords(
        ListAuditRecordsRequest request,
        ServerCallContext context)
    {
        if (request.AfterSequence < 0)
        {
            throw ProtocolMapper.InvalidArgument("after_sequence must be non-negative.");
        }

        if (request.Limit > 1000)
        {
            throw ProtocolMapper.InvalidArgument("limit must be between 1 and 1000.");
        }

        int limit = request.Limit == 0 ? 100 : checked((int)request.Limit);

        string? database = null;
        if (request.HasDatabase)
        {
            try
            {
                database = ActorStoragePath.NormalizeDatabaseName(request.Database);
            }
            catch (ArgumentException exception)
            {
                throw ProtocolMapper.InvalidArgument(exception.Message);
            }
        }

        ListAuditRecordsActorResponse result = await GrainCall.RunAsync(
                token => grains.GetGrain<IAuditGrain>(IAuditGrain.SingletonKey).ListAsync(
                    new ListAuditRecordsActorRequest(
                        request.AfterSequence,
                        limit,
                        database,
                        request.HasOperation ? request.Operation : null,
                        request.HasPhase ? request.Phase : null,
                        request.HasOutcome ? request.Outcome : null,
                        request.HasAuthenticationScheme ? request.AuthenticationScheme : null,
                        request.HasSubject ? request.Subject : null),
                    token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);

        ListAuditRecordsResponse response = new()
        {
            NextAfterSequence = result.NextAfterSequence,
            HasMore = result.HasMore,
            BoundarySequence = result.BoundarySequence
        };
        response.Records.AddRange(result.Records.Select(ToRecord));
        return response;
    }

    private static AuditRecord ToRecord(AuditRecordMessage value)
    {
        AuditRecord record = new()
        {
            Schema = "native-dcb-audit-record-v1",
            Sequence = value.Sequence,
            TimestampUtc = Timestamp.FromDateTimeOffset(value.TimestampUtc),
            OperationId = value.OperationId.ToString("D"),
            Phase = value.Phase,
            Category = value.Category,
            Operation = value.Operation,
            PreviousHash = value.PreviousHash,
            RecordHash = value.RecordHash
        };
        Set(value.AuthenticationScheme, entry => record.AuthenticationScheme = entry);
        Set(value.Subject, entry => record.Subject = entry);
        Set(value.Issuer, entry => record.Issuer = entry);
        Set(value.Database, entry => record.Database = entry);
        Set(value.Resource, entry => record.Resource = entry);
        Set(value.Outcome, entry => record.Outcome = entry);
        Set(value.Code, entry => record.Code = entry);
        Set(value.GrpcStatus, entry => record.GrpcStatus = entry);
        Set(value.TraceId, entry => record.TraceId = entry);
        Set(value.CommandId, entry => record.CommandId = entry);
        if (value.FirstEventId is { } first)
        {
            record.FirstEventId = first;
        }

        if (value.LastEventId is { } last)
        {
            record.LastEventId = last;
        }

        if (value.Revision is { } revision)
        {
            record.Revision = revision;
        }

        return record;
    }

    private static void Set(string? value, Action<string> assign)
    {
        if (value is not null)
        {
            assign(value);
        }
    }
}