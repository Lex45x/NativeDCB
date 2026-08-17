using System.Collections.Concurrent;
using System.Text.Json;

using Grpc.Core;

using NativeDCB.Model;
using NativeDCB.Ndl;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Runtime;
using NativeDCB.Server.Storage;

using DiagnosticSeverity = NativeDCB.Protocol.V1.DiagnosticSeverity;

namespace NativeDCB.Server.Services;

public sealed class CatalogGrpcService(DatabaseRegistry registry) : CatalogService.CatalogServiceBase
{
    public override Task<RegisterSchemaResponse> RegisterEventSchema(RegisterSchemaRequest request,
        ServerCallContext context)
    {
        return RegisterSchemaAsync(request, eventSchema: true, context.CancellationToken);
    }

    public override Task<RegisterSchemaResponse> RegisterCommandSchema(RegisterSchemaRequest request,
        ServerCallContext context)
    {
        return RegisterSchemaAsync(request, eventSchema: false, context.CancellationToken);
    }

    public override async Task<RemoveSchemaResponse> RemoveSchema(RemoveSchemaRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        EnsureCatalogWritable(database);
        ConcurrentDictionary<string, SchemaCatalogEntry> schemas = request.SchemaKind switch
        {
            SchemaKind.Event => database.Catalog.EventSchemas,
            SchemaKind.Command => database.Catalog.CommandSchemas,
            _ => throw ProtocolMapper.InvalidArgument("A schema kind is required.")
        };

        await database.CatalogLock.WaitAsync(context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            if (!schemas.TryRemove(request.SchemaName, out SchemaCatalogEntry? removed))
            {
                throw ProtocolMapper.NotFound($"Schema '{request.SchemaName}' was not found.");
            }

            try
            {
                await database.SaveCatalogAsync(context.CancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                return new RemoveSchemaResponse { RemovedFingerprint = removed.Fingerprint };
            }
            catch
            {
                schemas[request.SchemaName] = removed;
                throw;
            }
        }
        finally
        {
            database.CatalogLock.Release();
        }
    }

    public override async Task<GetSchemaResponse> GetSchema(GetSchemaRequest request, ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ConcurrentDictionary<string, SchemaCatalogEntry> schemas = Schemas(database, request.SchemaKind);
        if (!schemas.TryGetValue(request.SchemaName, out SchemaCatalogEntry? schema))
        {
            throw ProtocolMapper.NotFound($"Schema '{request.SchemaName}' was not found.");
        }

        return new GetSchemaResponse { Schema = ProtocolMapper.ToSchema(schema, request.SchemaKind) };
    }

    public override async Task<ListSchemasResponse> ListSchemas(ListSchemasRequest request, ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        IEnumerable<SchemaSummary> schemas = request.SchemaKind switch
        {
            SchemaKind.Unspecified => database.Catalog.EventSchemas.Values
                .Select(value => ProtocolMapper.ToSchemaSummary(value, SchemaKind.Event))
                .Concat(database.Catalog.CommandSchemas.Values.Select(value =>
                    ProtocolMapper.ToSchemaSummary(value, SchemaKind.Command))),
            SchemaKind.Event => database.Catalog.EventSchemas.Values.Select(value =>
                ProtocolMapper.ToSchemaSummary(value, SchemaKind.Event)),
            SchemaKind.Command => database.Catalog.CommandSchemas.Values.Select(value =>
                ProtocolMapper.ToSchemaSummary(value, SchemaKind.Command)),
            _ => throw ProtocolMapper.InvalidArgument("The schema kind is invalid.")
        };
        ListSchemasResponse response = new();
        response.Schemas.AddRange(schemas
            .OrderBy(value => value.SchemaKind)
            .ThenBy(value => value.SchemaName, StringComparer.Ordinal));
        return response;
    }

    public override async Task<RegisterHandlerResponse> RegisterHandler(
        RegisterHandlerRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        EnsureCatalogWritable(database);
        if (string.IsNullOrWhiteSpace(request.HandlerName) || string.IsNullOrWhiteSpace(request.CommandType))
        {
            throw ProtocolMapper.InvalidArgument("Handler name and command type are required.");
        }

        Dictionary<string, RegisteredJsonSchema> eventSchemas = ParseEventSchemas(database);
        HandlerDescription description = new()
        {
            HandlerName = request.HandlerName,
            CommandType = request.CommandType,
            NdlSource = request.NdlSource
        };
        string planJson = string.Empty;
        if (request.PlanJson.Length > 0)
        {
            try
            {
                DecisionPlan plan = JsonSerializer.Deserialize<DecisionPlan>(
                                        request.PlanJson.Span, DatabaseRegistry.JsonOptions)
                                    ?? throw new JsonException("The decision plan is empty.");
                foreach (string error in ValidatePlan(plan, request, eventSchemas, database))
                {
                    description.Diagnostics.Add(ErrorDiagnostic("PLAN2001", error));
                }

                planJson = JsonSerializer.Serialize(plan, DatabaseRegistry.JsonOptions);
                description.SourceFingerprint = plan.Fingerprints.SourceFingerprint;
            }
            catch (JsonException exception)
            {
                description.Diagnostics.Add(ErrorDiagnostic("PLAN1001", exception.Message));
            }
        }
        else
        {
            ParseResult parsed = Ndl.Ndl.Parse(request.NdlSource);
            description.SourceFingerprint = DatabaseRegistry.Fingerprint(request.NdlSource);
            description.Diagnostics.AddRange(parsed.Diagnostics.Select(x =>
                ProtocolMapper.ToDiagnostic(x, parsed.Source)));
            IReadOnlyList<string> semanticErrors = parsed.HasErrors
                ? []
                : NdlDecisionRuntime.ValidateDecision(parsed, request.CommandType, eventSchemas);
            foreach (string error in semanticErrors)
            {
                description.Diagnostics.Add(ErrorDiagnostic("NDL2001", error));
            }

            if (!parsed.HasErrors && semanticErrors.Count == 0)
            {
                CompilationResult compilation = Ndl.Ndl.Compile(request.NdlSource);
                DecisionPlan plan = AssertSinglePlan(compilation, request.HandlerName) with
                {
                    Fingerprints = compilation.Plans[index: 0].Fingerprints with
                    {
                        SchemaFingerprints = database.Catalog.EventSchemas.Values
                            .Concat(database.Catalog.CommandSchemas.Values)
                            .ToDictionary(schema => schema.Name, schema => schema.Fingerprint, StringComparer.Ordinal)
                    }
                };
                planJson = JsonSerializer.Serialize(plan, DatabaseRegistry.JsonOptions);
            }
        }

        description.Valid = description.Diagnostics.All(x => x.Severity != DiagnosticSeverity.Error);
        if (!description.Valid)
        {
            return new RegisterHandlerResponse { Handler = description };
        }

        description.PlanFingerprint = DatabaseRegistry.Fingerprint(planJson);
        HandlerCatalogEntry registration = new(
            request.HandlerName,
            request.CommandType,
            request.NdlSource,
            description.SourceFingerprint,
            description.PlanFingerprint,
            planJson);
        await database.CatalogLock.WaitAsync(context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            bool hadPrevious = database.Catalog.Handlers.TryGetValue(
                request.HandlerName, out HandlerCatalogEntry? previous);
            database.Catalog.Handlers[request.HandlerName] = registration;
            try
            {
                await database.SaveCatalogAsync(context.CancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
            }
            catch
            {
                if (hadPrevious)
                {
                    database.Catalog.Handlers[request.HandlerName] = previous!;
                }
                else
                {
                    database.Catalog.Handlers.TryRemove(request.HandlerName, out _);
                }

                throw;
            }
        }
        finally
        {
            database.CatalogLock.Release();
        }

        return new RegisterHandlerResponse { Handler = description };
    }

    public override async Task<RemoveHandlerResponse> RemoveHandler(RemoveHandlerRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        EnsureCatalogWritable(database);
        await database.CatalogLock.WaitAsync(context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            if (!database.Catalog.Handlers.TryRemove(request.HandlerName, out HandlerCatalogEntry? removed))
            {
                throw ProtocolMapper.NotFound($"Handler '{request.HandlerName}' was not found.");
            }

            try
            {
                await database.SaveCatalogAsync(context.CancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                return new RemoveHandlerResponse
                {
                    RemovedSourceFingerprint = removed.SourceFingerprint,
                    RemovedPlanFingerprint = removed.PlanFingerprint
                };
            }
            catch
            {
                database.Catalog.Handlers[request.HandlerName] = removed;
                throw;
            }
        }
        finally
        {
            database.CatalogLock.Release();
        }
    }

    public override async Task<GetHandlerResponse> GetHandler(GetHandlerRequest request, ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (!database.Catalog.Handlers.TryGetValue(request.HandlerName, out HandlerCatalogEntry? handler))
        {
            throw ProtocolMapper.NotFound($"Handler '{request.HandlerName}' was not found.");
        }

        HandlerDescription description = ProtocolMapper.ToHandler(handler);
        if (request.IncludePlanJson)
        {
            description.PlanJson = Google.Protobuf.ByteString.CopyFromUtf8(handler.PlanJson);
        }

        if (request.GenerateNdl)
        {
            NdlPlanFormatResult generated = Ndl.Ndl.TryFormat(handler.ParsePlan());
            if (generated.Success)
            {
                description.GeneratedNdl = generated.NdlSource;
            }
            else
            {
                description.NdlGenerationDiagnostics.AddRange(generated.Diagnostics.Select(value => new Diagnostic
                {
                    Code = "NDL3001",
                    Severity = DiagnosticSeverity.Error,
                    Message = $"{value.Path}: {value.Message}"
                }));
            }
        }

        return new GetHandlerResponse { Handler = description };
    }

    public override async Task<ListHandlersResponse> ListHandlers(ListHandlersRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ListHandlersResponse response = new();
        response.Handlers.AddRange(database.Catalog.Handlers.Values
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(ProtocolMapper.ToHandlerSummary));
        return response;
    }

    public override async Task<ValidateNdlResponse> ValidateNdl(ValidateNdlRequest request, ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(
            request.Database, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        Dictionary<string, RegisteredJsonSchema> eventSchemas = ParseEventSchemas(database);
        foreach (TransientSchema transient in request.TransientSchemas)
        {
            if (transient.SchemaKind == SchemaKind.Event)
            {
                string document = ProtocolMapper.Utf8Text(transient.SchemaDocumentJson);
                eventSchemas[transient.SchemaName] = RegisteredJsonSchema.Parse(
                    transient.SchemaName, document, eventSchema: true);
            }
            else if (transient.SchemaKind != SchemaKind.Command)
            {
                throw ProtocolMapper.InvalidArgument("A transient schema kind is required.");
            }
        }

        ParseResult parsed = Ndl.Ndl.Parse(request.NdlSource);
        ValidateNdlResponse response = new() { Valid = !parsed.HasErrors };
        response.Diagnostics.AddRange(parsed.Diagnostics.Select(x => ProtocolMapper.ToDiagnostic(x, parsed.Source)));
        if (!parsed.HasErrors)
        {
            foreach (string error in NdlDecisionRuntime.ValidateDecision(parsed, eventSchemas: eventSchemas))
            {
                response.Diagnostics.Add(ErrorDiagnostic("NDL2001", error));
            }

            response.Valid = response.Diagnostics.All(x => x.Severity != DiagnosticSeverity.Error);
            if (response.Valid)
            {
                response.Plan = ProtocolMapper.ToPlan(parsed);
            }
        }

        return response;
    }

    private async Task<RegisterSchemaResponse> RegisterSchemaAsync(
        RegisterSchemaRequest request,
        bool eventSchema,
        CancellationToken cancellationToken)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        EnsureCatalogWritable(database);
        if (string.IsNullOrWhiteSpace(request.SchemaName))
        {
            throw ProtocolMapper.InvalidArgument("A schema name is required.");
        }

        string document = ProtocolMapper.Utf8Text(request.SchemaDocumentJson);
        RegisteredJsonSchema parsedSchema;
        try
        {
            parsedSchema = RegisteredJsonSchema.Parse(request.SchemaName, document, eventSchema);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw ProtocolMapper.InvalidArgument($"Invalid schema JSON: {exception.Message}");
        }

        string fingerprint = DatabaseRegistry.Fingerprint(document);
        SchemaCatalogEntry entry = new(request.SchemaName, document, fingerprint);
        await database.CatalogLock.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            ConcurrentDictionary<string, SchemaCatalogEntry> schemas = eventSchema
                ? database.Catalog.EventSchemas
                : database.Catalog.CommandSchemas;
            bool hadPrevious = schemas.TryGetValue(request.SchemaName, out SchemaCatalogEntry? previous);
            if (previous is not null)
            {
                RegisteredJsonSchema previousSchema = RegisteredJsonSchema.Parse(
                    previous.Name, previous.DocumentJson, eventSchema);
                IReadOnlyList<string> incompatibilities = parsedSchema.CompatibilityErrors(previousSchema);
                if (incompatibilities.Count > 0 && !request.AllowIncompatible)
                {
                    RegisterSchemaResponse incompatible = new() { Fingerprint = fingerprint };
                    incompatible.Diagnostics.AddRange(incompatibilities.Select(message =>
                        ErrorDiagnostic("SCHEMA2001", message)));
                    return incompatible;
                }
            }

            schemas[request.SchemaName] = entry;
            try
            {
                await database.SaveCatalogAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch
            {
                if (hadPrevious)
                {
                    schemas[request.SchemaName] = previous!;
                }
                else
                {
                    schemas.TryRemove(request.SchemaName, out _);
                }

                throw;
            }
        }
        finally
        {
            database.CatalogLock.Release();
        }

        return new RegisterSchemaResponse { Fingerprint = fingerprint };
    }

    private async Task<DatabaseEntry> GetDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await registry.GetAsync(name, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (ArgumentException exception)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            throw ProtocolMapper.NotFound(exception.Message);
        }
    }

    private static Diagnostic ErrorDiagnostic(string code, string message)
    {
        return new Diagnostic { Code = code, Severity = DiagnosticSeverity.Error, Message = message };
    }

    private static Dictionary<string, RegisteredJsonSchema> ParseEventSchemas(DatabaseEntry database)
    {
        return database.Catalog.EventSchemas.Values.ToDictionary(
            schema => schema.Name,
            schema => RegisteredJsonSchema.Parse(schema.Name, schema.DocumentJson, eventSchema: true),
            StringComparer.Ordinal);
    }

    private static DecisionPlan AssertSinglePlan(CompilationResult compilation, string handlerName)
    {
        if (compilation.HasErrors || compilation.Plans.Count != 1)
        {
            throw new InvalidDataException($"Handler '{handlerName}' did not compile to exactly one decision plan.");
        }

        return compilation.Plans[index: 0];
    }

    private static void EnsureCatalogWritable(DatabaseEntry database)
    {
        if (database.Status is not (DatabaseStatus.Recovering or DatabaseStatus.Ready))
        {
            throw ProtocolMapper.Unavailable(
                $"Database '{database.Name}' does not accept catalog changes while in state '{database.Status}'.");
        }
    }

    private static ConcurrentDictionary<string, SchemaCatalogEntry> Schemas(
        DatabaseEntry database,
        SchemaKind kind)
    {
        return kind switch
        {
            SchemaKind.Event => database.Catalog.EventSchemas,
            SchemaKind.Command => database.Catalog.CommandSchemas,
            _ => throw ProtocolMapper.InvalidArgument("A schema kind is required.")
        };
    }

    private static IReadOnlyList<string> ValidatePlan(
        DecisionPlan plan,
        RegisterHandlerRequest request,
        IReadOnlyDictionary<string, RegisteredJsonSchema> eventSchemas,
        DatabaseEntry database)
    {
        List<string> errors = new();
        if (string.IsNullOrWhiteSpace(plan.Name) ||
            !string.Equals(plan.CommandSchema, request.CommandType, StringComparison.Ordinal))
        {
            errors.Add("The plan must have a name and its command schema must match command_type.");
        }

        if (plan.Fingerprints.LanguageVersion is not ("ndl-v1" or "sdk-v1") ||
            string.IsNullOrWhiteSpace(plan.Fingerprints.SourceFingerprint))
        {
            errors.Add("The plan language version or source fingerprint is invalid.");
        }

        if (plan.Includes.Count == 0 || plan.Includes.Any(include =>
                string.IsNullOrWhiteSpace(include.EventType) || include.KeyBindings.Count == 0))
        {
            errors.Add("Every plan must include at least one keyed event specification.");
        }

        if (plan.Emissions.Count == 0)
        {
            errors.Add("An accepted plan must emit at least one event.");
        }

        foreach (PlanInclude include in plan.Includes)
        {
            if (eventSchemas.TryGetValue(include.EventType, out RegisteredJsonSchema? schema))
            {
                foreach (PlanKeyBinding binding in include.KeyBindings)
                {
                    try
                    {
                        _ = schema.ResolveKeyName(binding.PropertyName);
                    }
                    catch (InvalidOperationException exception)
                    {
                        errors.Add(exception.Message);
                    }
                }
            }
        }

        foreach ((string name, string fingerprint) in plan.Fingerprints.SchemaFingerprints)
        {
            SchemaCatalogEntry? current = database.Catalog.EventSchemas.GetValueOrDefault(name) ??
                                          database.Catalog.CommandSchemas.GetValueOrDefault(name);
            if (current is not null && !string.Equals(current.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                errors.Add($"Plan schema fingerprint for '{name}' does not match the current catalog.");
            }
        }

        return errors;
    }
}