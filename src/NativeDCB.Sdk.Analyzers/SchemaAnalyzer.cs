using System.Collections.Concurrent;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NativeDCB.Sdk.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SchemaAnalyzer : DiagnosticAnalyzer
{
    public const string MissingKeyId = "NDCB001";
    public const string DuplicateEventId = "NDCB002";
    public const string DuplicateKeyId = "NDCB003";

    // ReSharper disable once MemberCanBePrivate.Global
    public const string UnsupportedKeyTypeId = "NDCB004";

    private static readonly DiagnosticDescriptor MissingKey = new(
        MissingKeyId,
        "Event schema requires a consistency key",
        "Event type '{0}' must declare at least one ConsistencyKey property",
        "NativeDCB.Schema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateEvent = new(
        DuplicateEventId,
        "Event schema name is duplicated",
        "Event schema name '{0}' is declared more than once",
        "NativeDCB.Schema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    private static readonly DiagnosticDescriptor DuplicateKey = new(
        DuplicateKeyId,
        "Consistency key name is duplicated",
        "Consistency key name '{0}' is duplicated on event type '{1}'",
        "NativeDCB.Schema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedKeyType = new(
        UnsupportedKeyTypeId,
        "Consistency key type is unsupported",
        "Consistency key property '{0}' has unsupported type '{1}'",
        "NativeDCB.Schema",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [MissingKey, DuplicateEvent, DuplicateKey, UnsupportedKeyType];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            ConcurrentDictionary<string, ConcurrentBag<Location>> eventNames = new(StringComparer.Ordinal);
            startContext.RegisterSymbolAction(
                symbolContext => AnalyzeType((INamedTypeSymbol)symbolContext.Symbol, symbolContext, eventNames),
                SymbolKind.NamedType);
            startContext.RegisterCompilationEndAction(endContext =>
            {
                foreach (KeyValuePair<string, ConcurrentBag<Location>> pair in
                         eventNames.Where(value => value.Value.Count > 1))
                {
                    foreach (Location location in pair.Value)
                    {
                        endContext.ReportDiagnostic(Diagnostic.Create(DuplicateEvent, location, pair.Key));
                    }
                }
            });
        });
    }

    private static void AnalyzeType(
        INamedTypeSymbol type,
        SymbolAnalysisContext context,
        ConcurrentDictionary<string, ConcurrentBag<Location>> eventNames)
    {
        AttributeData? eventAttribute = Attribute(type, "NativeDCB.Sdk.EventTypeAttribute");
        if (eventAttribute is null)
        {
            return;
        }

        string eventName = eventAttribute.ConstructorArguments[index: 0].Value as string ?? string.Empty;
        eventNames.GetOrAdd(eventName, _ => []).Add(type.Locations.FirstOrDefault() ?? Location.None);
        (IPropertySymbol Property, string Name)[] keys = type.GetMembers()
            .OfType<IPropertySymbol>()
            .Select(property => (Property: property,
                Attribute: Attribute(property, "NativeDCB.Sdk.ConsistencyKeyAttribute")))
            .Where(value => value.Attribute is not null)
            .Select(value => (value.Property,
                value.Attribute!.ConstructorArguments[index: 0].Value as string ?? string.Empty))
            .ToArray();
        if (keys.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                MissingKey, type.Locations.FirstOrDefault(), eventName));
            return;
        }

        foreach (IGrouping<string, (IPropertySymbol Property, string Name)> duplicate in
                 keys.GroupBy(value => value.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            foreach ((IPropertySymbol property, _) in duplicate)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateKey, property.Locations.FirstOrDefault(), duplicate.Key, eventName));
            }
        }

        foreach ((IPropertySymbol property, _) in keys.Where(value => !Supported(value.Property.Type)))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                UnsupportedKeyType,
                property.Locations.FirstOrDefault(),
                property.Name,
                property.Type.ToDisplayString()));
        }
    }

    private static AttributeData? Attribute(ISymbol symbol, string metadataName)
    {
        return symbol.GetAttributes().FirstOrDefault(value =>
            string.Equals(value.AttributeClass?.ToDisplayString(), metadataName, StringComparison.Ordinal));
    }

    private static bool Supported(ITypeSymbol type)
    {
        type = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[index: 0]
            : type;
        return type.SpecialType is
                   SpecialType.System_String or
                   SpecialType.System_Boolean or
                   SpecialType.System_Byte or
                   SpecialType.System_SByte or
                   SpecialType.System_Int16 or
                   SpecialType.System_UInt16 or
                   SpecialType.System_Int32 or
                   SpecialType.System_UInt32 or
                   SpecialType.System_Int64 or
                   SpecialType.System_UInt64 or
                   SpecialType.System_Decimal ||
               type.TypeKind == TypeKind.Enum ||
               type.ToDisplayString() is "System.Guid" or "System.DateTime" or "System.DateTimeOffset";
    }
}