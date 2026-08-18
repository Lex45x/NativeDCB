using System.Text.Json;

using Grpc.Core;

using NativeDCB.Model.Decisions;
using NativeDCB.Ndl.Compilation;
using NativeDCB.Ndl.Parsing;
using NativeDCB.Ndl.Syntax;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Catalog;
using NativeDCB.Server.Catalog.Schemas;
using NativeDCB.Server.Databases;
using NativeDCB.Server.Decisions.Execution;
using NativeDCB.Server.Grpc.Infrastructure;

using DiagnosticSeverity = NativeDCB.Protocol.V1.DiagnosticSeverity;
using QueryItem = NativeDCB.Protocol.V1.QueryItem;

namespace NativeDCB.Server.Grpc;

public sealed class StatementGrpcService(DatabaseRegistry registry) : StatementService.StatementServiceBase
{
    public override async Task<ExplainStatementResponse> ExplainStatement(
        ExplainStatementRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await EnsureDatabaseAsync(
            request.Database, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        Dictionary<string, RegisteredJsonSchema> eventSchemas = ParseEventSchemas(database);
        ParseResult parsed = Ndl.Ndl.Parse(request.NdlSource);
        ExplainStatementResponse response = new() { Valid = !parsed.HasErrors };
        response.Diagnostics.AddRange(parsed.Diagnostics.Select(value =>
            ProtocolMapper.ToDiagnostic(value, parsed.Source)));
        if (!parsed.HasErrors)
        {
            foreach (string error in NdlDecisionRuntime.ValidateDecision(parsed, eventSchemas: eventSchemas))
            {
                response.Diagnostics.Add(ErrorDiagnostic(error));
            }

            response.Valid = response.Diagnostics.All(value =>
                value.Severity != DiagnosticSeverity.Error);
            if (response.Valid)
            {
                response.Plan = ProtocolMapper.ToPlan(parsed);
                CompilationResult compilation = Ndl.Ndl.Compile(request.NdlSource);
                foreach (DecisionPlan plan in compilation.Plans)
                {
                    response.QueryTemplates.Add(ToQueryTemplate(plan, eventSchemas));
                }
            }
        }

        return response;
    }

    public override async Task ExecuteStatement(
        ExecuteStatementRequest request,
        IServerStreamWriter<StatementResult> responseStream,
        ServerCallContext context)
    {
        ExplainStatementResponse explained = await ExplainStatement(
                new ExplainStatementRequest { Database = request.Database, NdlSource = request.NdlSource }, context)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (explained.Diagnostics.Count > 0)
        {
            DiagnosticBatch batch = new();
            batch.Diagnostics.AddRange(explained.Diagnostics);
            await responseStream
                .WriteAsync(new StatementResult { StatementIndex = 0, Diagnostics = batch }, context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }

        if (!explained.Valid)
        {
            await WriteCompletionAsync(
                    responseStream, statementIndex: 0, succeeded: false, "Statement validation failed.",
                    context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        DatabaseEntry database = await EnsureDatabaseAsync(
            request.Database, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (!database.WriteAvailable)
        {
            throw ProtocolMapper.Unavailable(
                $"Database '{database.Name}' is not accepting statements while in state '{database.Status}'.");
        }

        ParseResult parsed = Ndl.Ndl.Parse(request.NdlSource);
        CompilationResult compilation = Ndl.Ndl.Compile(request.NdlSource);
        Dictionary<string, string> schemaFingerprints = database.Catalog.EventSchemas.Values
            .Concat(database.Catalog.CommandSchemas.Values)
            .ToDictionary(schema => schema.Name, schema => schema.Fingerprint, StringComparer.Ordinal);
        List<HandlerCatalogEntry> registrations = new(compilation.Plans.Count);
        foreach (DecisionPlan sourcePlan in compilation.Plans)
        {
            DecisionPlan plan = sourcePlan with
            {
                Fingerprints = sourcePlan.Fingerprints with { SchemaFingerprints = schemaFingerprints }
            };
            string planJson = JsonSerializer.Serialize(plan, DatabaseRegistry.JsonOptions);
            DecisionSyntax decision = parsed.Document.Decisions.Single(value => value.Name == plan.Name);
            string singleSource = Ndl.Ndl.Format(new DocumentSyntax([decision], decision.Span));
            registrations.Add(new HandlerCatalogEntry(
                plan.Name,
                plan.CommandSchema,
                singleSource,
                DatabaseRegistry.Fingerprint(singleSource),
                DatabaseRegistry.Fingerprint(planJson),
                planJson));
        }

        await PublishRegistrationsAsync(database, registrations, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        for (int index = 0; index < registrations.Count; index++)
        {
            HandlerCatalogEntry registration = registrations[index];
            await responseStream
                .WriteAsync(
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
                    }, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }

        await WriteCompletionAsync(
            responseStream,
            checked((uint)registrations.Count),
            succeeded: true,
            $"Registered {registrations.Count} handler(s).",
            context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static async Task PublishRegistrationsAsync(
        DatabaseEntry database,
        IReadOnlyList<HandlerCatalogEntry> registrations,
        CancellationToken cancellationToken)
    {
        await database.CatalogLock.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        Dictionary<string, HandlerCatalogEntry?> previous = new(StringComparer.Ordinal);
        try
        {
            foreach (HandlerCatalogEntry registration in registrations)
            {
                previous[registration.Name] = database.Catalog.Handlers.TryGetValue(
                    registration.Name, out HandlerCatalogEntry? existing)
                    ? existing
                    : null;
                database.Catalog.Handlers[registration.Name] = registration;
            }

            try
            {
                await database.SaveCatalogAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch
            {
                foreach ((string name, HandlerCatalogEntry? existing) in previous)
                {
                    if (existing is null)
                    {
                        database.Catalog.Handlers.TryRemove(name, out _);
                    }
                    else
                    {
                        database.Catalog.Handlers[name] = existing;
                    }
                }

                throw;
            }
        }
        finally
        {
            database.CatalogLock.Release();
        }
    }

    private static Query ToQueryTemplate(
        DecisionPlan decision,
        IReadOnlyDictionary<string, RegisteredJsonSchema> eventSchemas)
    {
        Query query = new();
        foreach (PlanInclude include in decision.Includes)
        {
            QueryItem item = new();
            item.EventTypes.Add(include.EventType);
            eventSchemas.TryGetValue(include.EventType, out RegisteredJsonSchema? schema);
            item.Keys.AddRange(include.KeyBindings.Select(binding => new KeyValue
            {
                Key = schema?.ResolveKeyName(binding.PropertyName) ?? binding.PropertyName, Value = "$command"
            }));
            query.Items.Add(item);
        }

        return query;
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
            }, cancellationToken);
    }

    private async Task<DatabaseEntry> EnsureDatabaseAsync(string name, CancellationToken cancellationToken)
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

    private static Dictionary<string, RegisteredJsonSchema> ParseEventSchemas(DatabaseEntry database)
    {
        return database.Catalog.EventSchemas.Values.ToDictionary(
            schema => schema.Name,
            schema => RegisteredJsonSchema.Parse(schema.Name, schema.DocumentJson, eventSchema: true),
            StringComparer.Ordinal);
    }

    private static Diagnostic ErrorDiagnostic(string message)
    {
        return new Diagnostic { Code = "NDL2001", Severity = DiagnosticSeverity.Error, Message = message };
    }
}