using System.Text;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using NativeDCB.Engine;
using NativeDCB.Engine.Actors;
using NativeDCB.Model;
using NativeDCB.Ndl;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Storage;

using DiagnosticSeverity = NativeDCB.Ndl.DiagnosticSeverity;
using ProtocolDatabaseInfo = NativeDCB.Protocol.V1.DatabaseInfo;
using ProtocolDiagnostic = NativeDCB.Protocol.V1.Diagnostic;
using ProtocolPartitionStatus = NativeDCB.Protocol.V1.PartitionStatus;
using QueryItem = NativeDCB.Model.QueryItem;

namespace NativeDCB.Server.Services;

internal static class ProtocolMapper
{
    private const string ErrorDetailTrailerName = "native-dcb-error-bin";

    public static EventEnvelope ToEnvelope(SequencedEvent value)
    {
        EventEnvelope result = new()
        {
            EventId = value.EventId,
            Type = value.Type,
            SchemaVersion = value.SchemaVersion,
            DataJson = ByteString.CopyFromUtf8(value.Data.GetRawText()),
            TimestampUtc = Timestamp.FromDateTimeOffset(value.TimestampUtc),
            CommandId = value.CommandId.ToString("D"),
            CommandType = value.CommandType
        };
        result.Keys.AddRange(value.Keys.Select(ToKeyValue));
        return result;
    }

    private static KeyValue ToKeyValue(EventKey value)
    {
        return new KeyValue { Key = value.Name, Value = value.Value };
    }

    public static EventKey ToEventKey(KeyValue value)
    {
        if (string.IsNullOrWhiteSpace(value.Key) || string.IsNullOrWhiteSpace(value.Value))
        {
            throw InvalidArgument("Query keys must have non-empty names and values.");
        }

        return new EventKey(value.Key, value.Value);
    }

    public static EventQuery ToQuery(Query? value)
    {
        if (value is null || value.Items.Count == 0)
        {
            return EventQuery.All;
        }

        List<QueryItem> items = new(value.Items.Count);
        foreach (Protocol.V1.QueryItem item in value.Items)
        {
            if (item.EventTypes.Count == 0 && item.Keys.Count == 0)
            {
                throw InvalidArgument("A query item must contain an event type or key.");
            }

            if (item.EventTypes.Any(string.IsNullOrWhiteSpace) ||
                item.EventTypes.Count != item.EventTypes.Distinct(StringComparer.Ordinal).Count())
            {
                throw InvalidArgument("Query event types must be non-empty and unique within an item.");
            }

            EventKey[] keys = item.Keys.Select(ToEventKey).ToArray();
            if (keys.Length != keys.Distinct().Count())
            {
                throw InvalidArgument("Query keys must be unique within an item.");
            }

            items.Add(new QueryItem(item.EventTypes.ToArray(), keys));
        }

        return new EventQuery(items);
    }

    public static ProtocolDatabaseInfo ToDatabaseInfo(DatabaseEntry entry, WriterStateMessage? state = null)
    {
        ProtocolDatabaseInfo info = new()
        {
            Database = entry.Name,
            State = ToDatabaseState(entry.Status),
            DatabaseVersion = "1",
            FileFormatVersion = "1",
            MainHead = state?.Head ?? entry.Head,
            ActivePartitionIndex = checked((uint)(state?.ActivePartition ?? entry.ActivePartition)),
            WriterLockOwned = entry.IsOpen,
            ReadAvailable = entry.ReadAvailable,
            WriteAvailable = entry.WriteAvailable
        };
        if (entry.LastFault is { } fault)
        {
            info.LastFault = new ErrorDetail { Code = "DatabaseFaulted", Message = fault };
        }

        info.CatalogFingerprints.AddRange(
            entry.Catalog.EventSchemas.Values.Select(x => Fingerprint("event-schema", x.Name, x.Fingerprint)));
        info.CatalogFingerprints.AddRange(
            entry.Catalog.CommandSchemas.Values.Select(x => Fingerprint("command-schema", x.Name, x.Fingerprint)));
        info.CatalogFingerprints.AddRange(
            entry.Catalog.Handlers.Values.Select(x => Fingerprint("handler", x.Name, x.PlanFingerprint)));
        return info;
    }

    public static HandlerDescription ToHandler(HandlerCatalogEntry value)
    {
        return new HandlerDescription
        {
            HandlerName = value.Name,
            CommandType = value.CommandType,
            NdlSource = value.NdlSource,
            SourceFingerprint = value.SourceFingerprint,
            PlanFingerprint = value.PlanFingerprint,
            Valid = true
        };
    }

    public static SchemaDescription ToSchema(SchemaCatalogEntry value, SchemaKind kind)
    {
        return new SchemaDescription
        {
            SchemaName = value.Name,
            SchemaKind = kind,
            Fingerprint = value.Fingerprint,
            SchemaDocumentJson = ByteString.CopyFromUtf8(value.DocumentJson)
        };
    }

    public static SchemaSummary ToSchemaSummary(SchemaCatalogEntry value, SchemaKind kind)
    {
        return new SchemaSummary { SchemaName = value.Name, SchemaKind = kind, Fingerprint = value.Fingerprint };
    }

    public static HandlerSummary ToHandlerSummary(HandlerCatalogEntry value)
    {
        return new HandlerSummary
        {
            HandlerName = value.Name,
            CommandType = value.CommandType,
            SourceFingerprint = value.SourceFingerprint,
            PlanFingerprint = value.PlanFingerprint,
            Valid = true
        };
    }

    public static ProtocolDiagnostic ToDiagnostic(NdlDiagnostic value, SourceText source)
    {
        LinePosition start = source.GetLinePosition(value.Span.Start);
        LinePosition end = source.GetLinePosition(value.Span.End);
        return new ProtocolDiagnostic
        {
            Code = value.Code,
            Severity = value.Severity == DiagnosticSeverity.Error
                ? Protocol.V1.DiagnosticSeverity.Error
                : Protocol.V1.DiagnosticSeverity.Warning,
            Message = value.Message,
            SourceSpan = new SourceSpan
            {
                Start = Position(start, value.Span.Start),
                End = Position(end, value.Span.End)
            }
        };
    }

    public static PlanSummary ToPlan(ParseResult parsed)
    {
        PlanSummary plan = new()
        {
            PlanFingerprint = DatabaseRegistry.Fingerprint(Ndl.Ndl.Format(parsed.Document)),
            RedactedSummary = $"{parsed.Document.Decisions.Count} decision(s)"
        };
        plan.Operations.AddRange(parsed.Document.Decisions.SelectMany(x => new[]
        {
            $"decision:{x.Name}", $"include:{x.Includes.Count}", $"emit:{x.Decide.Emissions.Count}"
        }));
        return plan;
    }

    public static RpcException InvalidArgument(string message)
    {
        return Error(StatusCode.InvalidArgument, "InvalidArgument", message);
    }

    public static RpcException NotFound(string message)
    {
        return Error(StatusCode.NotFound, "NotFound", message);
    }

    public static RpcException AlreadyExists(string message)
    {
        return Error(StatusCode.AlreadyExists, "AlreadyExists", message);
    }

    public static RpcException FailedPrecondition(string message)
    {
        return Error(StatusCode.FailedPrecondition, "FailedPrecondition", message);
    }

    public static RpcException Unavailable(string message)
    {
        return Error(StatusCode.Unavailable, "Unavailable", message);
    }

    public static RpcException DataLoss(string message)
    {
        return Error(StatusCode.DataLoss, "DataLoss", message);
    }

    public static RpcException ResourceExhausted(string message)
    {
        return Error(StatusCode.ResourceExhausted, "ResourceExhausted", message);
    }

    public static RpcException Internal(string message)
    {
        return Error(StatusCode.Internal, "Internal", message);
    }

    public static byte[] Utf8(ByteString bytes)
    {
        return bytes.ToByteArray();
    }

    public static string Utf8Text(ByteString bytes)
    {
        return Encoding.UTF8.GetString(bytes.Span);
    }

    private static RpcException Error(StatusCode statusCode, string code, string message)
    {
        ErrorDetail detail = new() { Code = code, Message = message, DetailsJson = ByteString.CopyFromUtf8("{}") };
        Metadata trailers = new() { { ErrorDetailTrailerName, detail.ToByteArray() } };
        return new RpcException(new Status(statusCode, message), trailers);
    }

    public static async Task WriteAsync(
        IServerStreamWriter<EventEnvelope> stream,
        IEnumerable<SequencedEvent> events,
        uint? limit,
        CancellationToken cancellationToken)
    {
        IEnumerable<SequencedEvent> selected = limit is > 0 ? events.Take(checked((int)limit.Value)) : events;
        foreach (SequencedEvent item in selected)
        {
            await stream.WriteAsync(ToEnvelope(item), cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    public static ProtocolPartitionStatus ToPartitionStatus(
        PartitionStatusMessage value,
        StateFileStatus stateFile)
    {
        return new ProtocolPartitionStatus
        {
            PartitionNumber = checked((uint)value.PartitionNumber),
            Filename = Path.GetFileName(value.FilePath),
            Active = value.IsActive,
            FirstEventId = value.FirstEventId ?? 0,
            LastEventId = value.LastEventId ?? 0,
            EventCount = checked((ulong)value.CommittedEventCount),
            StateFile = stateFile
        };
    }

    public static StateFileStatus ToStateFileStatus(StateFileInspection value)
    {
        return new StateFileStatus
        {
            Present = value.Present,
            Locked = value.Locked,
            Building = value.Locked,
            JsonValid = value.JsonValid,
            SchemaValid = value.SchemaValid,
            SourcePartition = checked((uint)value.SourcePartition),
            LastBuildError = value.Error is null
                ? null
                : new ErrorDetail { Code = "StateFileInvalid", Message = value.Error }
        };
    }

    public static IndexStatus ToIndexStatus(IndexStatusMessage value)
    {
        IndexStatus result = new()
        {
            EventType = value.EventType,
            Filename = Path.GetFileName(value.FilePath),
            IndexHead = value.IndexHead,
            MainHead = value.MainHead,
            Lag = checked((ulong)Math.Max(val1: 0, value.MainHead - value.IndexHead)),
            HydrationState = value.HydrationState switch
            {
                IndexHydrationState.Pending => HydrationState.Pending,
                IndexHydrationState.Hydrating => HydrationState.Hydrating,
                IndexHydrationState.Ready => HydrationState.Ready,
                IndexHydrationState.Faulted => HydrationState.Faulted,
                _ => HydrationState.Unspecified
            }
        };
        result.Keys.Add(ToKeyValue(new EventKey(value.Key.Name, value.Key.Value)));
        if (value.LastFault is not null)
        {
            result.LastFault = new ErrorDetail { Code = "IndexFaulted", Message = value.LastFault };
        }

        return result;
    }

    private static CatalogFingerprint Fingerprint(string kind, string name, string value)
    {
        return new CatalogFingerprint { Kind = kind, Name = name, Fingerprint = value };
    }

    public static DatabaseState ToDatabaseState(DatabaseStatus value)
    {
        return value switch
        {
            DatabaseStatus.Discovered => DatabaseState.Discovered,
            DatabaseStatus.AcquiringLock => DatabaseState.AcquiringLock,
            DatabaseStatus.Recovering => DatabaseState.Recovering,
            DatabaseStatus.Ready => DatabaseState.Ready,
            DatabaseStatus.Draining => DatabaseState.Draining,
            DatabaseStatus.Stopped => DatabaseState.Stopped,
            DatabaseStatus.Faulted => DatabaseState.Faulted,
            _ => DatabaseState.Unspecified
        };
    }

    private static SourcePosition Position(LinePosition value, int offset)
    {
        return new SourcePosition
        {
            Line = checked((uint)(value.Line + 1)),
            Column = checked((uint)(value.Character + 1)),
            Offset = checked((uint)offset)
        };
    }
}