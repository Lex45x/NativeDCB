using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

using NativeDCB.Engine.Storage.Audit;

namespace NativeDCB.Engine.Tests.Storage.Audit;

public sealed class AuditJournalTests
{
    [Fact]
    public async Task Journal_appends_rolls_over_filters_and_reopens()
    {
        string root = NewRoot();
        try
        {
            await using (AuditJournal journal = new(root, maxRecordCountPerPartition: 2, TimeProvider.System))
            {
                await journal.InitializeAsync(CancellationToken.None);
                await journal.AppendAsync(Draft("attempt", database: "one"), CancellationToken.None);
                await journal.AppendAsync(Draft("outcome", database: "one", outcome: "committed"),
                    CancellationToken.None);
                await journal.AppendAsync(Draft("denied", database: "two", outcome: "permission_denied"),
                    CancellationToken.None);

                AuditPage first = await journal.QueryAsync(
                    new AuditQuery(AfterSequence: 0, Limit: 1, Database: "one"), CancellationToken.None);
                Assert.Single(first.Records);
                Assert.True(first.HasMore);

                AuditPage second = await journal.QueryAsync(
                    new AuditQuery(first.NextAfterSequence, Limit: 10, Database: "one"), CancellationToken.None);
                Assert.Single(second.Records);
                Assert.Equal("committed", second.Records[0].Outcome);
                Assert.False(second.HasMore);
                Assert.Equal(expected: 3, second.BoundarySequence);
            }

            Assert.Equal(expected: 2, Directory.GetFiles(
                Path.Combine(root, ".audit"), "audit_partition_*_v1.ndjson").Length);

            await using AuditJournal reopened = new(root, maxRecordCountPerPartition: 2, TimeProvider.System);
            await reopened.InitializeAsync(CancellationToken.None);
            Assert.Equal(expected: 3, reopened.GetStatus().LastSequence);
            AuditRecord appended = await reopened.AppendAsync(Draft("outcome"), CancellationToken.None);
            Assert.Equal(expected: 4, appended.Sequence);
            Assert.NotEmpty(appended.PreviousHash);
            Assert.Equal(expected: 64, appended.RecordHash.Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recovery_truncates_only_an_incomplete_active_suffix()
    {
        string root = NewRoot();
        try
        {
            await using (AuditJournal journal = new(root, maxRecordCountPerPartition: 10, TimeProvider.System))
            {
                await journal.InitializeAsync(CancellationToken.None);
                await journal.AppendAsync(Draft("attempt"), CancellationToken.None);
            }

            string partition = Assert.Single(Directory.GetFiles(
                Path.Combine(root, ".audit"), "audit_partition_*_v1.ndjson"));
            await File.AppendAllTextAsync(partition, "{\"incomplete\"", Encoding.UTF8);

            await using AuditJournal recovered = new(root, maxRecordCountPerPartition: 10, TimeProvider.System);
            await recovered.InitializeAsync(CancellationToken.None);
            Assert.Equal(expected: 1, recovered.GetStatus().LastSequence);
            Assert.EndsWith("\n", await File.ReadAllTextAsync(partition), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Recovery_rejects_a_modified_committed_record()
    {
        string root = NewRoot();
        try
        {
            await using (AuditJournal journal = new(root, maxRecordCountPerPartition: 10, TimeProvider.System))
            {
                await journal.InitializeAsync(CancellationToken.None);
                await journal.AppendAsync(Draft("attempt", database: "school"), CancellationToken.None);
            }

            string partition = Assert.Single(Directory.GetFiles(
                Path.Combine(root, ".audit"), "audit_partition_*_v1.ndjson"));
            string content = await File.ReadAllTextAsync(partition);
            await File.WriteAllTextAsync(
                partition, content.Replace("school", "other", StringComparison.Ordinal), Encoding.UTF8);

            await using AuditJournal corrupted = new(root, maxRecordCountPerPartition: 10, TimeProvider.System);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                corrupted.InitializeAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown-member")]
    [InlineData("blank-line")]
    public async Task Recovery_rejects_noncanonical_committed_content(string corruption)
    {
        string root = NewRoot();
        try
        {
            await using (AuditJournal journal = new(root, maxRecordCountPerPartition: 10, TimeProvider.System))
            {
                await journal.InitializeAsync(CancellationToken.None);
                await journal.AppendAsync(Draft("attempt"), CancellationToken.None);
            }

            string partition = Assert.Single(Directory.GetFiles(
                Path.Combine(root, ".audit"), "audit_partition_*_v1.ndjson"));
            string content = await File.ReadAllTextAsync(partition);
            content = corruption == "blank-line"
                ? content.Replace("\n", "\n\n", StringComparison.Ordinal)
                : content.Insert(content.LastIndexOf('}'), ",\"unexpected\":true");
            await File.WriteAllTextAsync(partition, content, Encoding.UTF8);

            await using AuditJournal corrupted = new(root, maxRecordCountPerPartition: 10, TimeProvider.System);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                corrupted.InitializeAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Journal_lock_excludes_a_second_writer()
    {
        string root = NewRoot();
        try
        {
            await using AuditJournal first = new(root, maxRecordCountPerPartition: 10, TimeProvider.System);
            await first.InitializeAsync(CancellationToken.None);
            await using AuditJournal second = new(root, maxRecordCountPerPartition: 10, TimeProvider.System);
            await Assert.ThrowsAsync<IOException>(() => second.InitializeAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Sparse_query_advances_to_its_boundary_and_cancellation_does_not_fault_the_journal()
    {
        string root = NewRoot();
        try
        {
            await using AuditJournal journal = new(root, maxRecordCountPerPartition: 10, TimeProvider.System);
            await journal.InitializeAsync(CancellationToken.None);
            await journal.AppendAsync(Draft("attempt", database: "one"), CancellationToken.None);
            await journal.AppendAsync(Draft("outcome", database: "two"), CancellationToken.None);

            AuditPage empty = await journal.QueryAsync(
                new AuditQuery(AfterSequence: 0, Limit: 1, Database: "missing"), CancellationToken.None);
            Assert.Empty(empty.Records);
            Assert.Equal(empty.BoundarySequence, empty.NextAfterSequence);
            Assert.False(empty.HasMore);

            AuditPage beyondBoundary = await journal.QueryAsync(
                new AuditQuery(AfterSequence: 100, Limit: 1), CancellationToken.None);
            Assert.Empty(beyondBoundary.Records);
            Assert.Equal(expected: 100, beyondBoundary.NextAfterSequence);
            Assert.False(beyondBoundary.HasMore);

            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.QueryAsync(
                new AuditQuery(AfterSequence: 0, Limit: 10), cancelled.Token));
            Assert.True(journal.GetStatus().Ready);

            AuditRecord appended = await journal.AppendAsync(Draft("outcome"), CancellationToken.None);
            Assert.Equal(expected: 3, appended.Sequence);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Journal_emits_bounded_activity_and_metric_dimensions()
    {
        string root = NewRoot();
        List<Activity> stopped = new();
        using ActivityListener activities = new()
        {
            ShouldListenTo = source => source.Name == "NativeDCB.Engine",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(activities);

        List<string> measurements = new();
        using MeterListener metrics = new();
        metrics.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "NativeDCB.Engine")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        metrics.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add($"{instrument.Name}:{string.Join(',', tags.ToArray().Select(tag => tag.Key))}"));
        metrics.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add($"{instrument.Name}:{string.Join(',', tags.ToArray().Select(tag => tag.Key))}"));
        metrics.Start();

        try
        {
            await using AuditJournal journal = new(root, maxRecordCountPerPartition: 10, TimeProvider.System);
            await journal.InitializeAsync(CancellationToken.None);
            await journal.AppendAsync(Draft("attempt", database: "sensitive-database"), CancellationToken.None);

            Activity activity = Assert.Single(stopped, value => value.OperationName == "audit.append");
            Assert.DoesNotContain(activity.TagObjects, tag => tag.Key is "subject" or "command_id");
            Assert.Contains(measurements, value => value.StartsWith("nativedcb.audit.records:", StringComparison.Ordinal));
            Assert.Contains(measurements,
                value => value.StartsWith("nativedcb.audit.append.duration:", StringComparison.Ordinal));
            Assert.DoesNotContain(measurements, value => value.Contains("database", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AuditRecordDraft Draft(string phase, string? database = null, string? outcome = null)
    {
        return new AuditRecordDraft(
            Guid.NewGuid(),
            phase,
            phase == "denied" ? "authorization" : "decision",
            "/nativedcb.v1.CommandService/ExecuteHandler",
            AuthenticationScheme: "ApiKey",
            Subject: "key-id",
            Database: database,
            Outcome: outcome);
    }

    private static string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "NativeDCB.AuditTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}