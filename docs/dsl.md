# NativeDCB Decision Language

Status: implemented NDL v1 subset and known limitations  
Last verified: 2026-08-14

## Overview

NDL is a small decision-definition language with a pipeline-shaped syntax. It is independently implemented and is not SQL or KQL compatible. A document contains one or more decisions; a registered handler must contain exactly one, while `ExecuteStatement` can persist every decision in a multi-decision document as a handler.

```ndl
decision SubscribeStudent
from SubscribeStudentToCourse command
| include CourseDefined event
    where event.CourseId == command.CourseId
    apply {
        CourseExists = true,
        Capacity = event.Capacity
    }
| include StudentSubscribedToCourse event
    where event.CourseId == command.CourseId
    apply {
        Count = (previous.Count ?? 0) + 1
    }
| evaluate {
    require (model.CourseExists ?? false) else "Course does not exist";
    require (model.Count ?? 0) < model.Capacity else "Course is full";
    let remaining = model.Capacity - (model.Count ?? 0) - 1;
}
| decide {
    emit StudentSubscribedToCourse {
        StudentId = command.StudentId,
        CourseId = command.CourseId,
        RemainingSeats = max(remaining, 0)
    };
};
```

The final semicolon after `decide` is required. At least one `include` and one `emit` are required. `evaluate` is syntactically required but may be empty.

## Library API

`NativeDCB.Ndl.Ndl` exposes:

```csharp
LexResult lexed = Ndl.Lex(source);
ParseResult parsed = Ndl.Parse(source);
CompilationResult compiled = Ndl.Compile(source);
string canonical = Ndl.Format(source);
string canonicalTree = Ndl.Format(parsed.Document);
```

Lex/parse results retain `SourceText`, zero-based `TextSpan`s, tokens/syntax, diagnostics, and `HasErrors`. Formatting invalid source throws `NdlFormatException`. Compilation first parses; parse errors yield no plans. Valid syntax is lowered to one storage-neutral `DecisionPlan` per decision with language version `ndl-v1` and SHA-256 of canonical formatted source.

Important limitation: the library compiler performs lowering, not complete semantic/type analysis. It does not accept schema inputs. Some syntactically valid but unsupported forms can throw `InvalidOperationException` during lowering instead of producing a diagnostic. Server registration adds a limited semantic pass.

## Syntax

The implemented skeleton is:

```ebnf
document = { decision } ;
decision = "decision", name,
           "from", type-name, command-alias,
           include, { include }, evaluate, decide, ";" ;
include = "|", "include", type-name, event-alias,
          "where", expression,
          "apply", object ;
evaluate = "|", "evaluate", "{", { require | let }, "}" ;
require = "require", expression, "else", expression, ";" ;
let = "let", name, "=", expression, ";" ;
decide = "|", "decide", "{", emit, { emit }, "}" ;
emit = "emit", type-name, object, ";" ;
object = "{", assignment, { ",", assignment }, [","], "}" ;
assignment = name, "=", expression ;
```

Identifiers use letters/underscore followed by letters, digits, or underscore. Type names may be dot-qualified. Whitespace, `//` comments, and `/* ... */` comments are accepted.

### Expressions

Implemented expressions include:

- null, Boolean, signed-via-unary integer, decimal, string, canonical GUID, ISO-like `DateTimeOffset`, and duration literals
- duration suffixes `ms`, `s`, `m`, `h`, and `d`
- member access and function calls
- unary `not`, `+`, and `-`
- `*`, `/`, `%`, `+`, `-`
- `<`, `<=`, `>`, `>=`, `==`, `!=`
- short-circuit `and`, `or`
- null/uninitialized coalescing `??`
- conditional `condition ? whenTrue : whenFalse`
- object expressions

Strings support `\n`, `\r`, `\t`, `\"`, and `\\`. The lexer reports unknown escapes but keeps the escaped character.

The runtime standard library is only `exists(value)`, `min(a,b)`, and `max(a,b)`. Numeric evaluation normalizes supported JSON numbers to `long` or `decimal` and arithmetic to `decimal`. String `+` concatenates. Non-numeric ordered comparison falls back to ordinal comparison of scalar text.

## Query And Model Semantics

Every include must be a conjunction of equality bindings where one side is a direct property of the event alias (or `event`). Server validation rejects other `where` shapes. The opposite side should be command-derived; the runtime evaluates it before reading history. A registered event schema maps the property name to the logical key from `x-native-dcb-consistency-key`; without a schema, the property name itself is the key name.

Each include becomes one DCB query item. Includes are ORed. Multiple bindings within an include must all match the same event. Events are replayed in ascending global event ID through one observed Main Writer head.

The model is a mutable runtime dictionary representing structural patches. For each persisted event, matching includes of that event type run in source order. `previous` is a copy of the model before that include's assignments. Assignment expressions are evaluated against that copy and then written to the current model. Fields not assigned remain unchanged.

An absent member produces an internal Missing sentinel. Reading it in Boolean, numeric, equality, comparison, scalar, or emitted-value context fails the command unless protected with `exists(...)` or `??`. Missing history is not automatically rejected: a create decision can require `not exists(model.Field)` and proceed from an empty model.

## Evaluation And Emission

Evaluation statements run in source order:

- `require` rejects on false with code `DomainRejected` and the scalar reason text.
- `let` adds a decision-local value available to later evaluation steps and emissions.

Emission order is batch order. A registered event schema checks required/types/additional properties and derives all event keys. Without a schema, every non-null scalar top-level payload property becomes a key; if none exist, execution fails. Accepted plans must emit at least one event.

## Compile, Persist, Execute

### NDL handler registration

`CatalogService.RegisterHandler` parses source, requires exactly one decision, checks command-type equality, keyed includes, duplicate object assignments, supported calls in `apply`/`emit`, and available event schemas. It then compiles the plan, adds current schema fingerprints, serializes it as JSON, computes source/plan SHA-256 fingerprints, and saves source plus plan in `catalog_v1.json`.

The request's `handler_name` is the catalog key. The compiled decision's name is not currently required to equal that handler name. At execution the stored compiled plan is authoritative; source is retained for inspection and reparsing helpers.

`CatalogService.GetHandler` can optionally return that authoritative plan JSON and ask the NDL library for canonical generated NDL. Generated text is kept separate from original source. Conversion fails with diagnostics rather than changing behavior when a stored plan uses constructs that NDL cannot represent, including invalid NDL names or key bindings that differ from those derivable from its `where` expression.

### NDL statement execution

`StatementService.ExplainStatement` and `ExecuteStatement` validate every decision individually and reject duplicate decision names. Execute compiles valid decisions, canonicalizes each to standalone source, and persists all of them under their decision names with one catalog save; if any decision is invalid, none are published. It is a handler-registration statement path only. It does not read events, execute a command, register schemas, or perform administration.

### SDK plans

The .NET SDK serializes its `sdk-v1` `DecisionPlan` directly in `plan_json`. The server validates and stores that plan without converting it to NDL; the stored NDL source is empty. NDL and SDK plans use the same runtime evaluator.

### Execution

The Transaction grain validates the stored plan fingerprint, captures a committed read snapshot, interprets plan expressions, and conditionally appends. Append conflicts cause an unbounded retry within the gRPC request lifetime. See [Internal Engine](internal-engine.md).

## Diagnostics

The lexer emits `NDL0001`-`NDL0006`. The parser emits `NDL1001`/`1002`/`1003`, `NDL1101`, `NDL1201`, `NDL1301`/`1302`, `NDL1401`, and `NDL1501`, all with spans. It performs error recovery at top level and statement boundaries.

The server converts spans to one-based line/column plus zero-based absolute offset and uses `NDL2001` for its semantic errors. It does not currently implement full name resolution, type inference, definite assignment, overlap warnings, determinism analysis across every expression, or stable per-rule semantic diagnostic codes.

## Known Limitations

- `where` command-dependency purity is not comprehensively validated; an unresolved symbol fails at execution.
- Unsupported calls are checked only in include `apply` and emitted object assignments during registration. Unknown calls in other expressions may register and fail at execution.
- There is no user-defined function, collection/index access, loop, recursion, read statement, schema statement, command statement, or administration grammar.
- Arrays cannot be written as NDL literals, though command/event JSON may contain arrays.
- Schema-aware compile analysis exists only in server validation and is intentionally shallow.
- `allow_incompatible` on handler/statement registration has no implemented handler compatibility behavior or audit trail.
- The Web workbench provides a plain NDL source editor for validation, explanation, and execution, but there is no syntax-aware editor service, LSP integration, or generated C# output.

The fuller language features previously described in design documents remain future behavior, not current guarantees.
