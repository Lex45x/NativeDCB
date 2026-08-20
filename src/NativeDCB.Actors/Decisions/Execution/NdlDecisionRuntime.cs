using System.Globalization;
using System.Text.Json;

using NativeDCB.Actors.Catalog;
using NativeDCB.Actors.Catalog.Schemas;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.Model.Decisions;
using NativeDCB.Model.Decisions.Evaluation;
using NativeDCB.Model.Decisions.Expressions;
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
    private static readonly object Missing = new();

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

            Dictionary<string, object?> evaluateScope = BaseScope(decision, command, model);
            foreach (PlanEvaluationStep statement in decision.Evaluation)
            {
                switch (statement)
                {
                    case PlanRequirement require when !ToBoolean(Evaluate(require.Condition, evaluateScope)):
                        return DecisionExecution.Reject("DomainRejected",
                            ScalarText(Evaluate(require.Reason, evaluateScope)));
                    case PlanLocal let:
                        evaluateScope[let.Name] = Evaluate(let.Value, evaluateScope);
                        break;
                }
            }

            List<CandidateEvent> candidates = new();
            foreach (PlanEmission emission in decision.Emissions)
            {
                Dictionary<string, object?> payload = EvaluateObject(emission.Assignments, evaluateScope);
                if (payload.Values.Any(x => ReferenceEquals(x, Missing)))
                {
                    throw new InvalidOperationException(
                        $"Emitted event '{emission.EventType}' contains an uninitialized property.");
                }

                JsonElement data = JsonSerializer.SerializeToElement(payload, CatalogJson.Options);
                EventKey[] keys = eventSchemas.TryGetValue(
                    emission.EventType, out RegisteredJsonSchema? emittedSchema)
                    ? emittedSchema.ExtractKeys(data)
                    : payload
                        .Where(x => IsScalar(x.Value) && x.Value is not null && !ReferenceEquals(x.Value, Missing))
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

        Dictionary<string, object?> model = Replay(decision, command, events);
        byte[] modelJson = [];
        if (serializeModel)
        {
            EnsureInitialized(model);
            modelJson = JsonSerializer.SerializeToUtf8Bytes(model, CatalogJson.Options);
        }

        return new PreparedDecisionResult(decision, query, hydrated.ObservedHead, modelJson, model);
    }

    private static Dictionary<string, object?> Replay(
        DecisionPlan decision,
        JsonElement command,
        IReadOnlyList<SequencedEvent> events)
    {
        Dictionary<string, object?> model = new(StringComparer.Ordinal);
        foreach (SequencedEvent persisted in events)
        {
            object? eventValue = ConvertJson(persisted.Data);
            foreach (PlanInclude include in decision.Includes.Where(x => x.EventType == persisted.Type))
            {
                Dictionary<string, object?> scope = BaseScope(decision, command, model);
                scope[include.Alias] = eventValue;
                scope["event"] = eventValue;
                scope["position"] = persisted.EventId;
                if (!ToBoolean(Evaluate(include.Where, scope)))
                {
                    continue;
                }

                Dictionary<string, object?> previous = new(model, StringComparer.Ordinal);
                scope["previous"] = previous;
                foreach (PlanAssignment assignment in include.Assignments)
                {
                    model[assignment.Name] = Evaluate(assignment.Value, scope);
                }
            }
        }

        return model;
    }

    private static void EnsureInitialized(object? value)
    {
        if (ReferenceEquals(value, Missing))
        {
            throw new InvalidOperationException("The prepared decision model contains an uninitialized value.");
        }

        IEnumerable<object?> children = value switch
        {
            Dictionary<string, object?> dictionary => dictionary.Values,
            object?[] array => array,
            _ => []
        };
        foreach (object? child in children)
        {
            EnsureInitialized(child);
        }
    }

    private static EventQuery BuildQuery(
        DecisionPlan decision,
        JsonElement command,
        IReadOnlyDictionary<string, RegisteredJsonSchema>? eventSchemas = null)
    {
        List<QueryItem> items = new();
        foreach (PlanInclude include in decision.Includes)
        {
            Dictionary<string, object?> scope = BaseScope(decision, command, new Dictionary<string, object?>());
            RegisteredJsonSchema? schema = null;
            eventSchemas?.TryGetValue(include.EventType, out schema);
            EventKey[] keys = include.KeyBindings.Select(binding => new EventKey(
                schema?.ResolveKeyName(binding.PropertyName) ?? binding.PropertyName,
                ScalarText(Evaluate(binding.Value, scope)))).ToArray();
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

    private static Dictionary<string, object?> BaseScope(
        DecisionPlan decision,
        JsonElement command,
        Dictionary<string, object?> model)
    {
        object? commandValue = ConvertJson(command);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [decision.CommandAlias] = commandValue,
            ["command"] = commandValue,
            ["model"] = model,
            ["previous"] = model
        };
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

    private static object? Evaluate(PlanExpression expression, Dictionary<string, object?> scope)
    {
        return expression switch
        {
            PlanLiteralExpression literal => EvaluateLiteral(literal),
            PlanSymbolExpression symbol => scope.TryGetValue(symbol.Name, out object? value) ? value : Missing,
            PlanMemberExpression member => ReadMember(Evaluate(member.Target, scope), member.Member),
            PlanObjectExpression objectValue => EvaluateObject(objectValue.Assignments, scope),
            PlanUnaryExpression unary => EvaluateUnary(unary, scope),
            PlanBinaryExpression binary => EvaluateBinary(binary, scope),
            PlanConditionalExpression conditional => ToBoolean(Evaluate(conditional.Condition, scope))
                ? Evaluate(conditional.WhenTrue, scope)
                : Evaluate(conditional.WhenFalse, scope),
            PlanCallExpression call => EvaluateCall(call, scope),
            _ => throw new InvalidOperationException($"Unsupported plan expression '{expression.GetType().Name}'.")
        };
    }

    private static Dictionary<string, object?> EvaluateObject(
        IReadOnlyList<PlanAssignment> assignments,
        Dictionary<string, object?> scope)
    {
        return assignments.ToDictionary(
            assignment => assignment.Name,
            assignment => Evaluate(assignment.Value, scope),
            StringComparer.Ordinal);
    }

    private static object? EvaluateLiteral(PlanLiteralExpression literal)
    {
        return literal.Kind switch
        {
            PlanLiteralKind.Null => null,
            PlanLiteralKind.Boolean => bool.Parse(literal.Value!),
            PlanLiteralKind.Integer => long.Parse(literal.Value!, CultureInfo.InvariantCulture),
            PlanLiteralKind.Decimal => decimal.Parse(literal.Value!, CultureInfo.InvariantCulture),
            PlanLiteralKind.String => literal.Value,
            PlanLiteralKind.Guid => Guid.Parse(literal.Value!),
            PlanLiteralKind.DateTime => DateTimeOffset.Parse(literal.Value!, CultureInfo.InvariantCulture),
            PlanLiteralKind.Duration => TimeSpan.Parse(literal.Value!, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"Unsupported literal kind '{literal.Kind}'.")
        };
    }

    private static object EvaluateUnary(PlanUnaryExpression unary, Dictionary<string, object?> scope)
    {
        object? value = Evaluate(unary.Operand, scope);
        return unary.Operator switch
        {
            PlanUnaryOperator.Not => !ToBoolean(value),
            PlanUnaryOperator.Plus => Number(value),
            PlanUnaryOperator.Minus => -Number(value),
            _ => throw new InvalidOperationException("Unsupported unary operator.")
        };
    }

    private static object? EvaluateBinary(PlanBinaryExpression binary, Dictionary<string, object?> scope)
    {
        object? left = Evaluate(binary.Left, scope);
        if (binary.Operator == PlanBinaryOperator.Coalesce)
        {
            return left is null || ReferenceEquals(left, Missing) ? Evaluate(binary.Right, scope) : left;
        }

        if (binary.Operator == PlanBinaryOperator.And && !ToBoolean(left))
        {
            return false;
        }

        if (binary.Operator == PlanBinaryOperator.Or && ToBoolean(left))
        {
            return true;
        }

        object? right = Evaluate(binary.Right, scope);
        return binary.Operator switch
        {
            PlanBinaryOperator.Add when left is string || right is string => ScalarText(left) + ScalarText(right),
            PlanBinaryOperator.Add => Number(left) + Number(right),
            PlanBinaryOperator.Subtract => Number(left) - Number(right),
            PlanBinaryOperator.Multiply => Number(left) * Number(right),
            PlanBinaryOperator.Divide => Number(left) / Number(right),
            PlanBinaryOperator.Remainder => Number(left) % Number(right),
            PlanBinaryOperator.Equal => Equal(left, right),
            PlanBinaryOperator.NotEqual => !Equal(left, right),
            PlanBinaryOperator.Less => Compare(left, right) < 0,
            PlanBinaryOperator.LessOrEqual => Compare(left, right) <= 0,
            PlanBinaryOperator.Greater => Compare(left, right) > 0,
            PlanBinaryOperator.GreaterOrEqual => Compare(left, right) >= 0,
            PlanBinaryOperator.And => ToBoolean(right),
            PlanBinaryOperator.Or => ToBoolean(right),
            _ => throw new InvalidOperationException("Unsupported binary operator.")
        };
    }

    private static object? EvaluateCall(PlanCallExpression call, Dictionary<string, object?> scope)
    {
        object?[] arguments = call.Arguments.Select(value => Evaluate(value, scope)).ToArray();
        return call.Function switch
        {
            "exists" when arguments.Length == 1 => !ReferenceEquals(arguments[0], Missing),
            "min" when arguments.Length == 2 => Number(arguments[0]) <= Number(arguments[1])
                ? arguments[0]
                : arguments[1],
            "max" when arguments.Length == 2 => Number(arguments[0]) >= Number(arguments[1])
                ? arguments[0]
                : arguments[1],
            _ => throw new InvalidOperationException($"Unknown function '{call.Function}'.")
        };
    }

    private static object? Evaluate(ExpressionSyntax expression, Dictionary<string, object?> scope)
    {
        return expression switch
        {
            LiteralExpressionSyntax literal => literal.Value,
            IdentifierExpressionSyntax identifier => scope.TryGetValue(identifier.Name, out object? value)
                ? value
                : Missing,
            MemberAccessExpressionSyntax member => ReadMember(Evaluate(member.Target, scope), member.Member),
            ParenthesizedExpressionSyntax parenthesized => Evaluate(parenthesized.Expression, scope),
            ObjectExpressionSyntax value => EvaluateObject(value, scope),
            UnaryExpressionSyntax unary => EvaluateUnary(unary, scope),
            BinaryExpressionSyntax binary => EvaluateBinary(binary, scope),
            ConditionalExpressionSyntax conditional => ToBoolean(Evaluate(conditional.Condition, scope))
                ? Evaluate(conditional.WhenTrue, scope)
                : Evaluate(conditional.WhenFalse, scope),
            CallExpressionSyntax call => EvaluateCall(call, scope),
            _ => throw new InvalidOperationException($"Unsupported expression '{expression.GetType().Name}'.")
        };
    }

    private static Dictionary<string, object?> EvaluateObject(
        ObjectExpressionSyntax expression,
        Dictionary<string, object?> scope)
    {
        return expression.Assignments.ToDictionary(
            x => x.Name, x => Evaluate(x.Value, scope), StringComparer.Ordinal);
    }

    private static object? EvaluateUnary(UnaryExpressionSyntax unary, Dictionary<string, object?> scope)
    {
        object? value = Evaluate(unary.Operand, scope);
        return unary.OperatorKind switch
        {
            SyntaxKind.NotKeyword => !ToBoolean(value),
            SyntaxKind.PlusToken => Number(value),
            SyntaxKind.MinusToken => -Number(value),
            _ => throw new InvalidOperationException("Unsupported unary operator.")
        };
    }

    private static object? EvaluateBinary(BinaryExpressionSyntax binary, Dictionary<string, object?> scope)
    {
        object? left = Evaluate(binary.Left, scope);
        if (binary.OperatorKind == SyntaxKind.QuestionQuestionToken)
        {
            return left is null || ReferenceEquals(left, Missing) ? Evaluate(binary.Right, scope) : left;
        }

        if (binary.OperatorKind == SyntaxKind.AndKeyword && !ToBoolean(left))
        {
            return false;
        }

        if (binary.OperatorKind == SyntaxKind.OrKeyword && ToBoolean(left))
        {
            return true;
        }

        object? right = Evaluate(binary.Right, scope);
        return binary.OperatorKind switch
        {
            SyntaxKind.PlusToken when left is string || right is string => ScalarText(left) + ScalarText(right),
            SyntaxKind.PlusToken => Number(left) + Number(right),
            SyntaxKind.MinusToken => Number(left) - Number(right),
            SyntaxKind.StarToken => Number(left) * Number(right),
            SyntaxKind.SlashToken => Number(left) / Number(right),
            SyntaxKind.PercentToken => Number(left) % Number(right),
            SyntaxKind.EqualsEqualsToken => Equal(left, right),
            SyntaxKind.BangEqualsToken => !Equal(left, right),
            SyntaxKind.LessToken => Compare(left, right) < 0,
            SyntaxKind.LessOrEqualsToken => Compare(left, right) <= 0,
            SyntaxKind.GreaterToken => Compare(left, right) > 0,
            SyntaxKind.GreaterOrEqualsToken => Compare(left, right) >= 0,
            SyntaxKind.AndKeyword => ToBoolean(right),
            SyntaxKind.OrKeyword => ToBoolean(right),
            _ => throw new InvalidOperationException("Unsupported binary operator.")
        };
    }

    private static object? EvaluateCall(CallExpressionSyntax call, Dictionary<string, object?> scope)
    {
        if (call.Target is not IdentifierExpressionSyntax function)
        {
            throw new InvalidOperationException("Only standard library calls are supported.");
        }

        object?[] arguments = call.Arguments.Select(x => Evaluate(x, scope)).ToArray();
        return function.Name switch
        {
            "exists" when arguments.Length == 1 => !ReferenceEquals(arguments[0], Missing),
            "min" when arguments.Length == 2 => Number(arguments[0]) <= Number(arguments[1])
                ? arguments[0]
                : arguments[1],
            "max" when arguments.Length == 2 => Number(arguments[0]) >= Number(arguments[1])
                ? arguments[0]
                : arguments[1],
            _ => throw new InvalidOperationException($"Unknown function '{function.Name}'.")
        };
    }

    private static object? ReadMember(object? target, string member)
    {
        if (target is Dictionary<string, object?> dictionary && dictionary.TryGetValue(member, out object? value))
        {
            return value;
        }

        return Missing;
    }

    private static object? ConvertJson(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                x => x.Name, x => ConvertJson(x.Value), StringComparer.Ordinal),
            JsonValueKind.Array => value.EnumerateArray().Select(ConvertJson).ToArray(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out long integer) => integer,
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => Missing
        };
    }

    private static bool ToBoolean(object? value)
    {
        if (ReferenceEquals(value, Missing))
        {
            throw new InvalidOperationException(
                "An uninitialized value was read without an exists check or null-coalescing default.");
        }

        return value is bool boolean
            ? boolean
            : throw new InvalidOperationException("Expression must evaluate to a boolean.");
    }

    private static decimal Number(object? value)
    {
        if (ReferenceEquals(value, Missing))
        {
            throw new InvalidOperationException(
                "An uninitialized value was read without an exists check or null-coalescing default.");
        }

        return value switch
        {
            byte number => number,
            short number => number,
            int number => number,
            long number => number,
            float number => (decimal)number,
            double number => (decimal)number,
            decimal number => number,
            _ => throw new InvalidOperationException("Expression must evaluate to a number.")
        };
    }

    private static bool Equal(object? left, object? right)
    {
        if (ReferenceEquals(left, Missing) || ReferenceEquals(right, Missing))
        {
            throw new InvalidOperationException(
                "An uninitialized value was read without an exists check or null-coalescing default.");
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return Number(left) == Number(right);
        }

        return Equals(left, right);
    }

    private static int Compare(object? left, object? right)
    {
        if (ReferenceEquals(left, Missing) || ReferenceEquals(right, Missing))
        {
            throw new InvalidOperationException(
                "An uninitialized value was read without an exists check or null-coalescing default.");
        }

        if (IsNumber(left) && IsNumber(right))
        {
            return Number(left).CompareTo(Number(right));
        }

        return string.Compare(ScalarText(left), ScalarText(right), StringComparison.Ordinal);
    }

    private static bool IsNumber(object? value)
    {
        return value is byte or short or int or long or float or double or decimal;
    }

    private static bool IsScalar(object? value)
    {
        return value is null or string or bool or byte or short or int or long or float or double or decimal or Guid
            or DateTimeOffset or TimeSpan;
    }

    private static string ScalarText(object? value)
    {
        if (ReferenceEquals(value, Missing))
        {
            throw new InvalidOperationException("An uninitialized value was read.");
        }

        return value switch
        {
            null => "null",
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}