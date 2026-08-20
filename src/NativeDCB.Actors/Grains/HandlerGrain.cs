using System.Text.Json;

using NativeDCB.Actors.Catalog;
using NativeDCB.Actors.Catalog.Schemas;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Model.Decisions;
using NativeDCB.Ndl.Compilation;
using NativeDCB.Ndl.Formatting;
using NativeDCB.Ndl.Parsing;
using NativeDCB.Ndl.Syntax;
using NativeDCB.Ndl.Text;

using NdlDiagnosticSeverity = NativeDCB.Ndl.Diagnostics.DiagnosticSeverity;

namespace NativeDCB.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class HandlerGrain(
    ActorStoragePath storage,
    IGrainFactory grains) : Grain, IHandlerGrain
{
    private const string FileName = "handlers_v1.json";
    private HandlerCatalogDocument _document = new();
    private string _path = string.Empty;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _path = storage.GetFile(this.GetPrimaryKeyString(), FileName);
        if (File.Exists(_path))
        {
            await using FileStream stream = new(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _document = await JsonSerializer.DeserializeAsync<HandlerCatalogDocument>(
                            stream, CatalogJson.Options, cancellationToken)
                        .ConfigureAwait(continueOnCapturedContext: true)
                    ?? throw new InvalidDataException($"Handler catalog '{_path}' is empty.");
            if (_document.FormatVersion != 1 || _document.Handlers is null)
            {
                throw new InvalidDataException($"Handler catalog '{_path}' is invalid.");
            }
        }
        else
        {
            await CatalogJson.ReplaceAsync(_path, _document, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        await base.OnActivateAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    public async Task<HandlerRegistrationResultMessage> RegisterAsync(
        RegisterHandlerMessage request,
        GrainCancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.CommandType))
        {
            return InvalidRegistration(request, "Handler name and command type are required.");
        }

        SchemaSnapshotMessage snapshot = await SchemaGrain.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        Dictionary<string, RegisteredJsonSchema> eventSchemas = ParseEventSchemas(snapshot);
        List<ActorDiagnosticMessage> diagnostics = new();
        string sourceFingerprint = string.Empty;
        string planJson = string.Empty;

        if (!string.IsNullOrEmpty(request.PlanJson))
        {
            try
            {
                DecisionPlan plan = JsonSerializer.Deserialize<DecisionPlan>(request.PlanJson, CatalogJson.Options)
                                    ?? throw new JsonException("The decision plan is empty.");
                foreach (string error in ValidatePlan(plan, request.CommandType, eventSchemas, snapshot))
                {
                    diagnostics.Add(Error("PLAN2001", error));
                }

                planJson = JsonSerializer.Serialize(plan, CatalogJson.Options);
                sourceFingerprint = plan.Fingerprints.SourceFingerprint;
            }
            catch (JsonException exception)
            {
                diagnostics.Add(Error("PLAN1001", exception.Message));
            }
        }
        else
        {
            ParseResult parsed = NativeDCB.Ndl.Ndl.Parse(request.NdlSource);
            sourceFingerprint = CatalogJson.Fingerprint(request.NdlSource);
            diagnostics.AddRange(parsed.Diagnostics.Select(value => ToDiagnostic(value, parsed.Source)));
            IReadOnlyList<string> semanticErrors = parsed.HasErrors
                ? []
                : NdlDecisionValidator.Validate(parsed, request.CommandType, eventSchemas);
            diagnostics.AddRange(semanticErrors.Select(error => Error("NDL2001", error)));

            if (!parsed.HasErrors && semanticErrors.Count == 0)
            {
                CompilationResult compilation = NativeDCB.Ndl.Ndl.Compile(request.NdlSource);
                DecisionPlan sourcePlan = AssertSinglePlan(compilation, request.Name);
                DecisionPlan plan = sourcePlan with
                {
                    Fingerprints = sourcePlan.Fingerprints with
                    {
                        SchemaFingerprints = snapshot.EventSchemas.Concat(snapshot.CommandSchemas)
                            .ToDictionary(schema => schema.Name, schema => schema.Fingerprint, StringComparer.Ordinal)
                    }
                };
                planJson = JsonSerializer.Serialize(plan, CatalogJson.Options);
            }
        }

        bool valid = diagnostics.All(value => value.Severity != ActorDiagnosticSeverity.Error);
        if (!valid)
        {
            return new HandlerRegistrationResultMessage(new HandlerDescriptionMessage(
                request.Name,
                request.CommandType,
                request.NdlSource,
                sourceFingerprint,
                string.Empty,
                Valid: false,
                diagnostics.ToArray(),
                PlanJson: null,
                GeneratedNdl: null,
                NdlGenerationDiagnostics: [],
                Version: 0));
        }

        string planFingerprint = CatalogJson.Fingerprint(planJson);
        uint revision = checked(_document.Revision + 1);
        HandlerCatalogEntry entry = new(
            request.Name,
            request.CommandType,
            request.NdlSource,
            sourceFingerprint,
            planFingerprint,
            planJson,
            revision);
        HandlerCatalogDocument replacement = _document.Copy(revision);
        replacement.Handlers[entry.Name] = entry;
        await CatalogJson.ReplaceAsync(_path, replacement, cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        _document = replacement;

        return new HandlerRegistrationResultMessage(ToDescription(
            entry, includePlanJson: false, generatedNdl: null, generationDiagnostics: []));
    }

    public async Task<HandlerRemoveResultMessage?> RemoveAsync(
        string name,
        GrainCancellationToken cancellationToken)
    {
        if (!_document.Handlers.TryGetValue(name, out HandlerCatalogEntry? removed))
        {
            return null;
        }

        uint revision = checked(_document.Revision + 1);
        HandlerCatalogDocument replacement = _document.Copy(revision);
        replacement.Handlers.Remove(name);
        await CatalogJson.ReplaceAsync(_path, replacement, cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        _document = replacement;
        return new HandlerRemoveResultMessage(
            removed.SourceFingerprint, removed.PlanFingerprint, removed.Version);
    }

    public Task<HandlerDescriptionMessage?> GetAsync(
        GetHandlerMessage request,
        GrainCancellationToken cancellationToken)
    {
        cancellationToken.CancellationToken.ThrowIfCancellationRequested();
        if (!_document.Handlers.TryGetValue(request.Name, out HandlerCatalogEntry? handler))
        {
            return Task.FromResult<HandlerDescriptionMessage?>(null);
        }

        string? generatedNdl = null;
        ActorDiagnosticMessage[] generationDiagnostics = [];
        if (request.GenerateNdl)
        {
            NdlPlanFormatResult generated = NativeDCB.Ndl.Ndl.TryFormat(handler.ParsePlan());
            if (generated.Success)
            {
                generatedNdl = generated.NdlSource;
            }
            else
            {
                generationDiagnostics = generated.Diagnostics.Select(value => Error(
                    "NDL3001", $"{value.Path}: {value.Message}")).ToArray();
            }
        }

        return Task.FromResult<HandlerDescriptionMessage?>(ToDescription(
            handler, request.IncludePlanJson, generatedNdl, generationDiagnostics));
    }

    public Task<HandlerListMessage> ListAsync(GrainCancellationToken cancellationToken)
    {
        cancellationToken.CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HandlerListMessage(
            _document.Handlers.Values
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .Select(ToSummary).ToArray(),
            _document.Revision));
    }

    public async Task<NdlValidationResultMessage> ValidateNdlAsync(
        ValidateNdlMessage request,
        GrainCancellationToken cancellationToken)
    {
        SchemaSnapshotMessage snapshot = await SchemaGrain.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        Dictionary<string, RegisteredJsonSchema> eventSchemas = ParseEventSchemas(snapshot);
        foreach (TransientSchemaMessage transient in request.TransientSchemas)
        {
            if (transient.Kind == ActorSchemaKind.Event)
            {
                try
                {
                    eventSchemas[transient.Name] = RegisteredJsonSchema.Parse(
                        transient.Name, transient.DocumentJson, eventSchema: true);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    return new NdlValidationResultMessage(
                        Valid: false,
                        Diagnostics: [],
                        Plan: null,
                        new CatalogErrorMessage(
                            CatalogErrorKind.InvalidArgument,
                            $"Invalid schema JSON: {exception.Message}"));
                }
            }
            else if (transient.Kind != ActorSchemaKind.Command)
            {
                return new NdlValidationResultMessage(
                    Valid: false,
                    Diagnostics: [],
                    Plan: null,
                    new CatalogErrorMessage(
                        CatalogErrorKind.InvalidArgument, "A transient schema kind is required."));
            }
        }

        ParseResult parsed = NativeDCB.Ndl.Ndl.Parse(request.NdlSource);
        List<ActorDiagnosticMessage> diagnostics = parsed.Diagnostics
            .Select(value => ToDiagnostic(value, parsed.Source)).ToList();
        if (!parsed.HasErrors)
        {
            diagnostics.AddRange(NdlDecisionValidator.Validate(parsed, eventSchemas: eventSchemas)
                .Select(error => Error("NDL2001", error)));
        }

        bool valid = diagnostics.All(value => value.Severity != ActorDiagnosticSeverity.Error);
        PlanSummaryMessage? plan = valid ? ToPlan(parsed) : null;
        EventQueryMessage[] queryTemplates = valid
            ? NativeDCB.Ndl.Ndl.Compile(request.NdlSource).Plans
                .Select(value => ToQueryTemplate(value, eventSchemas)).ToArray()
            : [];
        return new NdlValidationResultMessage(
            valid, diagnostics.ToArray(), plan, QueryTemplates: queryTemplates);
    }

    public async Task<PublishStatementResultMessage> PublishStatementAsync(
        PublishStatementMessage request,
        GrainCancellationToken cancellationToken)
    {
        SchemaSnapshotMessage snapshot = await SchemaGrain.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        Dictionary<string, RegisteredJsonSchema> eventSchemas = ParseEventSchemas(snapshot);
        ParseResult parsed = NativeDCB.Ndl.Ndl.Parse(request.NdlSource);
        List<ActorDiagnosticMessage> diagnostics = parsed.Diagnostics
            .Select(value => ToDiagnostic(value, parsed.Source)).ToList();
        if (!parsed.HasErrors)
        {
            diagnostics.AddRange(NdlDecisionValidator.Validate(parsed, eventSchemas: eventSchemas)
                .Select(error => Error("NDL2001", error)));
        }

        if (diagnostics.Any(value => value.Severity == ActorDiagnosticSeverity.Error))
        {
            return new PublishStatementResultMessage(false, diagnostics.ToArray(), []);
        }

        Dictionary<string, string> schemaFingerprints = snapshot.EventSchemas.Concat(snapshot.CommandSchemas)
            .ToDictionary(schema => schema.Name, schema => schema.Fingerprint, StringComparer.Ordinal);
        CompilationResult compilation = NativeDCB.Ndl.Ndl.Compile(request.NdlSource);
        uint revision = _document.Revision;
        List<HandlerCatalogEntry> registrations = new(compilation.Plans.Count);
        foreach (DecisionPlan sourcePlan in compilation.Plans)
        {
            DecisionPlan plan = sourcePlan with
            {
                Fingerprints = sourcePlan.Fingerprints with { SchemaFingerprints = schemaFingerprints }
            };
            string planJson = JsonSerializer.Serialize(plan, CatalogJson.Options);
            DecisionSyntax decision = parsed.Document.Decisions.Single(value => value.Name == plan.Name);
            string source = NativeDCB.Ndl.Ndl.Format(new DocumentSyntax([decision], decision.Span));
            revision = checked(revision + 1);
            registrations.Add(new HandlerCatalogEntry(
                plan.Name,
                plan.CommandSchema,
                source,
                CatalogJson.Fingerprint(source),
                CatalogJson.Fingerprint(planJson),
                planJson,
                revision));
        }

        HandlerCatalogDocument replacement = _document.Copy(revision);
        foreach (HandlerCatalogEntry registration in registrations)
        {
            replacement.Handlers[registration.Name] = registration;
        }

        await CatalogJson.ReplaceAsync(_path, replacement, cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        _document = replacement;
        return new PublishStatementResultMessage(
            true, diagnostics.ToArray(), registrations.Select(ToSummary).ToArray());
    }

    private ISchemaGrain SchemaGrain => grains.GetGrain<ISchemaGrain>(this.GetPrimaryKeyString());

    private static Dictionary<string, RegisteredJsonSchema> ParseEventSchemas(SchemaSnapshotMessage snapshot)
    {
        try
        {
            return snapshot.EventSchemas.ToDictionary(
                schema => schema.Name,
                schema => RegisteredJsonSchema.Parse(schema.Name, schema.DocumentJson, eventSchema: true),
                StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException($"A stored event schema is invalid: {exception.Message}", exception);
        }
    }

    private static IReadOnlyList<string> ValidatePlan(
        DecisionPlan plan,
        string commandType,
        IReadOnlyDictionary<string, RegisteredJsonSchema> eventSchemas,
        SchemaSnapshotMessage snapshot)
    {
        List<string> errors = new();
        if (string.IsNullOrWhiteSpace(plan.Name) ||
            !string.Equals(plan.CommandSchema, commandType, StringComparison.Ordinal))
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

        Dictionary<string, SchemaRegistrationMessage> current = snapshot.EventSchemas
            .Concat(snapshot.CommandSchemas)
            .GroupBy(value => value.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach ((string name, string fingerprint) in plan.Fingerprints.SchemaFingerprints)
        {
            if (current.TryGetValue(name, out SchemaRegistrationMessage? schema) &&
                !string.Equals(schema.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                errors.Add($"Plan schema fingerprint for '{name}' does not match the current catalog.");
            }
        }

        return errors;
    }

    private static DecisionPlan AssertSinglePlan(CompilationResult compilation, string handlerName)
    {
        if (compilation.HasErrors || compilation.Plans.Count != 1)
        {
            throw new InvalidDataException(
                $"Handler '{handlerName}' did not compile to exactly one decision plan.");
        }

        return compilation.Plans[0];
    }

    private static HandlerDescriptionMessage ToDescription(
        HandlerCatalogEntry entry,
        bool includePlanJson,
        string? generatedNdl,
        ActorDiagnosticMessage[] generationDiagnostics)
    {
        return new HandlerDescriptionMessage(
            entry.Name,
            entry.CommandType,
            entry.NdlSource,
            entry.SourceFingerprint,
            entry.PlanFingerprint,
            Valid: true,
            Diagnostics: [],
            includePlanJson ? entry.PlanJson : null,
            generatedNdl,
            generationDiagnostics,
            entry.Version);
    }

    private static HandlerSummaryMessage ToSummary(HandlerCatalogEntry entry)
    {
        return new HandlerSummaryMessage(
            entry.Name,
            entry.CommandType,
            entry.SourceFingerprint,
            entry.PlanFingerprint,
            Valid: true,
            entry.Version);
    }

    private static HandlerRegistrationResultMessage InvalidRegistration(
        RegisterHandlerMessage request,
        string message)
    {
        return new HandlerRegistrationResultMessage(
            new HandlerDescriptionMessage(
                request.Name,
                request.CommandType,
                request.NdlSource,
                string.Empty,
                string.Empty,
                Valid: false,
                Diagnostics: [],
                PlanJson: null,
                GeneratedNdl: null,
                NdlGenerationDiagnostics: [],
                Version: 0),
            new CatalogErrorMessage(CatalogErrorKind.InvalidArgument, message));
    }

    private static ActorDiagnosticMessage ToDiagnostic(
        NativeDCB.Ndl.Diagnostics.NdlDiagnostic value,
        SourceText source)
    {
        LinePosition start = source.GetLinePosition(value.Span.Start);
        LinePosition end = source.GetLinePosition(value.Span.End);
        return new ActorDiagnosticMessage(
            value.Code,
            value.Severity == NdlDiagnosticSeverity.Error
                ? ActorDiagnosticSeverity.Error
                : ActorDiagnosticSeverity.Warning,
            value.Message,
            new ActorSourceSpanMessage(
                new ActorSourcePositionMessage(
                    checked((uint)start.Line), checked((uint)start.Character), checked((uint)value.Span.Start)),
                new ActorSourcePositionMessage(
                    checked((uint)end.Line), checked((uint)end.Character), checked((uint)value.Span.End))));
    }

    private static PlanSummaryMessage ToPlan(ParseResult parsed)
    {
        return new PlanSummaryMessage(
            CatalogJson.Fingerprint(NativeDCB.Ndl.Ndl.Format(parsed.Document)),
            $"{parsed.Document.Decisions.Count} decision(s)",
            parsed.Document.Decisions.SelectMany(value => new[]
            {
                $"decision:{value.Name}", $"include:{value.Includes.Count}", $"emit:{value.Decide.Emissions.Count}"
            }).ToArray());
    }

    private static EventQueryMessage ToQueryTemplate(
        DecisionPlan decision,
        IReadOnlyDictionary<string, RegisteredJsonSchema> eventSchemas)
    {
        return new EventQueryMessage(decision.Includes.Select(include =>
        {
            eventSchemas.TryGetValue(include.EventType, out RegisteredJsonSchema? schema);
            return new QueryItemMessage(
                [include.EventType],
                include.KeyBindings.Select(binding => new EventKeyMessage(
                    schema?.ResolveKeyName(binding.PropertyName) ?? binding.PropertyName,
                    "$command")).ToArray());
        }).ToArray());
    }

    private static ActorDiagnosticMessage Error(string code, string message)
    {
        return new ActorDiagnosticMessage(code, ActorDiagnosticSeverity.Error, message);
    }

    private sealed class HandlerCatalogDocument
    {
        public int FormatVersion { get; init; } = 1;
        public uint Revision { get; init; }
        public Dictionary<string, HandlerCatalogEntry> Handlers { get; init; } = new(StringComparer.Ordinal);

        public HandlerCatalogDocument Copy(uint revision)
        {
            return new HandlerCatalogDocument
            {
                Revision = revision,
                Handlers = new Dictionary<string, HandlerCatalogEntry>(Handlers, StringComparer.Ordinal)
            };
        }
    }
}