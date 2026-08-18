using System.Linq.Expressions;

using NativeDCB.Model;

namespace NativeDCB.Sdk;

public sealed record IncludedEventDefinition(
    Type EventType,
    LambdaExpression Reducer,
    LambdaExpression Predicate,
    QueryItem? QueryTemplate);