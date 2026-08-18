using System.Text.Json;

using Google.Protobuf;

using NativeDCB.Cli.Arguments;
using NativeDCB.Protocol.V1;

namespace NativeDCB.Cli.IO;

internal sealed class InputReader(CancellationToken cancellationToken)
{
    private bool _stdinClaimed;

    public async Task<ByteString> RequiredJsonAsync(CliArguments arguments, string name)
    {
        string json = await RequiredTextAsync(arguments, name);
        ValidateJson(json, $"--{name}");
        return ByteString.CopyFromUtf8(json);
    }

    public async Task<string> RequiredTextAsync(CliArguments arguments, string name)
    {
        string? inline = arguments.Optional(name);
        string? filename = arguments.Optional($"{name}-file");
        bool stdin = arguments.Flag($"{name}-stdin");
        int sources = (inline is null ? 0 : 1) + (filename is null ? 0 : 1) + (stdin ? 1 : 0);
        if (sources != 1)
        {
            throw new CliUsageException(
                $"Specify exactly one of --{name}, --{name}-file, or --{name}-stdin.");
        }

        if (inline is not null)
        {
            return inline;
        }

        if (filename is not null)
        {
            try
            {
                return await File.ReadAllTextAsync(filename, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CliInputException($"Could not read '{filename}': {exception.Message}", exception);
            }
        }

        return await ReadStdinAsync();
    }

    public async Task<(string? Source, ByteString Plan)> HandlerInputsAsync(CliArguments arguments)
    {
        bool hasSource = HasInput(arguments, "source");
        bool hasPlan = HasInput(arguments, "plan");
        if (!hasSource && !hasPlan)
        {
            throw new CliUsageException(
                "Specify handler source with --source/--source-file/--source-stdin, plan JSON with " +
                "--plan/--plan-file/--plan-stdin, or both.");
        }

        string? source = hasSource ? await RequiredTextAsync(arguments, "source") : null;
        if (!hasPlan)
        {
            return (source, ByteString.Empty);
        }

        ByteString plan = await RequiredJsonAsync(arguments, "plan");
        return (source, plan);
    }

    public async Task<Query?> QueryAsync(
        CliArguments arguments,
        bool required,
        string keyOption = "key")
    {
        Query? query = null;
        if (HasInput(arguments, "query"))
        {
            string json = await RequiredTextAsync(arguments, "query");
            query = ParseProtoJson(json, Query.Parser, "query");
        }

        foreach (string itemJson in arguments.Many("query-item"))
        {
            query ??= new Query();
            query.Items.Add(ParseProtoJson(itemJson, QueryItem.Parser, "query item"));
        }

        IReadOnlyList<string> eventTypes = arguments.Many("event-type");
        IReadOnlyList<string> keys = arguments.Many(keyOption);
        if (eventTypes.Count > 0 || keys.Count > 0)
        {
            query ??= new Query();
            QueryItem item = new();
            item.EventTypes.AddRange(eventTypes);
            item.Keys.AddRange(ParseKeys(keys, keyOption));
            query.Items.Add(item);
        }

        if (required && (query is null || query.Items.Count == 0))
        {
            throw new CliUsageException(
                "A query is required. Use --query, --query-file, --query-stdin, --query-item, " +
                "--event-type, or key filters.");
        }

        return query;
    }

    public async Task<IReadOnlyList<TransientSchema>> TransientSchemasAsync(CliArguments arguments)
    {
        List<TransientSchema> schemas = [];
        foreach (string specification in arguments.Many("transient"))
        {
            (SchemaKind kind, string name, string json) = ParseTransientWithValue(specification, "transient");
            ValidateJson(json, $"transient schema '{name}'");
            schemas.Add(CreateTransient(kind, name, json));
        }

        foreach (string specification in arguments.Many("transient-file"))
        {
            (SchemaKind kind, string name, string filename) = ParseTransientWithValue(
                specification,
                "transient-file");
            string json;
            try
            {
                json = await File.ReadAllTextAsync(filename, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new CliInputException($"Could not read '{filename}': {exception.Message}", exception);
            }

            ValidateJson(json, $"transient schema '{name}'");
            schemas.Add(CreateTransient(kind, name, json));
        }

        foreach (string identity in arguments.Many("transient-stdin"))
        {
            (SchemaKind kind, string name) = ParseTransientIdentity(identity);
            string json = await ReadStdinAsync();
            ValidateJson(json, $"transient schema '{name}'");
            schemas.Add(CreateTransient(kind, name, json));
        }

        return schemas;
    }

    public static IReadOnlyList<KeyValue> ParseKeys(IEnumerable<string> values, string option = "key")
    {
        List<KeyValue> keys = [];
        foreach (string value in values)
        {
            int equals = value.IndexOf(value: '=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new CliUsageException($"Option --{option} must use key=value syntax.");
            }

            keys.Add(new KeyValue { Key = value[..equals], Value = value[(equals + 1)..] });
        }

        return keys;
    }

    private static bool HasInput(CliArguments arguments, string name)
    {
        return arguments.Has(name) || arguments.Has($"{name}-file") || arguments.Has($"{name}-stdin");
    }

    private async Task<string> ReadStdinAsync()
    {
        if (_stdinClaimed)
        {
            throw new CliUsageException("Standard input can be used by only one input option.");
        }

        _stdinClaimed = true;
        try
        {
            return await Console.In.ReadToEndAsync(cancellationToken);
        }
        catch (IOException exception)
        {
            throw new CliInputException($"Could not read standard input: {exception.Message}", exception);
        }
    }

    private static T ParseProtoJson<T>(string json, MessageParser<T> parser, string description)
        where T : IMessage<T>
    {
        try
        {
            return parser.ParseJson(json);
        }
        catch (InvalidJsonException exception)
        {
            throw new CliInputException($"Invalid {description} JSON: {exception.Message}", exception);
        }
    }

    private static void ValidateJson(string json, string description)
    {
        try
        {
            using JsonDocument _ = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new CliInputException($"Invalid JSON for {description}: {exception.Message}", exception);
        }
    }

    private static (SchemaKind Kind, string Name, string Value) ParseTransientWithValue(
        string specification,
        string option)
    {
        int equals = specification.IndexOf(value: '=', StringComparison.Ordinal);
        if (equals <= 0 || equals == specification.Length - 1)
        {
            throw new CliUsageException(
                $"Option --{option} must use event:name=value or command:name=value syntax.");
        }

        (SchemaKind kind, string name) = ParseTransientIdentity(specification[..equals]);
        return (kind, name, specification[(equals + 1)..]);
    }

    private static (SchemaKind Kind, string Name) ParseTransientIdentity(string identity)
    {
        int colon = identity.IndexOf(value: ':', StringComparison.Ordinal);
        if (colon <= 0 || colon == identity.Length - 1)
        {
            throw new CliUsageException("Transient schema identity must use event:name or command:name syntax.");
        }

        SchemaKind kind = identity[..colon] switch
        {
            "event" => SchemaKind.Event,
            "command" => SchemaKind.Command,
            _ => throw new CliUsageException("Transient schema kind must be event or command.")
        };
        return (kind, identity[(colon + 1)..]);
    }

    private static TransientSchema CreateTransient(SchemaKind kind, string name, string json)
    {
        return new TransientSchema
        {
            SchemaKind = kind, SchemaName = name, SchemaDocumentJson = ByteString.CopyFromUtf8(json)
        };
    }
}