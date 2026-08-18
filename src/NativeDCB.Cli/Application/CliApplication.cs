using Google.Protobuf;

using Grpc.Core;
using Grpc.Net.Client;

using NativeDCB.Cli.Arguments;
using NativeDCB.Cli.IO;
using NativeDCB.Cli.Presentation;
using NativeDCB.Protocol.V1;

namespace NativeDCB.Cli.Application;

internal static class CliApplication
{
    private const int Success = 0;
    private const int UsageError = 64;
    private const int DataError = 65;
    private const int Unavailable = 69;
    private const int SoftwareError = 70;
    private const int IoError = 74;
    private const int PermissionError = 77;
    private const int Cancelled = 130;
    private const string ErrorTrailerName = "native-dcb-error-bin";

    public static async Task<int> RunAsync(string[] rawArguments)
    {
        using ConsoleCancellation cancellation = new();

        try
        {
            CliArguments arguments = CliArguments.Parse(rawArguments);
            if (arguments.Flag("help") || arguments.Positionals.Count == 0)
            {
                return await PrintHelpAsync(arguments.Positionals);
            }

            if (arguments.Positionals.Count == 1 && HelpText.IsGroup(arguments.Positionals[index: 0]))
            {
                await Console.Error.WriteLineAsync(
                    $"Missing command after group '{arguments.Positionals[index: 0]}'.");
                await Console.Error.WriteLineAsync("Run nativedcb --help to list commands.");
                return UsageError;
            }

            if (arguments.Positionals.Count != 2)
            {
                throw new CliUsageException("Expected a command group and command name.");
            }

            string command = $"{arguments.Positionals[index: 0]} {arguments.Positionals[index: 1]}";
            if (!HelpText.TryGetCommand(command, out _))
            {
                throw new CliUsageException($"Unknown command '{command}'. Run nativedcb --help.");
            }

            string server = arguments.Optional("server")
                            ?? Environment.GetEnvironmentVariable("NATIVEDCB_SERVER")
                            ?? "http://localhost:5010";
            ValidateServer(server);

            using GrpcChannel channel = GrpcChannel.ForAddress(server);
            InputReader input = new(cancellation.Token);
            return await DispatchAsync(command, arguments, input, channel, cancellation.Token);
        }
        catch (CliUsageException exception)
        {
            await Console.Error.WriteLineAsync($"usage error: {exception.Message}");
            return UsageError;
        }
        catch (CliInputException exception)
        {
            await Console.Error.WriteLineAsync($"input error: {exception.Message}");
            return exception.InnerException is IOException or UnauthorizedAccessException ? IoError : DataError;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("cancelled");
            return Cancelled;
        }
        catch (RpcException exception)
        {
            return await ReportRpcErrorAsync(exception, cancellation.IsCancellationRequested);
        }
        catch (IOException exception)
        {
            await Console.Error.WriteLineAsync($"I/O error: {exception.Message}");
            return IoError;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"error: {exception.Message}");
            return SoftwareError;
        }
    }

    private static async Task<int> PrintHelpAsync(IReadOnlyList<string> positionals)
    {
        if (positionals.Count == 0)
        {
            await Console.Out.WriteLineAsync(HelpText.Main);
            return Success;
        }

        if (positionals.Count == 2 &&
            HelpText.TryGetCommand($"{positionals[index: 0]} {positionals[index: 1]}", out string text))
        {
            await Console.Out.WriteLineAsync(text);
            return Success;
        }

        await Console.Error.WriteLineAsync("Unknown help topic. Run nativedcb --help to list commands.");
        return UsageError;
    }

    private static async Task<int> DispatchAsync(
        string command,
        CliArguments arguments,
        InputReader input,
        GrpcChannel channel,
        CancellationToken cancellationToken)
    {
        DatabaseService.DatabaseServiceClient databases = new(channel);
        CatalogService.CatalogServiceClient catalog = new(channel);
        CommandService.CommandServiceClient commands = new(channel);
        EventService.EventServiceClient events = new(channel);
        StatementService.StatementServiceClient statements = new(channel);
        AdministrationService.AdministrationServiceClient administration = new(channel);

        switch (command)
        {
            case "database list":
                arguments.EnsureAllowed();
                return await UnaryAsync(databases.ListDatabasesAsync(
                    new ListDatabasesRequest(), cancellationToken: cancellationToken));
            case "database create":
                arguments.EnsureAllowed("database");
                return await UnaryAsync(databases.CreateDatabaseAsync(
                    new CreateDatabaseRequest { Database = arguments.Required("database") },
                    cancellationToken: cancellationToken));
            case "database info":
                arguments.EnsureAllowed("database");
                return await UnaryAsync(databases.GetDatabaseInfoAsync(
                    new GetDatabaseInfoRequest { Database = arguments.Required("database") },
                    cancellationToken: cancellationToken));
            case "database health":
                arguments.EnsureAllowed();
                return await UnaryAsync(databases.GetHealthAsync(
                    new GetHealthRequest(), cancellationToken: cancellationToken));
            case "database capabilities":
                arguments.EnsureAllowed();
                return await UnaryAsync(databases.GetCapabilitiesAsync(
                    new GetCapabilitiesRequest(), cancellationToken: cancellationToken));
            case "database head":
                arguments.EnsureAllowed("database");
                return await UnaryAsync(databases.GetHeadAsync(
                    new GetHeadRequest { Database = arguments.Required("database") },
                    cancellationToken: cancellationToken));
            case "catalog register-event-schema":
                return await RegisterSchemaAsync(arguments, input, catalog, SchemaKind.Event, cancellationToken);
            case "catalog register-command-schema":
                return await RegisterSchemaAsync(arguments, input, catalog, SchemaKind.Command, cancellationToken);
            case "catalog remove-schema":
                arguments.EnsureAllowed("database", "name", "kind");
                return await UnaryAsync(catalog.RemoveSchemaAsync(
                    new RemoveSchemaRequest
                    {
                        Database = arguments.Required("database"),
                        SchemaName = arguments.Required("name"),
                        SchemaKind = ParseSchemaKind(arguments.Required("kind"))
                    }, cancellationToken: cancellationToken));
            case "catalog get-schema":
                arguments.EnsureAllowed("database", "name", "kind");
                return await UnaryAsync(catalog.GetSchemaAsync(
                    new GetSchemaRequest
                    {
                        Database = arguments.Required("database"),
                        SchemaName = arguments.Required("name"),
                        SchemaKind = ParseSchemaKind(arguments.Required("kind"))
                    }, cancellationToken: cancellationToken));
            case "catalog list-schemas":
                arguments.EnsureAllowed("database", "kind");
                return await UnaryAsync(catalog.ListSchemasAsync(
                    new ListSchemasRequest
                    {
                        Database = arguments.Required("database"),
                        SchemaKind = ParseOptionalSchemaKind(arguments.Optional("kind"))
                    }, cancellationToken: cancellationToken));
            case "catalog register-handler":
                arguments.EnsureAllowed(
                    "database", "name", "command-type", "source", "source-file", "source-stdin",
                    "plan", "plan-file", "plan-stdin", "allow-incompatible");
                (string? source, ByteString plan) = await input.HandlerInputsAsync(arguments);
                return await UnaryAsync(catalog.RegisterHandlerAsync(
                    new RegisterHandlerRequest
                    {
                        Database = arguments.Required("database"),
                        HandlerName = arguments.Required("name"),
                        CommandType = arguments.Required("command-type"),
                        NdlSource = source ?? string.Empty,
                        PlanJson = plan,
                        AllowIncompatible = arguments.Flag("allow-incompatible")
                    }, cancellationToken: cancellationToken));
            case "catalog remove-handler":
                arguments.EnsureAllowed("database", "name");
                return await UnaryAsync(catalog.RemoveHandlerAsync(
                    new RemoveHandlerRequest
                    {
                        Database = arguments.Required("database"),
                        HandlerName = arguments.Required("name")
                    }, cancellationToken: cancellationToken));
            case "catalog get-handler":
                arguments.EnsureAllowed("database", "name", "include-plan", "generate-ndl");
                return await UnaryAsync(catalog.GetHandlerAsync(
                    new GetHandlerRequest
                    {
                        Database = arguments.Required("database"),
                        HandlerName = arguments.Required("name"),
                        IncludePlanJson = arguments.Flag("include-plan"),
                        GenerateNdl = arguments.Flag("generate-ndl")
                    }, cancellationToken: cancellationToken));
            case "catalog list-handlers":
                arguments.EnsureAllowed("database");
                return await UnaryAsync(catalog.ListHandlersAsync(
                    new ListHandlersRequest { Database = arguments.Required("database") },
                    cancellationToken: cancellationToken));
            case "catalog validate-ndl":
                arguments.EnsureAllowed(
                    "database", "ndl", "ndl-file", "ndl-stdin",
                    "transient", "transient-file", "transient-stdin");
                ValidateNdlRequest validateRequest = new()
                {
                    Database = arguments.Required("database"),
                    NdlSource = await input.RequiredTextAsync(arguments, "ndl")
                };
                validateRequest.TransientSchemas.AddRange(await input.TransientSchemasAsync(arguments));
                return await UnaryAsync(
                    catalog.ValidateNdlAsync(validateRequest, cancellationToken: cancellationToken),
                    response => response.Valid);
            case "command execute-handler":
                arguments.EnsureAllowed(
                    "database", "handler", "command-id", "command", "command-file", "command-stdin");
                ExecuteHandlerRequest executeRequest = new()
                {
                    Database = arguments.Required("database"),
                    HandlerName = arguments.Required("handler"),
                    CommandJson = await input.RequiredJsonAsync(arguments, "command")
                };
                string? commandId = arguments.Optional("command-id");
                if (commandId is not null)
                {
                    executeRequest.CommandId = commandId;
                }

                return await UnaryAsync(
                    commands.ExecuteHandlerAsync(executeRequest, cancellationToken: cancellationToken),
                    response => response.OutcomeCase is ExecuteHandlerResponse.OutcomeOneofCase.Committed or
                        ExecuteHandlerResponse.OutcomeOneofCase.AlreadyCommitted);
            case "command prepare-decision":
                arguments.EnsureAllowed(
                    "database", "handler", "command-id", "command", "command-file", "command-stdin");
                PrepareDecisionRequest prepareRequest = new()
                {
                    Database = arguments.Required("database"),
                    HandlerName = arguments.Required("handler"),
                    CommandJson = await input.RequiredJsonAsync(arguments, "command")
                };
                string? prepareCommandId = arguments.Optional("command-id");
                if (prepareCommandId is not null)
                {
                    prepareRequest.CommandId = prepareCommandId;
                }

                return await UnaryAsync(
                    commands.PrepareDecisionAsync(prepareRequest, cancellationToken: cancellationToken),
                    response => response.OutcomeCase is PrepareDecisionResponse.OutcomeOneofCase.Prepared or
                        PrepareDecisionResponse.OutcomeOneofCase.AlreadyCommitted);
            case "command complete-decision":
                arguments.EnsureAllowed(
                    "database", "signature", "signature-file", "signature-stdin",
                    "events", "events-file", "events-stdin");
                CompleteDecisionRequest completeRequest = new()
                {
                    Database = arguments.Required("database"),
                    ModelSignature = ParseBase64(
                        (await input.RequiredTextAsync(arguments, "signature")).Trim(),
                        "signature")
                };
                completeRequest.ProposedEvents.AddRange(await input.RequiredProposedEventsAsync(arguments));
                return await UnaryAsync(
                    commands.CompleteDecisionAsync(completeRequest, cancellationToken: cancellationToken),
                    response => response.OutcomeCase is CompleteDecisionResponse.OutcomeOneofCase.Committed or
                        CompleteDecisionResponse.OutcomeOneofCase.AlreadyCommitted);
            case "command events-by-command-id":
                arguments.EnsureAllowed("database", "command-id");
                return await UnaryAsync(commands.GetEventsByCommandIdAsync(
                    new GetEventsByCommandIdRequest
                    {
                        Database = arguments.Required("database"),
                        CommandId = arguments.Required("command-id")
                    }, cancellationToken: cancellationToken));
            case "event read-range":
                arguments.EnsureAllowed("database", "after", "through", "limit", "mode");
                (long rangeAfter, long? rangeThrough, uint? rangeLimit) = ParseBounds(arguments);
                ReadEventsByRangeRequest rangeRequest = new()
                {
                    Database = arguments.Required("database"),
                    AfterEventId = rangeAfter,
                    Mode = ParseReadMode(arguments.Optional("mode"))
                };
                SetBounds(rangeRequest, rangeThrough, rangeLimit);
                return await StreamAsync(events.ReadEventsByRange(
                    rangeRequest, cancellationToken: cancellationToken), cancellationToken);
            case "event read-query":
                arguments.EnsureAllowed(
                    "database", "query", "query-file", "query-stdin", "query-item", "event-type", "key",
                    "after", "through", "limit", "consistency");
                (long queryAfter, long? queryThrough, uint? queryLimit) = ParseBounds(arguments);
                ReadEventsByQueryRequest queryRequest = new()
                {
                    Database = arguments.Required("database"),
                    Query = await input.QueryAsync(arguments, required: true),
                    AfterEventId = queryAfter,
                    Consistency = ParseConsistency(arguments.Optional("consistency"))
                };
                SetBounds(queryRequest, queryThrough, queryLimit);
                return await StreamAsync(events.ReadEventsByQuery(
                    queryRequest, cancellationToken: cancellationToken), cancellationToken);
            case "event read-type-and-keys":
                arguments.EnsureAllowed(
                    "database", "event-type", "key", "after", "through", "limit", "consistency");
                (long typeAfter, long? typeThrough, uint? typeLimit) = ParseBounds(arguments);
                ReadEventsByTypeAndKeysRequest typeRequest = new()
                {
                    Database = arguments.Required("database"),
                    EventType = arguments.Required("event-type"),
                    AfterEventId = typeAfter,
                    Consistency = ParseConsistency(arguments.Optional("consistency"))
                };
                typeRequest.Keys.AddRange(InputReader.ParseKeys(arguments.Many("key")));
                SetBounds(typeRequest, typeThrough, typeLimit);
                return await StreamAsync(events.ReadEventsByTypeAndKeys(
                    typeRequest, cancellationToken: cancellationToken), cancellationToken);
            case "event subscribe":
                arguments.EnsureAllowed(
                    "database", "after", "query", "query-file", "query-stdin", "query-item", "event-type",
                    "query-key", "key");
                SubscribeEventsRequest subscribeRequest = new()
                {
                    Database = arguments.Required("database"),
                    AfterEventId = arguments.Int64("after", defaultValue: 0, nonNegative: true)
                };
                Query? subscriptionQuery = await input.QueryAsync(arguments, required: false, "query-key");
                if (subscriptionQuery is not null)
                {
                    subscribeRequest.Query = subscriptionQuery;
                }

                subscribeRequest.Keys.AddRange(InputReader.ParseKeys(arguments.Many("key")));
                return await StreamAsync(events.SubscribeEvents(
                    subscribeRequest, cancellationToken: cancellationToken), cancellationToken);
            case "statement execute":
                arguments.EnsureAllowed("database", "ndl", "ndl-file", "ndl-stdin", "allow-incompatible");
                ExecuteStatementRequest statementRequest = new()
                {
                    Database = arguments.Required("database"),
                    NdlSource = await input.RequiredTextAsync(arguments, "ndl"),
                    AllowIncompatible = arguments.Flag("allow-incompatible")
                };
                return await StreamAsync(
                    statements.ExecuteStatement(statementRequest, cancellationToken: cancellationToken),
                    cancellationToken,
                    result => result.ResultCase != StatementResult.ResultOneofCase.Completion ||
                              result.Completion.Succeeded);
            case "statement explain":
                arguments.EnsureAllowed("database", "ndl", "ndl-file", "ndl-stdin");
                return await UnaryAsync(
                    statements.ExplainStatementAsync(
                        new ExplainStatementRequest
                        {
                            Database = arguments.Required("database"),
                            NdlSource = await input.RequiredTextAsync(arguments, "ndl")
                        }, cancellationToken: cancellationToken), response => response.Valid);
            case "admin list-partitions":
                arguments.EnsureAllowed("database");
                return await UnaryAsync(administration.ListPartitionsAsync(
                    new ListPartitionsRequest { Database = arguments.Required("database") },
                    cancellationToken: cancellationToken));
            case "admin list-indexes":
                arguments.EnsureAllowed("database");
                return await UnaryAsync(administration.ListIndexesAsync(
                    new ListIndexesRequest { Database = arguments.Required("database") },
                    cancellationToken: cancellationToken));
            case "admin state-file-status":
                arguments.EnsureAllowed("database", "partition");
                return await UnaryAsync(administration.GetStateFileStatusAsync(
                    new GetStateFileStatusRequest
                    {
                        Database = arguments.Required("database"),
                        PartitionNumber = arguments.RequiredUInt32("partition")
                    }, cancellationToken: cancellationToken));
            case "admin rebuild-index":
                arguments.EnsureAllowed("database", "event-type", "key");
                RequestIndexRebuildRequest indexRequest = new()
                {
                    Database = arguments.Required("database"),
                    EventType = arguments.Required("event-type")
                };
                indexRequest.Keys.AddRange(InputReader.ParseKeys(arguments.Many("key")));
                return await UnaryAsync(
                    administration.RequestIndexRebuildAsync(indexRequest, cancellationToken: cancellationToken),
                    response => response.Accepted);
            case "admin rebuild-state":
                arguments.EnsureAllowed("database", "partition");
                return await UnaryAsync(
                    administration.RequestStateRebuildAsync(
                        new RequestStateRebuildRequest
                        {
                            Database = arguments.Required("database"),
                            PartitionNumber = arguments.RequiredUInt32("partition")
                        }, cancellationToken: cancellationToken),
                    response => response.Accepted);
            default:
                throw new CliUsageException($"Unknown command '{command}'.");
        }
    }

    private static async Task<int> RegisterSchemaAsync(
        CliArguments arguments,
        InputReader input,
        CatalogService.CatalogServiceClient catalog,
        SchemaKind kind,
        CancellationToken cancellationToken)
    {
        arguments.EnsureAllowed(
            "database", "name", "schema", "schema-file", "schema-stdin", "allow-incompatible");
        RegisterSchemaRequest request = new()
        {
            Database = arguments.Required("database"),
            SchemaName = arguments.Required("name"),
            SchemaDocumentJson = await input.RequiredJsonAsync(arguments, "schema"),
            AllowIncompatible = arguments.Flag("allow-incompatible")
        };
        AsyncUnaryCall<RegisterSchemaResponse> call = kind == SchemaKind.Event
            ? catalog.RegisterEventSchemaAsync(request, cancellationToken: cancellationToken)
            : catalog.RegisterCommandSchemaAsync(request, cancellationToken: cancellationToken);
        return await UnaryAsync(
            call,
            response => response.Diagnostics.All(diagnostic => diagnostic.Severity != DiagnosticSeverity.Error));
    }

    private static async Task<int> UnaryAsync<T>(AsyncUnaryCall<T> call, Func<T, bool>? successful = null)
        where T : IMessage<T>
    {
        using (call)
        {
            T response = await call.ResponseAsync;
            await Console.Out.WriteLineAsync(JsonFormatter.Default.Format(response));
            return successful is null || successful(response) ? Success : DataError;
        }
    }

    private static async Task<int> StreamAsync<T>(
        AsyncServerStreamingCall<T> call,
        CancellationToken cancellationToken,
        Func<T, bool>? successful = null)
        where T : IMessage<T>
    {
        bool allSuccessful = true;
        using (call)
        {
            while (await call.ResponseStream.MoveNext(cancellationToken))
            {
                T item = call.ResponseStream.Current;
                await Console.Out.WriteLineAsync(JsonFormatter.Default.Format(item));
                allSuccessful &= successful?.Invoke(item) ?? true;
            }
        }

        return allSuccessful ? Success : DataError;
    }

    private static (long After, long? Through, uint? Limit) ParseBounds(CliArguments arguments)
    {
        long after = arguments.Int64("after", defaultValue: 0, nonNegative: true);
        long? through = arguments.OptionalInt64("through", nonNegative: true);
        if (through.HasValue && through.Value <= after)
        {
            throw new CliUsageException("Option --through must be greater than --after.");
        }

        uint? limit = arguments.OptionalUInt32("limit");
        if (limit > int.MaxValue)
        {
            throw new CliUsageException($"Option --limit cannot exceed {int.MaxValue}.");
        }

        return (after, through, limit);
    }

    private static void SetBounds(ReadEventsByRangeRequest request, long? through, uint? limit)
    {
        if (through.HasValue)
        {
            request.ThroughEventId = through.Value;
        }

        if (limit.HasValue)
        {
            request.Limit = limit.Value;
        }
    }

    private static void SetBounds(ReadEventsByQueryRequest request, long? through, uint? limit)
    {
        if (through.HasValue)
        {
            request.ThroughEventId = through.Value;
        }

        if (limit.HasValue)
        {
            request.Limit = limit.Value;
        }
    }

    private static void SetBounds(ReadEventsByTypeAndKeysRequest request, long? through, uint? limit)
    {
        if (through.HasValue)
        {
            request.ThroughEventId = through.Value;
        }

        if (limit.HasValue)
        {
            request.Limit = limit.Value;
        }
    }

    private static ReadMode ParseReadMode(string? value)
    {
        return value switch
        {
            null or "snapshot" => ReadMode.Snapshot,
            "follow" => ReadMode.Follow,
            _ => throw new CliUsageException("Option --mode must be snapshot or follow.")
        };
    }

    private static QueryConsistency ParseConsistency(string? value)
    {
        return value switch
        {
            null or "committed-scan" or "committed" => QueryConsistency.CommittedScan,
            "eventual-index" or "eventual" => QueryConsistency.EventualIndex,
            _ => throw new CliUsageException(
                "Option --consistency must be eventual-index or committed-scan.")
        };
    }

    private static SchemaKind ParseSchemaKind(string value)
    {
        return value switch
        {
            "event" => SchemaKind.Event,
            "command" => SchemaKind.Command,
            _ => throw new CliUsageException("Option --kind must be event or command.")
        };
    }

    private static SchemaKind ParseOptionalSchemaKind(string? value)
    {
        return value is null ? SchemaKind.Unspecified : ParseSchemaKind(value);
    }

    private static ByteString ParseBase64(string value, string option)
    {
        try
        {
            return ByteString.CopyFrom(Convert.FromBase64String(value));
        }
        catch (FormatException exception)
        {
            throw new CliInputException($"Invalid base64 for --{option}: {exception.Message}", exception);
        }
    }

    private static void ValidateServer(string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new CliUsageException("--server must be an absolute http:// or https:// URL.");
        }
    }

    private static async Task<int> ReportRpcErrorAsync(RpcException exception, bool cancellationRequested)
    {
        if (cancellationRequested || exception.StatusCode == StatusCode.Cancelled)
        {
            await Console.Error.WriteLineAsync("cancelled");
            return Cancelled;
        }

        await Console.Error.WriteLineAsync($"RPC {exception.StatusCode}: {exception.Status.Detail}");
        Metadata.Entry? detailEntry =
            exception.Trailers.FirstOrDefault(entry => entry is { IsBinary: true, Key: ErrorTrailerName });
        if (detailEntry is not null)
        {
            try
            {
                ErrorDetail detail = ErrorDetail.Parser.ParseFrom(detailEntry.ValueBytes);
                await Console.Error.WriteLineAsync(JsonFormatter.Default.Format(detail));
            }
            catch (InvalidProtocolBufferException)
            {
                await Console.Error.WriteLineAsync($"Invalid {ErrorTrailerName} trailer received from server.");
            }
        }

        return exception.StatusCode switch
        {
            StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.OutOfRange or
                StatusCode.AlreadyExists or StatusCode.NotFound => DataError,
            StatusCode.Unauthenticated or StatusCode.PermissionDenied => PermissionError,
            StatusCode.Unavailable or StatusCode.DeadlineExceeded => Unavailable,
            _ => SoftwareError
        };
    }

    private sealed class ConsoleCancellation : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _source = new();
        private bool _disposed;

        public ConsoleCancellation()
        {
            Console.CancelKeyPress += OnCancelKeyPress;
        }

        public CancellationToken Token => _source.Token;

        public bool IsCancellationRequested => _source.IsCancellationRequested;

        public void Dispose()
        {
            Console.CancelKeyPress -= OnCancelKeyPress;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _source.Dispose();
            }
        }

        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;

            lock (_gate)
            {
                if (!_disposed)
                {
                    _source.Cancel();
                }
            }
        }
    }
}