using System.Linq.Expressions;

using NativeDCB.Model.Queries;

namespace NativeDCB.Sdk.Decisions.Authoring;

public sealed record IncludedEventDefinition(
    Type EventType,
    LambdaExpression Reducer,
    LambdaExpression Predicate,
    QueryItem? QueryTemplate);