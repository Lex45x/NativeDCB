using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

using Grpc.Net.Client;

using NativeDCB.Commerce;
using NativeDCB.Commerce.Commands;
using NativeDCB.Commerce.Fixtures;
using NativeDCB.Commerce.Verification;
using NativeDCB.Commerce.Workloads;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Client;
using NativeDCB.SystemBenchmarks.Hosting;
using NativeDCB.SystemBenchmarks.Metrics;

namespace NativeDCB.SystemBenchmarks.Scenarios;

internal sealed record PreparedBenchmarkSlot(
    string Database,
    CommerceWorkloadSlot Workload,
    string Sku,
    string WarehouseId);

internal sealed record ScenarioVerification(
    bool Valid,
    IReadOnlyList<string> Errors,
    long EndHead,
    IReadOnlyList<object> DatabaseStatus);

internal sealed class BenchmarkRuntime : IDisposable
{
    private const string HotSku = "benchmark-hot-sku";
    private const string HotWarehouse = "benchmark-hot-warehouse";
    private readonly object _clientLock = new();
    private readonly int _concurrency;
    private readonly List<PreparedBenchmarkSlot> _measurementSlots = [];
    private readonly List<PreparedBenchmarkSlot> _warmupSlots = [];
    private readonly ConcurrentDictionary<string, DuplicateCohort> _duplicateCohorts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReservationCheckoutPair> _reservationPairs = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<SubscriptionObservation> _subscriptionObservations = [];
    private readonly Dictionary<string, QueryFixture> _queryFixtures = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _derivedVariant = new(1, 1);
    private readonly Dictionary<string, (int Partitions, int States)> _measurementStorageBaseline = new(StringComparer.Ordinal);
    private ClientConnection _connection;
    private long _measurementSequence;
    private long _warmupSequence;

    public BenchmarkRuntime(BenchmarkOptions options, int concurrency, ServerProcess server, RunMetrics metrics)
    {
        Options = options;
        _concurrency = concurrency;
        Server = server;
        Metrics = metrics;
        Fixture = new CommerceFixture(options.FixtureOptions);
        Databases = Enumerable.Range(1, options.Databases).Select(i => $"benchmark-{i:D2}").ToArray();
        _connection = new ClientConnection(server.Address);
    }

    public BenchmarkOptions Options { get; }
    public ServerProcess Server { get; }
    public RunMetrics Metrics { get; }
    public CommerceFixture Fixture { get; }
    public IReadOnlyList<string> Databases { get; }
    public NativeDcbClient Client { get { lock (_clientLock) return _connection.Client; } }
    public int? MeasurementOperationLimit { get; private set; }
    public Guid RecoveryActivationCommandId { get; private set; }
    public Guid RecoveryOldCommandId { get; private set; }
    public PublishProduct RecoveryActivationCommand { get; } = new("recovery-activation", "Recovery activation", 1, "USD");
    public PublishProduct RecoveryOldCommand { get; } = new("recovery-old", "Recovery old command", 1, "USD");
    internal int Concurrency => _concurrency;
    internal int DatabaseCount => Databases.Count;
    internal string FirstDatabase => Databases[0];

    public long NextSequence(bool measure) => measure
        ? Interlocked.Increment(ref _measurementSequence)
        : Interlocked.Increment(ref _warmupSequence);

    public string Database(long sequence) => Databases[(int)((sequence - 1) % Databases.Count)];

    public PreparedBenchmarkSlot PreparedSlot(long sequence, bool measure)
    {
        List<PreparedBenchmarkSlot> source = measure ? _measurementSlots : _warmupSlots;
        if (source.Count == 0) throw new InvalidOperationException("The scenario has no prepared workload slots.");
        return source[(int)((sequence - 1) % source.Count)];
    }

    public Guid CommandId(bool measure, string operation, long ordinal)
    {
        uint scope = CommerceCommandIds.ScopeForSeed(Options.Seed + (measure ? 1_000_003 : 2_000_003));
        uint operationHash = 2166136261;
        foreach (char value in operation) operationHash = unchecked((operationHash ^ value) * 16777619);
        scope ^= operationHash;
        if (scope == 0) scope = 1;
        return CommerceCommandIds.Create(scope, ordinal);
    }

    public async Task SetupAsync(CancellationToken cancellationToken)
    {
        foreach (string database in Databases)
        {
            await CommerceSeeder.SeedAsync(Client, database, cancellationToken);
            CommerceFixturePopulationResult populated = await CommerceFixturePopulation.PopulateAsync(
                Client, database, Fixture, cancellationToken);
            if (populated.AppendedEventCount != Fixture.Options.PreloadedEventCount)
                throw new InvalidOperationException($"{database}: fixture appended {populated.AppendedEventCount}, expected {Fixture.Options.PreloadedEventCount}.");
        }

        if (Options.Mode == ScheduleMode.OpenLoop)
            MeasurementOperationLimit = OpenLoopPlan.OperationBudget(
                Options.Rate!.Value, Options.Duration, Options.Operations);
        else if (Options.Operations.HasValue)
            MeasurementOperationLimit = Options.Operations.Value;
        await Options.Strategy.SetupAsync(this, cancellationToken);
    }

    internal void EnsureMeasurementOperationLimit(int minimum) =>
        MeasurementOperationLimit = Math.Max(MeasurementOperationLimit ?? 0, minimum);

    internal void DefaultMeasurementOperationLimit(int value) => MeasurementOperationLimit ??= value;

    internal void SetMeasurementOperationLimit(int value) => MeasurementOperationLimit = value;

    internal int RoundUpToConcurrency(int value) => RoundUp(value, _concurrency);

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        lock (_clientLock)
        {
            _connection.Dispose();
            _connection = ClientConnection.Empty;
        }
        await Server.StopAsync();
        await Server.StartAsync(cancellationToken);
        lock (_clientLock) _connection = new ClientConnection(Server.Address);
    }

    public async Task StopForRecoveryAsync()
    {
        lock (_clientLock)
        {
            _connection.Dispose();
            _connection = ClientConnection.Empty;
        }
        await Server.StopAsync();
    }

    public Task LaunchForRecoveryAsync(CancellationToken cancellationToken) => Server.LaunchAsync(cancellationToken);

    public async Task WaitForLivenessAndReconnectAsync(CancellationToken cancellationToken)
    {
        await Server.WaitForLivenessAsync(cancellationToken);
        lock (_clientLock) _connection = new ClientConnection(Server.Address);
    }

    public async Task WaitForDatabaseReadinessAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.StartupTimeout);
        foreach (string database in Databases)
        {
            while (true)
            {
                GetDatabaseInfoResponse info = await Client.GetDatabaseInfoAsync(database, timeout.Token);
                if (info.Database.ReadAvailable && info.Database.State is DatabaseState.Ready or DatabaseState.Discovered) break;
                await Task.Delay(50, timeout.Token);
            }
        }
    }

    public QueryFixture GetQueryFixture(string database) => _queryFixtures[database];

    public string StockedWarehouse(string sku)
    {
        int index = Fixture.Skus.IndexOf(sku);
        if (index < 0) throw new InvalidOperationException($"Fixture does not contain SKU '{sku}'.");
        return Fixture.SelectWarehouseId(index);
    }

    public void RecordDuplicate(string database, CommerceSemanticOutcome outcome)
    {
        DuplicateCohort cohort = _duplicateCohorts.GetOrAdd(database, _ => new DuplicateCohort());
        Interlocked.Increment(ref cohort.Attempts);
        if (outcome == CommerceSemanticOutcome.Committed) Interlocked.Increment(ref cohort.Committed);
        if (outcome == CommerceSemanticOutcome.Reconciled) Interlocked.Increment(ref cohort.Reconciled);
    }

    public void RecordReservationPair(PreparedBenchmarkSlot slot, ExecuteHandlerResponse reserve,
        ExecuteHandlerResponse checkout)
    {
        _reservationPairs[slot.Workload.CartId] = new ReservationCheckoutPair(
            slot.Database, slot.Workload.CartId, slot.Workload.OrderId,
            CommerceOutcomes.Classify(reserve), CommerceOutcomes.Classify(checkout));
    }

    public void RecordSubscription(string database, long cursor, long firstEventId, long secondEventId)
    {
        _subscriptionObservations.Add(new SubscriptionObservation(database, cursor, firstEventId, secondEventId));
        Metrics.RecordSubscription(2, secondEventId - cursor);
    }

    internal async Task CaptureMeasurementStorageBaselineAsync(CancellationToken cancellationToken)
    {
        _measurementStorageBaseline.Clear();
        foreach (string database in Databases)
        {
            ListPartitionsResponse partitions = await Client.ListPartitionsAsync(database, cancellationToken);
            _measurementStorageBaseline[database] = (
                partitions.Partitions.Count,
                partitions.Partitions.Count(value => value.StateFile?.Present == true));
        }
    }

    public async Task<ScenarioVerification> VerifyAsync(long startHead, CancellationToken cancellationToken)
    {
        List<string> errors = [];
        List<object> databaseStatus = [];
        long endHead = 0;
        foreach (string database in Databases)
        {
            long head = (await Client.GetHeadAsync(database, cancellationToken)).EventId;
            endHead += head;
            List<SequencedEvent> history = [];
            await foreach (SequencedEvent value in Client.ReadEventsByRangeAsync(database, cancellationToken: cancellationToken))
                history.Add(value);
            CommerceVerificationResult verification = CommerceCorrectness.VerifyHistory(history);
            errors.AddRange(verification.Issues.Select(issue =>
                $"{database}:{issue.Code}{(issue.EventId.HasValue ? $"@{issue.EventId}" : "")}: {issue.Message}"));
            if (verification.Summary.EndingEventId != head)
                errors.Add($"{database}: verified final event {verification.Summary.EndingEventId} differs from head {head}.");
            ListPartitionsResponse partitions = await Client.ListPartitionsAsync(database, cancellationToken);
            ListIndexesResponse indexes = await Client.ListIndexesAsync(database, cancellationToken);
            databaseStatus.Add(new
            {
                database,
                partitions = partitions.Partitions.Count,
                stateFilesPresent = partitions.Partitions.Count(x => x.StateFile?.Present == true),
                indexes = indexes.Indexes.Count,
                maximumIndexLag = indexes.Indexes.Count == 0 ? 0UL : indexes.Indexes.Max(x => x.Lag)
            });
        }

        long delta = endHead - startHead;
        if (delta != Metrics.CommittedEvents)
            errors.Add($"Head delta {delta} does not equal the {Metrics.CommittedEvents} committed events reported by operations.");
        if (Metrics.Failed != 0) errors.Add($"{Metrics.Failed} transport, client, or unexpected semantic operations failed.");
        if (Server.UnexpectedExit) errors.Add("The server process exited unexpectedly during the run.");
        await Options.Strategy.VerifyAsync(this, errors, cancellationToken);
        return new ScenarioVerification(errors.Count == 0, errors, endHead, databaseStatus);
    }

    public async Task<long> AggregateHeadAsync(CancellationToken cancellationToken)
    {
        long head = 0;
        foreach (string database in Databases) head += (await Client.GetHeadAsync(database, cancellationToken)).EventId;
        return head;
    }

    internal async Task WaitForRolloverStateAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.StartupTimeout);
        foreach (string database in Databases)
        {
            if (!_measurementStorageBaseline.TryGetValue(database, out var baseline))
                throw new InvalidOperationException($"{database}: measurement storage baseline was not captured.");
            while (true)
            {
                ListPartitionsResponse partitions = await Client.ListPartitionsAsync(database, timeout.Token);
                PartitionStatus[] closed = partitions.Partitions.Where(x => !x.Active).ToArray();
                int states = partitions.Partitions.Count(x => x.StateFile?.Present == true);
                if (partitions.Partitions.Count > baseline.Partitions && states > baseline.States &&
                    closed.All(x => x.StateFile is { Present: true, JsonValid: true, SchemaValid: true })) break;
                await Task.Delay(100, timeout.Token);
            }
        }
    }

    public void Dispose()
    {
        lock (_clientLock) _connection.Dispose();
        _derivedVariant.Dispose();
    }

    internal int FiniteCohortSize()
    {
        int profileMaximum = Options.Profile switch
        {
            BenchmarkProfile.Smoke => 32,
            BenchmarkProfile.Standard => 2_048,
            BenchmarkProfile.Soak => 32_768,
            _ => 32
        };
        long demand = Options.Mode == ScheduleMode.OpenLoop
            ? MeasurementOperationLimit ?? OpenLoopPlan.OperationBudget(
                Options.Rate!.Value, Options.Duration, Options.Operations)
            : Math.Max((long)_concurrency * 4, Databases.Count);
        int result = Math.Max(Databases.Count, (int)Math.Min(profileMaximum, demand));
        return Options.Mode == ScheduleMode.SynchronizedBurst ? RoundUp(result, _concurrency) : result;
    }

    private static int RoundUp(int value, int multiple) => ((value + multiple - 1) / multiple) * multiple;

    internal async Task PrepareIndependentSlotsAsync(int count, bool measure, CancellationToken token)
    {
        List<PreparedBenchmarkSlot> target = measure ? _measurementSlots : _warmupSlots;
        uint scope = CommerceCommandIds.ScopeForSeed(Options.Seed + (measure ? 10_001 : 20_001));
        long baseOrdinal = measure ? 0 : 1_000_000;
        for (int index = 0; index < count; index++)
        {
            string database = Databases[index % Databases.Count];
            CommerceWorkloadSlot slot = CommerceWorkloadSlot.Create(Fixture, scope, baseOrdinal + index);
            string sku = $"{(measure ? "independent" : "warm-independent")}-sku-{index + 1:D12}";
            await RequireCommitted(database, new PublishProduct(sku, sku, 1_000, "USD"), slot.CommandId(1), token);
            await RequireCommitted(database, new ReceiveInventory(slot.ReceiptId, sku, slot.WarehouseId, 1), slot.CommandId(2), token);
            await RequireCommitted(database, new RegisterCustomer(slot.CustomerId, slot.CustomerId), slot.CommandId(3), token);
            await RequireCommitted(database, new OpenCart(slot.CartId, slot.CustomerId, "USD"), slot.CommandId(4), token);
            target.Add(new PreparedBenchmarkSlot(database, slot, sku, slot.WarehouseId));
        }
    }

    internal async Task PrepareHotSlotsAsync(int count, bool measure, CancellationToken token)
    {
        List<PreparedBenchmarkSlot> target = measure ? _measurementSlots : _warmupSlots;
        uint scope = CommerceCommandIds.ScopeForSeed(Options.Seed + (measure ? 30_001 : 40_001));
        long baseOrdinal = measure ? 0 : 1_000_000;
        foreach (string database in Databases)
        {
            string suffix = measure ? "measure" : "warm";
            int databaseAttempts = Enumerable.Range(0, count).Count(i => Databases[i % Databases.Count] == database);
            string sku = $"{HotSku}-{suffix}", warehouse = $"{HotWarehouse}-{suffix}";
            await RequireCommitted(database, new PublishProduct(sku, sku, 1_000, "USD"),
                CommerceCommandIds.Create(scope, baseOrdinal + 1 + Databases.IndexOf(database) * 2), token);
            await RequireCommitted(database, new ReceiveInventory($"hot-receipt-{suffix}-{database}", sku, warehouse,
                    Math.Max(1, databaseAttempts / 2)),
                CommerceCommandIds.Create(scope, baseOrdinal + 2 + Databases.IndexOf(database) * 2), token);
        }
        for (int index = 0; index < count; index++)
        {
            string database = Databases[index % Databases.Count];
            CommerceWorkloadSlot slot = CommerceWorkloadSlot.Create(Fixture, scope, baseOrdinal + 10_000 + index);
            await RequireCommitted(database, new RegisterCustomer(slot.CustomerId, slot.CustomerId), slot.CommandId(1), token);
            await RequireCommitted(database, new OpenCart(slot.CartId, slot.CustomerId, "USD"), slot.CommandId(2), token);
            target.Add(new PreparedBenchmarkSlot(database, slot,
                $"{HotSku}-{(measure ? "measure" : "warm")}", $"{HotWarehouse}-{(measure ? "measure" : "warm")}"));
        }
    }

    internal async Task PrepareRaceSlotsAsync(int count, bool measure, CancellationToken token)
    {
        List<PreparedBenchmarkSlot> target = measure ? _measurementSlots : _warmupSlots;
        uint scope = CommerceCommandIds.ScopeForSeed(Options.Seed + (measure ? 50_001 : 60_001));
        long baseOrdinal = measure ? 0 : 1_000_000;
        for (int index = 0; index < count; index++)
        {
            string database = Databases[index % Databases.Count];
            CommerceWorkloadSlot slot = CommerceWorkloadSlot.Create(Fixture, scope, baseOrdinal + index);
            await RequireCommitted(database, new RegisterCustomer(slot.CustomerId, slot.CustomerId), slot.CommandId(1), token);
            await RequireCommitted(database, new OpenCart(slot.CartId, slot.CustomerId, "USD"), slot.CommandId(2), token);
            target.Add(new PreparedBenchmarkSlot(database, slot, slot.Sku, StockedWarehouse(slot.Sku)));
        }
    }

    internal async Task PrepareRecoveryAsync(CancellationToken token)
    {
        uint scope = CommerceCommandIds.ScopeForSeed(Options.Seed + 70_001);
        RecoveryActivationCommandId = CommerceCommandIds.Create(scope, 1);
        RecoveryOldCommandId = CommerceCommandIds.Create(scope, 2);
        await RequireCommitted(Databases[0], RecoveryActivationCommand, RecoveryActivationCommandId, token);
        await RequireCommitted(Databases[0], RecoveryOldCommand, RecoveryOldCommandId, token);
    }

    internal async Task PrepareQueriesAsync(CancellationToken token)
    {
        uint scope = CommerceCommandIds.ScopeForSeed(Options.Seed + 80_001);
        for (int index = 0; index < Databases.Count; index++)
        {
            string database = Databases[index];
            CommerceWorkloadSlot slot = CommerceWorkloadSlot.Create(Fixture, scope, index);
            await RequireCommitted(database, new RegisterCustomer(slot.CustomerId, slot.CustomerId), slot.CommandId(1), token);
            await RequireCommitted(database, new OpenCart(slot.CartId, slot.CustomerId, "USD"), slot.CommandId(2), token);
            string warehouse = StockedWarehouse(slot.Sku);
            await RequireCommitted(database, new ReserveCartLine(slot.CartId, slot.LineId, slot.CustomerId,
                slot.Sku, warehouse, 1), slot.CommandId(3), token);
            await RequireCommitted(database, new CheckoutCart(slot.CartId, slot.OrderId, slot.CustomerId), slot.CommandId(4), token);

            EventQuery inventory = CommerceQueries.Inventory(slot.Sku, warehouse);
            EventQuery order = CommerceQueries.Order(slot.OrderId);
            IReadOnlyList<long> inventoryExpected = await ReadQueryIds(database, inventory, QueryConsistency.CommittedScan, token);
            IReadOnlyList<long> orderExpected = await ReadQueryIds(database, order, QueryConsistency.CommittedScan, token);
            List<long> productTypeExpected = [];
            await foreach (SequencedEvent value in Client.ReadEventsByRangeAsync(database, cancellationToken: token))
                if (value.Type == "ProductPublished") productTypeExpected.Add(value.EventId);
            await WaitForCurrentIndexes(database, inventory, inventoryExpected, token);
            await WaitForCurrentIndexes(database, order, orderExpected, token);
            _queryFixtures[database] = new QueryFixture(inventory, inventoryExpected, order, orderExpected,
                productTypeExpected);
        }
    }

    private async Task WaitForCurrentIndexes(string database, EventQuery query, IReadOnlyList<long> expected, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Options.StartupTimeout);
        foreach (var identity in query.Items.SelectMany(item => item.EventTypes.SelectMany(type =>
                     item.Keys.Select(key => (Type: type, Key: key)))).Distinct())
        {
            RebuildResponse rebuild = await Client.RequestIndexRebuildAsync(
                database, identity.Type, [identity.Key], timeout.Token);
            if (!rebuild.Accepted)
                throw new InvalidOperationException($"Index rebuild was not accepted for {identity.Type}/{identity.Key.Name}={identity.Key.Value}: {rebuild.Message}");
        }
        while (true)
        {
            IReadOnlyList<long> eventual = await ReadQueryIds(database, query, QueryConsistency.EventualIndex, timeout.Token);
            ListIndexesResponse statuses = await Client.ListIndexesAsync(database, timeout.Token);
            bool current = query.Items.SelectMany(item => item.EventTypes.SelectMany(type => item.Keys.Select(key => (type, key))))
                .All(required => statuses.Indexes.Any(status => status.EventType == required.type && status.Lag == 0 &&
                    status.Keys.Any(key => key.Key == required.key.Name && key.Value == required.key.Value)));
            if (current && eventual.SequenceEqual(expected)) return;
            await Task.Delay(50, timeout.Token);
        }
    }

    public async Task<IReadOnlyList<long>> ReadQueryIds(string database, EventQuery query,
        QueryConsistency consistency, CancellationToken token)
    {
        List<long> ids = [];
        await foreach (SequencedEvent value in Client.ReadEventsByQueryAsync(database, query,
                           consistency: consistency, cancellationToken: token)) ids.Add(value.EventId);
        return ids;
    }

    public async Task<IReadOnlyList<long>> ReadDerivedFallbackVariant(string database, EventQuery query,
        IReadOnlyList<long> expected, string variant, CancellationToken cancellationToken)
    {
        await _derivedVariant.WaitAsync(cancellationToken);
        string? indexDirectory = null;
        Dictionary<string, byte[]> backup = [];
        try
        {
            await StopForRecoveryAsync();
            indexDirectory = FindIndexDirectory(database, query);
            backup = Directory.GetFiles(indexDirectory, "*", SearchOption.AllDirectories)
                .ToDictionary(path => Path.GetRelativePath(indexDirectory, path), File.ReadAllBytes,
                    StringComparer.OrdinalIgnoreCase);
            switch (variant)
            {
                case "stale": CreateStaleGeneration(indexDirectory); break;
                case "missing": DeleteDerivedFiles(indexDirectory); break;
                case "corrupt": File.WriteAllText(Path.Combine(indexDirectory, "manifest_v1.json"), "{ corrupt"); break;
                default: throw new ArgumentOutOfRangeException(nameof(variant));
            }
            await Server.StartAsync(cancellationToken);
            lock (_clientLock) _connection = new ClientConnection(Server.Address);
            await WaitForDatabaseReadinessAsync(cancellationToken);
            IReadOnlyList<long> actual = await ReadQueryIds(database, query, QueryConsistency.EventualIndex,
                cancellationToken);
            if (!actual.SequenceEqual(expected))
                throw new InvalidOperationException($"{variant} EVENTUAL_INDEX fallback differed from the committed baseline.");
            ListIndexesResponse statuses = await Client.ListIndexesAsync(database, cancellationToken);
            string directoryName = Path.GetFileName(indexDirectory);
            IndexStatus[] targetStatuses = statuses.Indexes
                .Where(status => status.Filename.Contains(directoryName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (variant == "stale" && !targetStatuses.Any(status => status.Lag > 0))
                throw new InvalidOperationException("Stale index variant exposed no public index lag.");
            if (variant is "missing" or "corrupt" && targetStatuses.Length != 0)
                throw new InvalidOperationException($"{variant} index variant remained in public index status.");
            return actual;
        }
        finally
        {
            try
            {
                if (Server.CurrentProcess is { HasExited: false }) await StopForRecoveryAsync();
                if (indexDirectory is not null)
                {
                    if (Directory.Exists(indexDirectory)) Directory.Delete(indexDirectory, recursive: true);
                    Directory.CreateDirectory(indexDirectory);
                    foreach ((string relative, byte[] bytes) in backup)
                    {
                        string path = Path.Combine(indexDirectory, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        File.WriteAllBytes(path, bytes);
                    }
                }
                await Server.StartAsync(CancellationToken.None);
                lock (_clientLock) _connection = new ClientConnection(Server.Address);
                await WaitForDatabaseReadinessAsync(CancellationToken.None);
            }
            finally { _derivedVariant.Release(); }
        }
    }

    private string FindIndexDirectory(string database, EventQuery query)
    {
        string root = Path.Combine(Server.DatabaseRoot, database, "indexes");
        HashSet<string> eventTypes = query.Items.SelectMany(item => item.EventTypes).ToHashSet(StringComparer.Ordinal);
        foreach (string manifestPath in Directory.GetFiles(root, "manifest_v1.json", SearchOption.AllDirectories))
        {
            try
            {
                JsonNode? manifest = JsonNode.Parse(File.ReadAllText(manifestPath));
                string? eventType = StringProperty(manifest, "eventType");
                if (eventType is not null && eventTypes.Contains(eventType)) return Path.GetDirectoryName(manifestPath)!;
            }
            catch (System.Text.Json.JsonException) { }
        }
        throw new InvalidOperationException("No published derived index directory matched the query.");
    }

    private static void CreateStaleGeneration(string indexDirectory)
    {
        string manifestPath = Path.Combine(indexDirectory, "manifest_v1.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        string generationFile = StringProperty(manifest, "generationFile") ??
                                throw new InvalidOperationException("Index manifest has no generation file.");
        JsonObject generation = JsonNode.Parse(File.ReadAllText(Path.Combine(indexDirectory, generationFile)))!.AsObject();
        long staleHead = Math.Max(0, LongProperty(generation, "head") - 1);
        SetProperty(generation, "head", JsonValue.Create(staleHead));
        if (NodeProperty(generation, "events") is JsonArray events)
        {
            foreach (JsonNode? item in events.ToArray())
                if (item is not null && LongProperty(item, "eventId") > staleHead) events.Remove(item);
        }
        string staleFile = $"generation_{staleHead:D20}_benchmark_stale_v1.json";
        File.WriteAllText(Path.Combine(indexDirectory, staleFile), generation.ToJsonString());
        SetProperty(manifest, "head", JsonValue.Create(staleHead));
        SetProperty(manifest, "generationFile", JsonValue.Create(staleFile));
        File.WriteAllText(manifestPath, manifest.ToJsonString());
    }

    private static void DeleteDerivedFiles(string indexDirectory)
    {
        foreach (string path in Directory.GetFiles(indexDirectory)) File.Delete(path);
    }

    private static JsonNode? NodeProperty(JsonNode? node, string name)
    {
        if (node is not JsonObject value) return null;
        return value.FirstOrDefault(property => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    }
    private static string? StringProperty(JsonNode? node, string name) => NodeProperty(node, name)?.GetValue<string>();
    private static long LongProperty(JsonNode? node, string name) => NodeProperty(node, name)?.GetValue<long>() ??
        throw new InvalidOperationException($"Derived JSON has no {name}.");
    private static void SetProperty(JsonObject value, string name, JsonNode? replacement)
    {
        string key = value.Select(property => property.Key)
            .FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        value[key] = replacement;
    }

    private async Task RequireCommitted<T>(string database, T command, Guid commandId, CancellationToken token)
        where T : notnull
    {
        ExecuteHandlerResponse response = await Client.ExecuteHandlerAsync(database, typeof(T).Name, command, commandId, token);
        CommerceSemanticOutcome outcome = CommerceOutcomes.Classify(response);
        if (!CommerceExpectedOutcomes.FixturePopulation.Allows(outcome))
            throw new UnexpectedSemanticOutcomeException($"setup:{typeof(T).Name}", outcome, CommerceExpectedOutcomes.FixturePopulation);
    }

    internal void VerifyFiniteOperationCount(List<string> errors)
    {
        long accounted = Metrics.Completed + Metrics.Dropped;
        if (MeasurementOperationLimit.HasValue && accounted != MeasurementOperationLimit.Value)
            errors.Add($"Finite cohort accounted for {Metrics.Completed} completed and {Metrics.Dropped} dropped operations; expected {MeasurementOperationLimit.Value} planned operations.");
    }

    internal void VerifyIndependentWrites(List<string> errors)
    {
        if (Metrics.CommittedEvents != Metrics.Completed * 2L)
            errors.Add($"Independent writes committed {Metrics.CommittedEvents} events; expected exactly {Metrics.Completed * 2L} for executed operations.");
    }

    internal void VerifyHotInventory(List<string> errors)
    {
        int completed = checked((int)Metrics.Completed);
        int expectedCommitted = Enumerable.Range(0, completed)
            .GroupBy(index => Databases[index % Databases.Count])
            .Sum(group => Math.Max(1, group.Count() / 2));
        long hotCommitted = Metrics.OperationOutcomeCount("hot-inventory", CommerceSemanticOutcome.Committed);
        long rejected = Metrics.OperationOutcomeCount("hot-inventory", CommerceSemanticOutcome.Rejected);
        if (hotCommitted != expectedCommitted || rejected != completed - expectedCommitted)
            errors.Add($"Hot inventory outcomes were committed={hotCommitted}, rejected={rejected}; expected {expectedCommitted}/{completed - expectedCommitted}.");
    }

    internal async Task VerifyReservationCheckoutAsync(List<string> errors, CancellationToken cancellationToken)
    {
        if (_reservationPairs.Count != Metrics.Completed)
            errors.Add($"Reservation-checkout recorded {_reservationPairs.Count} pairs, expected {Metrics.Completed} executed operations.");
        foreach (ReservationCheckoutPair pair in _reservationPairs.Values)
        {
            if (pair.Reserve != CommerceSemanticOutcome.Committed)
                errors.Add($"{pair.Database}/{pair.Cart}: reservation did not commit ({pair.Reserve}).");
            if (pair.Checkout is not (CommerceSemanticOutcome.Committed or CommerceSemanticOutcome.Rejected))
                errors.Add($"{pair.Database}/{pair.Cart}: checkout returned {pair.Checkout}.");
        }

        foreach (string database in Databases)
        {
            List<SequencedEvent> history = [];
            await foreach (SequencedEvent value in Client.ReadEventsByRangeAsync(database,
                               cancellationToken: cancellationToken)) history.Add(value);
            foreach (ReservationCheckoutPair pair in _reservationPairs.Values.Where(value => value.Database == database))
            {
                bool Cart(SequencedEvent value) => value.Keys.Any(key => key.Name == "cart" && key.Value == pair.Cart);
                bool Order(SequencedEvent value) => value.Keys.Any(key => key.Name == "order" && key.Value == pair.Order);
                int reservations = history.Count(value => value.Type == "CartLineReserved" && Cart(value));
                int checkouts = history.Count(value => value.Type == "CartCheckedOut" && Cart(value));
                int orders = history.Count(value => value.Type == "OrderPlaced" && Order(value));
                if (reservations != 1) errors.Add($"{database}/{pair.Cart}: persisted {reservations} reservations, expected one.");
                int expectedCheckout = pair.Checkout == CommerceSemanticOutcome.Committed ? 1 : 0;
                if (checkouts != expectedCheckout || orders != expectedCheckout)
                    errors.Add($"{database}/{pair.Cart}: checkout/order persisted {checkouts}/{orders}, expected {expectedCheckout}/{expectedCheckout}.");
            }
        }
    }

    internal async Task VerifyDuplicateCommandAsync(List<string> errors, CancellationToken cancellationToken)
    {
        int attempts = 0, committed = 0, reconciled = 0;
        foreach ((string database, DuplicateCohort cohort) in _duplicateCohorts)
        {
            int databaseAttempts = Volatile.Read(ref cohort.Attempts);
            attempts += databaseAttempts;
            committed += Volatile.Read(ref cohort.Committed);
            reconciled += Volatile.Read(ref cohort.Reconciled);
            if (databaseAttempts < 2) errors.Add($"{database}: duplicate cohort had fewer than two simultaneous attempts.");
            GetEventsByCommandIdResponse events = await Client.GetEventsByCommandIdAsync(
                database, CommandId(measure: true, "duplicate", 1), cancellationToken);
            if (events.Events.Count != 1) errors.Add($"{database}: duplicate cohort persisted {events.Events.Count} events, expected exactly one.");
        }
        if (attempts != MeasurementOperationLimit) errors.Add($"Duplicate cohort executed {attempts} attempts, expected {MeasurementOperationLimit}.");
        if (committed != _duplicateCohorts.Count) errors.Add($"Duplicate cohort had {committed} first commits for {_duplicateCohorts.Count} participating databases.");
        if (reconciled != attempts - committed) errors.Add($"Duplicate cohort had {reconciled} reconciliations, expected {attempts - committed}.");
        if (_duplicateCohorts.Count != Databases.Count)
            errors.Add($"Duplicate cohort covered {_duplicateCohorts.Count} databases, expected {Databases.Count}.");
    }

    internal void VerifyOperationCoverage(IReadOnlyList<string> required, List<string> errors)
    {
        IReadOnlySet<string> names = Metrics.OperationNames;
        foreach (string operation in required)
            if (!names.Contains(operation)) errors.Add($"Required operation/variant '{operation}' was not measured.");
    }

    internal async Task VerifySubscriptionObservationsAsync(List<string> errors,
        CancellationToken cancellationToken)
    {
        foreach (SubscriptionObservation observation in _subscriptionObservations)
        {
            List<long> persisted = [];
            await foreach (SequencedEvent value in Client.ReadEventsByRangeAsync(observation.Database,
                               afterEventId: observation.Cursor, throughEventId: observation.SecondEventId,
                               cancellationToken: cancellationToken)) persisted.Add(value.EventId);
            int first = persisted.IndexOf(observation.FirstEventId);
            int second = persisted.IndexOf(observation.SecondEventId);
            if (first < 0 || second <= first)
                errors.Add($"{observation.Database}: subscription observations were not equivalent to persisted history.");
        }
    }

    internal async Task VerifyPartitionRolloverAsync(List<string> errors,
        CancellationToken cancellationToken)
    {
        foreach (string database in Databases)
        {
            ListPartitionsResponse partitions = await Client.ListPartitionsAsync(database, cancellationToken);
            if (partitions.Partitions.Count < 2) errors.Add($"{database}: rollover produced fewer than two partitions.");
            foreach (PartitionStatus closed in partitions.Partitions.Where(x => !x.Active))
            {
                if (closed.StateFile is null || !closed.StateFile.Present || !closed.StateFile.JsonValid ||
                    !closed.StateFile.SchemaValid)
                    errors.Add($"{database}: closed partition {closed.PartitionNumber} has no valid state file.");
            }
            if (!_measurementStorageBaseline.TryGetValue(database, out var baseline))
            {
                errors.Add($"{database}: measurement storage baseline was not captured.");
                continue;
            }
            if (partitions.Partitions.Count <= baseline.Partitions)
                errors.Add($"{database}: measurement did not create a new partition.");
            if (partitions.Partitions.Count(x => x.StateFile?.Present == true) <= baseline.States)
                errors.Add($"{database}: measurement did not create a new state file.");
        }
    }

    private sealed class DuplicateCohort
    {
        public int Attempts;
        public int Committed;
        public int Reconciled;
    }

    private sealed record ReservationCheckoutPair(string Database, string Cart, string Order,
        CommerceSemanticOutcome Reserve, CommerceSemanticOutcome Checkout);
    private sealed record SubscriptionObservation(string Database, long Cursor, long FirstEventId, long SecondEventId);

    internal sealed record QueryFixture(
        EventQuery Inventory,
        IReadOnlyList<long> InventoryExpected,
        EventQuery Order,
        IReadOnlyList<long> OrderExpected,
        IReadOnlyList<long> ProductTypeExpected);

    private sealed class ClientConnection : IDisposable
    {
        private ClientConnection() { Channel = null!; Client = null!; }
        public ClientConnection(string address)
        {
            Channel = GrpcChannel.ForAddress(address);
            Client = new NativeDcbClient(
                new DatabaseService.DatabaseServiceClient(Channel),
                new CatalogService.CatalogServiceClient(Channel),
                new CommandService.CommandServiceClient(Channel),
                new EventService.EventServiceClient(Channel),
                new StatementService.StatementServiceClient(Channel),
                new AdministrationService.AdministrationServiceClient(Channel));
        }
        public static ClientConnection Empty { get; } = new();
        public GrpcChannel Channel { get; }
        public NativeDcbClient Client { get; }
        public void Dispose() { Client?.Dispose(); Channel?.Dispose(); }
    }
}

internal static class ListExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (int index = 0; index < values.Count; index++) if (values[index] == value) return index;
        return -1;
    }
}