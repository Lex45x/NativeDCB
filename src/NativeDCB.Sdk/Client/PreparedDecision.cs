using System.Text.Json;

using Google.Protobuf;

using NativeDCB.Protocol.V1;

namespace NativeDCB.Sdk.Client;

public sealed class PreparedDecision<TModel>
{
    // ReSharper disable once ReplaceWithFieldKeyword -- Supports runtimes before the field keyword is available.
    private readonly TModel? _model;

    internal PreparedDecision(PrepareDecisionResponse response, JsonSerializerOptions jsonOptions)
    {
        Response = response ?? throw new ArgumentNullException(nameof(response));
        CommandId = Guid.Parse(response.CommandId);
        if (response.OutcomeCase == PrepareDecisionResponse.OutcomeOneofCase.Prepared)
        {
            JsonSerializerOptions modelOptions = new(jsonOptions) { PropertyNameCaseInsensitive = true };
            _model = JsonSerializer.Deserialize<TModel>(response.Prepared.ModelJson.Span, modelOptions) ??
                     throw new JsonException("The prepared decision model cannot be null.");
        }
    }

    public PrepareDecisionResponse Response { get; }

    public PrepareDecisionResponse.OutcomeOneofCase OutcomeCase => Response.OutcomeCase;

    public bool IsPrepared => OutcomeCase == PrepareDecisionResponse.OutcomeOneofCase.Prepared;

    public Guid CommandId { get; }

    public string CommandType => Response.CommandType;

    public TModel Model
    {
        get
        {
            EnsurePrepared();
            return _model!;
        }
    }

    public ByteString ModelSignature => GetPrepared().ModelSignature;

    public DateTimeOffset ExpiresUtc => GetPrepared().ExpiresUtc?.ToDateTimeOffset() ??
                                        throw new InvalidOperationException(
                                            "The prepared decision response does not contain an expiry.");

    public string PlanFingerprint => GetPrepared().PlanFingerprint;

    private PreparedDecision GetPrepared()
    {
        EnsurePrepared();
        return Response.Prepared;
    }

    private void EnsurePrepared()
    {
        if (!IsPrepared)
        {
            throw new InvalidOperationException($"The decision outcome is {OutcomeCase}, not Prepared.");
        }
    }
}