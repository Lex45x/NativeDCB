using System.Text.Json;

using NativeDCB.Actors.Catalog;
using NativeDCB.Actors.Catalog.Schemas;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;

namespace NativeDCB.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class SchemaGrain(ActorStoragePath storage) : Grain, ISchemaGrain
{
    private const string FileName = "schemas_v1.json";
    private SchemaCatalogDocument _document = new();
    private string _path = string.Empty;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        _path = storage.GetFile(this.GetPrimaryKeyString(), FileName);
        if (File.Exists(_path))
        {
            await using FileStream stream = new(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _document = await JsonSerializer.DeserializeAsync<SchemaCatalogDocument>(
                            stream, CatalogJson.Options, cancellationToken)
                        .ConfigureAwait(continueOnCapturedContext: true)
                    ?? throw new InvalidDataException($"Schema catalog '{_path}' is empty.");
            if (_document.FormatVersion != 1 ||
                _document.EventSchemas is null ||
                _document.CommandSchemas is null)
            {
                throw new InvalidDataException($"Schema catalog '{_path}' is invalid.");
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

    public async Task<SchemaRegistrationResultMessage> RegisterAsync(
        RegisterSchemaMessage request,
        GrainCancellationToken cancellationToken)
    {
        if (request.Kind is not (ActorSchemaKind.Event or ActorSchemaKind.Command))
        {
            return InvalidRegistration("A schema kind is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return InvalidRegistration("A schema name is required.");
        }

        RegisteredJsonSchema parsed;
        try
        {
            parsed = RegisteredJsonSchema.Parse(
                request.Name, request.DocumentJson, request.Kind == ActorSchemaKind.Event);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return InvalidRegistration($"Invalid schema JSON: {exception.Message}");
        }

        string fingerprint = CatalogJson.Fingerprint(request.DocumentJson);
        Dictionary<string, SchemaCatalogEntry> current = Schemas(request.Kind);
        if (current.TryGetValue(request.Name, out SchemaCatalogEntry? previous))
        {
            if (string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return new SchemaRegistrationResultMessage(
                    fingerprint, previous.Version, Registered: true, Diagnostics: []);
            }

            RegisteredJsonSchema previousSchema;
            try
            {
                previousSchema = RegisteredJsonSchema.Parse(
                    previous.Name, previous.DocumentJson, request.Kind == ActorSchemaKind.Event);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                throw new InvalidDataException(
                    $"Stored schema '{request.Name}' is invalid: {exception.Message}", exception);
            }

            IReadOnlyList<string> incompatibilities = parsed.CompatibilityErrors(previousSchema);
            if (incompatibilities.Count > 0 && !request.AllowIncompatible)
            {
                return new SchemaRegistrationResultMessage(
                    fingerprint,
                    previous.Version,
                    Registered: false,
                    incompatibilities.Select(message => Error("SCHEMA2001", message)).ToArray());
            }
        }

        uint revision = checked(_document.Revision + 1);
        uint version = checked((previous?.Version ?? 0) + 1);
        SchemaCatalogEntry entry = new(request.Name, request.DocumentJson, fingerprint, version);
        SchemaCatalogDocument replacement = _document.Copy(revision);
        replacement.Schemas(request.Kind)[request.Name] = entry;
        await CatalogJson.ReplaceAsync(_path, replacement, cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        _document = replacement;
        return new SchemaRegistrationResultMessage(
            fingerprint, version, Registered: true, Diagnostics: []);
    }

    public async Task<SchemaRemoveResultMessage?> RemoveAsync(
        SchemaLookupMessage request,
        GrainCancellationToken cancellationToken)
    {
        Dictionary<string, SchemaCatalogEntry> current = Schemas(request.Kind);
        if (!current.TryGetValue(request.Name, out SchemaCatalogEntry? removed))
        {
            return null;
        }

        uint revision = checked(_document.Revision + 1);
        SchemaCatalogDocument replacement = _document.Copy(revision);
        replacement.Schemas(request.Kind).Remove(request.Name);
        await CatalogJson.ReplaceAsync(_path, replacement, cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        _document = replacement;
        return new SchemaRemoveResultMessage(removed.Fingerprint, removed.Version);
    }

    public Task<SchemaRegistrationMessage?> GetAsync(
        SchemaLookupMessage request,
        GrainCancellationToken cancellationToken)
    {
        cancellationToken.CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Schemas(request.Kind).TryGetValue(request.Name, out SchemaCatalogEntry? entry)
            ? ToMessage(entry, request.Kind)
            : null);
    }

    public Task<SchemaListMessage> ListAsync(
        ActorSchemaKind kind,
        GrainCancellationToken cancellationToken)
    {
        cancellationToken.CancellationToken.ThrowIfCancellationRequested();
        IEnumerable<SchemaRegistrationMessage> schemas = kind switch
        {
            ActorSchemaKind.Unspecified => _document.EventSchemas.Values
                .Select(entry => ToMessage(entry, ActorSchemaKind.Event))
                .Concat(_document.CommandSchemas.Values.Select(entry =>
                    ToMessage(entry, ActorSchemaKind.Command))),
            ActorSchemaKind.Event => _document.EventSchemas.Values.Select(entry =>
                ToMessage(entry, ActorSchemaKind.Event)),
            ActorSchemaKind.Command => _document.CommandSchemas.Values.Select(entry =>
                ToMessage(entry, ActorSchemaKind.Command)),
            _ => throw new ArgumentException("The schema kind is invalid.", nameof(kind))
        };
        return Task.FromResult(new SchemaListMessage(
            schemas.OrderBy(value => value.Kind).ThenBy(value => value.Name, StringComparer.Ordinal).ToArray(),
            _document.Revision));
    }

    public Task<SchemaSnapshotMessage> GetSnapshotAsync(GrainCancellationToken cancellationToken)
    {
        cancellationToken.CancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SchemaSnapshotMessage(
            _document.EventSchemas.Values
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .Select(value => ToMessage(value, ActorSchemaKind.Event)).ToArray(),
            _document.CommandSchemas.Values
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .Select(value => ToMessage(value, ActorSchemaKind.Command)).ToArray(),
            _document.Revision));
    }

    private Dictionary<string, SchemaCatalogEntry> Schemas(ActorSchemaKind kind)
    {
        return kind switch
        {
            ActorSchemaKind.Event => _document.EventSchemas,
            ActorSchemaKind.Command => _document.CommandSchemas,
            _ => throw new ArgumentException("A schema kind is required.", nameof(kind))
        };
    }

    private static SchemaRegistrationMessage ToMessage(SchemaCatalogEntry entry, ActorSchemaKind kind)
    {
        return new SchemaRegistrationMessage(
            entry.Name, kind, entry.DocumentJson, entry.Fingerprint, entry.Version);
    }

    private static SchemaRegistrationResultMessage InvalidRegistration(string message)
    {
        return new SchemaRegistrationResultMessage(
            string.Empty,
            Version: 0,
            Registered: false,
            Diagnostics: [],
            new CatalogErrorMessage(CatalogErrorKind.InvalidArgument, message));
    }

    private static ActorDiagnosticMessage Error(string code, string message)
    {
        return new ActorDiagnosticMessage(code, ActorDiagnosticSeverity.Error, message);
    }

    private sealed class SchemaCatalogDocument
    {
        public int FormatVersion { get; init; } = 1;
        public uint Revision { get; init; }
        public Dictionary<string, SchemaCatalogEntry> EventSchemas { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, SchemaCatalogEntry> CommandSchemas { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, SchemaCatalogEntry> Schemas(ActorSchemaKind kind)
        {
            return kind == ActorSchemaKind.Event ? EventSchemas : CommandSchemas;
        }

        public SchemaCatalogDocument Copy(uint revision)
        {
            return new SchemaCatalogDocument
            {
                Revision = revision,
                EventSchemas = new Dictionary<string, SchemaCatalogEntry>(EventSchemas, StringComparer.Ordinal),
                CommandSchemas = new Dictionary<string, SchemaCatalogEntry>(CommandSchemas, StringComparer.Ordinal)
            };
        }
    }
}