using System.Globalization;
using System.Text.Json;

using NativeDCB.Actors.Catalog;
using NativeDCB.Actors.Catalog.Schemas;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.Model.Decisions;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;
using NativeDCB.Model.Queries;
using NativeDCB.Ndl.Lexing;
using NativeDCB.Ndl.Parsing;
using NativeDCB.Ndl.Syntax;
using NativeDCB.Ndl.Syntax.Expressions;
using NativeDCB.Ndl.Syntax.Statements;

namespace NativeDCB.Actors.Decisions.Execution;

internal sealed class NdlDecisionRuntime
{
    public async Task<DecisionExecution> ExecuteAsync(
        IGrainFactory grains,
        string database,
        HandlerCatalogEntry handler,
        IReadOnlyDictionary<string, RegisteredJsonSchema> eventSchemas,
        IReadOnlyDictionary<string, uint> eventSchemaVersions,
        Guid commandId,
        JsonElement command,
        GrainCancellationToken cancellationToken)
    {
        IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(database);
        while (true)
        {
            cancellationToken.CancellationToken.ThrowIfCancellationRequested();
            PreparedDecisionResult prepared = await PrepareAsync(
                grains, database, handler, eventSchemas, command, cancellationToken, serializeModel: false);
            DecisionPlan decision = prepared.Plan;
            Dictionary<string, object?> model = prepared.StructuralModel;

            DecisionModelEvaluationResult evaluation = DecisionModelKernel.Evaluate(decision, command, model);
            if (evaluation.Rejected)
            {
                return DecisionExecution.Reject("DomainRejected", evaluation.RejectionReason!);
            }

            List<CandidateEvent> candidates = new();
            foreach (EvaluatedDecisionEmission emission in evaluation.Emissions)
            {
                Dictionary<string, object?> payload = emission.Payload;
                JsonElement data = JsonSerializer.SerializeToElement(payload, CatalogJson.Options);
                EventKey[] keys = eventSchemas.TryGetValue(
                    emission.EventType, out RegisteredJsonSchema? emittedSchema)
                    ? emittedSchema.ExtractKeys(data)
                    : payload
                        .Where(x => IsScalar(x.Value) && x.Value is not null)
                        .Select(x => new EventKey(x.Key, ScalarText(x.Value)))
                        .ToArray();
                if (keys.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Emitted event '{emission.EventType}' must expose at least one scalar consistency key.");
                }

                candidates.Add(new CandidateEvent(
                    emission.EventType,
                    data,
                    keys,
                    eventSchemaVersions.GetValueOrDefault(emission.EventType, 1u)));
            }

            AppendResultMessage append = await writer.AppendAsync(
                ActorMessageMapper.ToMessage(new EventBatch(commandId, handler.CommandType, candidates)),
                ActorMessageMapper.ToMessage(new AppendCondition(prepared.Query, prepared.ObservedHead)),
                cancellationToken);
            if (append.Outcome == AppendResultOutcome.Conflict)
            {
                continue;
            }

            SequencedEvent[] committed = append.Events.Select(ActorMessageMapper.ToModel).ToArray();
            return append.Outcome == AppendResultOutcome.AlreadyCommitted
                ? DecisionExecution.AlreadyCommitted(committed)
                : DecisionExecution.Committed(committed);
        }
    }

    public async Task<PreparedDecisionResult> PrepareAsync(
        IGrainFactory grains,
        string database,
        HandlerCatalogEntry handler,
        IReadOnlyDictionary<string, RegisteredJsonSchema> eventSchemas,
        JsonElement command,
        GrainCancellationToken cancellationToken,
        bool serializeModel = true)
    {
        if (!string.Equals(
                handler.PlanFingerprint,
                CatalogJson.Fingerprint(handler.PlanJson),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Stored handler plan '{handler.Name}' failed fingerprint validation.");
        }

        DecisionPlan decision = handler.ParsePlan();
        EventQuery query = BuildQuery(decision, command, eventSchemas);
        EventQueryMessage queryMessage = ActorMessageMapper.ToMessage(query);
        IndexQueryResultMessage hydrated = await grains.GetGrain<IIndexOrchestratorGrain>(database)
            .ReadAuthoritativeAsync(
                queryMessage,
                afterEventIdExclusive: 0,
                throughEventIdInclusive: null,
                maxCount: int.MaxValue,
                cancellationToken);
        IReadOnlyList<SequencedEvent> events = hydrated.Events
            .Select(ActorMessageMapper.ToModel)
            .ToArray();

        Dictionary<string, object?> model = DecisionModelKernel.Replay(
            decision, command, events, ensureInitialized: serializeModel);
        byte[] modelJson = [];
        if (serializeModel)
        {
            modelJson = JsonSerializer.SerializeToUtf8Bytes(model, CatalogJson.Options);
        }

        return new PreparedDecisionResult(decision, query, hydrated.ObservedHead, modelJson, model);
    }

    private static EventQuery BuildQuery(
        DecisionPlan decision,
        JsonElement command,
        IReadOnlyDictionary<string, RegisteredJsonSchema>? eventSchemas = null)
    {
        List<QueryItem> items = new();
        foreach (PlanInclude include in decision.Includes)
        {
            RegisteredJsonSchema? schema = null;
            eventSchemas?.TryGetValue(include.EventType, out schema);
            EventKey[] keys = include.KeyBindings.Select(binding => new EventKey(
                schema?.ResolveKeyName(binding.PropertyName) ?? binding.PropertyName,
                DecisionModelKernel.EvaluateScalar(decision, command, binding.Value))).ToArray();
            items.Add(new QueryItem([include.EventType], keys));
        }

        return new EventQuery(items);
    }

    public static IReadOnlyList<string> ValidateDecision(
        ParseResult parsed,
        string? commandType = null,
        IReadOnlyDictionary<string, RegisteredJsonSchema>? eventSchemas = null)
    {
        List<string> errors = new();
        if (commandType is not null && parsed.Document.Decisions.Count != 1)
        {
            errors.Add("Handler NDL must contain exactly one decision.");
            return errors;
        }

        foreach (IGrouping<string, DecisionSyntax> duplicate in parsed.Document.Decisions
                     .GroupBy(value => value.Name, StringComparer.Ordinal)
                     .Where(value => value.Count() > 1))
        {
            errors.Add($"Decision name '{duplicate.Key}' is declared more than once.");
        }

        foreach (DecisionSyntax decision in parsed.Document.Decisions)
        {
            ValidateDecision(decision, commandType, eventSchemas, errors);
        }

        return errors;
    }

    private static void ValidateDecision(
        DecisionSyntax decision,
        string? commandType,
        IReadOnlyDictionary<string, RegisteredJsonSchema>? eventSchemas,
        List<string> errors)
    {
        if (commandType is not null && !string.Equals(decision.From.CommandType, commandType, StringComparison.Ordinal))
        {
            errors.Add(
                $"Handler command type '{commandType}' does not match NDL command type '{decision.From.CommandType}'.");
        }

        foreach (IncludeStageSyntax include in decision.Includes)
        {
            List<(string Name, ExpressionSyntax Value)> bindings = new();
            try
            {
                CollectBindings(include.Where, include.Alias, bindings);
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(exception.Message);
            }

            if (bindings.Count == 0)
            {
                errors.Add($"Include '{include.EventType}' must bind at least one event key.");
            }

            if (eventSchemas is not null &&
                eventSchemas.TryGetValue(include.EventType, out RegisteredJsonSchema? includeSchema))
            {
                foreach ((string property, _) in bindings)
                {
                    try
                    {
                        _ = includeSchema.ResolveKeyName(property);
                    }
                    catch (InvalidOperationException exception)
                    {
                        errors.Add(exception.Message);
                    }
                }
            }

            ValidateObject(include.Apply, $"include '{include.EventType}' apply", errors);
        }

        foreach (EmitStatementSyntax emission in decision.Decide.Emissions)
        {
            ValidateObject(emission.Value, $"emit '{emission.EventType}'", errors);
            if (eventSchemas is not null &&
                eventSchemas.TryGetValue(emission.EventType, out RegisteredJsonSchema? emittedSchema))
            {
                HashSet<string> assigned = emission.Value.Assignments
                    .Select(assignment => assignment.Name)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (JsonPropertySchema property in emittedSchema.Properties.Values)
                {
                    if ((property.Required || property.KeyName is not null) && !assigned.Contains(property.Name))
                    {
                        errors.Add(
                            $"Emitted event '{emission.EventType}' must assign property '{property.Name}'.");
                    }
                }

                if (!emittedSchema.AdditionalProperties)
                {
                    foreach (string property in assigned.Where(value => !emittedSchema.Properties.ContainsKey(value)))
                    {
                        errors.Add(
                            $"Emitted event '{emission.EventType}' assigns undeclared property '{property}'.");
                    }
                }
            }
        }
    }

    private static void ValidateObject(ObjectExpressionSyntax value, string role, List<string> errors)
    {
        string? duplicate = value.Assignments.GroupBy(x => x.Name, StringComparer.Ordinal)
            .FirstOrDefault(x => x.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            errors.Add($"The {role} assigns '{duplicate}' more than once.");
        }

        foreach (CallExpressionSyntax call in value.Assignments.SelectMany(x => Calls(x.Value)))
        {
            if (call.Target is not IdentifierExpressionSyntax function ||
                function.Name is not ("exists" or "min" or "max"))
            {
                errors.Add($"The {role} contains an unsupported function call.");
            }
        }
    }

    private static IEnumerable<CallExpressionSyntax> Calls(ExpressionSyntax value)
    {
        if (value is CallExpressionSyntax call)
        {
            yield return call;
        }

        IEnumerable<ExpressionSyntax> children = value switch
        {
            MemberAccessExpressionSyntax member => [member.Target],
            CallExpressionSyntax invocation => [invocation.Target, .. invocation.Arguments],
            ParenthesizedExpressionSyntax parenthesized => [parenthesized.Expression],
            UnaryExpressionSyntax unary => [unary.Operand],
            BinaryExpressionSyntax binary => [binary.Left, binary.Right],
            ConditionalExpressionSyntax conditional =>
                [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            ObjectExpressionSyntax objectValue => objectValue.Assignments.Select(x => x.Value),
            _ => []
        };
        foreach (ExpressionSyntax child in children)
        {
            foreach (CallExpressionSyntax nested in Calls(child))
            {
                yield return nested;
            }
        }
    }

    private static void CollectBindings(
        ExpressionSyntax expression,
        string eventAlias,
        List<(string Name, ExpressionSyntax Value)> bindings)
    {
        if (expression is BinaryExpressionSyntax { OperatorKind: SyntaxKind.AndKeyword } conjunction)
        {
            CollectBindings(conjunction.Left, eventAlias, bindings);
            CollectBindings(conjunction.Right, eventAlias, bindings);
            return;
        }

        if (expression is not BinaryExpressionSyntax { OperatorKind: SyntaxKind.EqualsEqualsToken } equality)
        {
            throw new InvalidOperationException(
                "Where expressions support only event-key equality bindings joined by 'and'.");
        }

        if (TryEventMember(equality.Left, eventAlias, out string left))
        {
            bindings.Add((left, equality.Right));
        }
        else if (TryEventMember(equality.Right, eventAlias, out string right))
        {
            bindings.Add((right, equality.Left));
        }
        else
        {
            throw new InvalidOperationException("A where equality must compare a direct event property.");
        }
    }

    private static bool TryEventMember(ExpressionSyntax expression, string alias, out string member)
    {
        if (expression is MemberAccessExpressionSyntax
            {
                Target: IdentifierExpressionSyntax identifier
            } access && (identifier.Name == alias || identifier.Name == "event"))
        {
            member = access.Member;
            return true;
        }

        member = string.Empty;
        return false;
    }

    private static bool IsScalar(object? value)
    {
        return value is null or string or bool or byte or short or int or long or float or double or decimal or Guid
            or DateTimeOffset or TimeSpan;
    }

    private static string ScalarText(object? value)
    {
        return value switch
        {
            null => "null",
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}