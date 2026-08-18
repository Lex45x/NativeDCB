using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Grpc.Core;
using Grpc.Net.Client;

using NativeDCB.Protocol.V1;
using NativeDCB.Sdk;
using NativeDCB.Sdk.Client;

namespace NativeDCB.EndToEndTests.Processes;

public sealed class CourseSubscriptionsProcessTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(seconds: 90);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(seconds: 30);

    [Fact]
    public async Task Course_subscriptions_run_against_a_real_server_process()
    {
        string databaseRoot = Path.Combine(
            Path.GetTempPath(),
            "NativeDCB.EndToEndTests",
            Guid.NewGuid().ToString("N"));
        string serverDirectory = Path.Combine(AppContext.BaseDirectory, "server");
        string serverAssembly = Path.Combine(serverDirectory, "NativeDCB.Server.dll");
        string sampleDirectory = Path.Combine(AppContext.BaseDirectory, "sample");
        string sampleAssembly = Path.Combine(sampleDirectory, "CourseSubscriptions.dll");
        Assert.True(File.Exists(serverAssembly), $"Server assembly was not copied to '{serverAssembly}'.");
        Assert.True(File.Exists(sampleAssembly), $"Sample assembly was not copied to '{sampleAssembly}'.");

        int[] ports = GetAvailablePorts(count: 3);
        string address = $"http://127.0.0.1:{ports[0]}";
        ConcurrentQueue<string> output = new();
        Process? server = null;
        using CancellationTokenSource timeout = new(TestTimeout);

        try
        {
            Directory.CreateDirectory(databaseRoot);
            server = StartServer(
                serverAssembly,
                serverDirectory,
                address,
                databaseRoot,
                ports[1],
                ports[2],
                output);

            using GrpcChannel channel = GrpcChannel.ForAddress(address);
            DatabaseService.DatabaseServiceClient databases = new(channel);
            CatalogService.CatalogServiceClient catalog = new(channel);
            CommandService.CommandServiceClient commands = new(channel);
            EventService.EventServiceClient events = new(channel);
            StatementService.StatementServiceClient statements = new(channel);
            AdministrationService.AdministrationServiceClient administration = new(channel);
            await WaitUntilReadyAsync(databases, server, output, timeout.Token);

            const string database = "school";
            await RunSeederAsync(sampleAssembly, sampleDirectory, address, database, timeout.Token);
            await RunSeederAsync(sampleAssembly, sampleDirectory, address, database, timeout.Token);

            using NativeDcbClient client = new(
                databases,
                catalog,
                commands,
                events,
                statements,
                administration);
            GetDatabaseInfoResponse info = await client.GetDatabaseInfoAsync(database, timeout.Token);
            Assert.Equal(expected: 0, info.Database.MainHead);
            Assert.Equal(
                [
                    ("command-schema", "DefineCourse"),
                    ("command-schema", "SubscribeStudentToCourse"),
                    ("event-schema", "CourseDefined"),
                    ("event-schema", "StudentSubscribedToCourse"),
                    ("handler", "DefineCourse"),
                    ("handler", "SubscribeStudentNdl"),
                    ("handler", "SubscribeStudentSdk")
                ],
                info.Database.CatalogFingerprints
                    .Select(item => (item.Kind, item.Name))
                    .OrderBy(item => item.Kind, StringComparer.Ordinal)
                    .ThenBy(item => item.Name, StringComparer.Ordinal));

            using AsyncServerStreamingCall<EventEnvelope> subscription = events.SubscribeEvents(
                new SubscribeEventsRequest { Database = database, AfterEventId = 0 },
                cancellationToken: timeout.Token);
            Task<IReadOnlyList<EventEnvelope>> publishedTask = ReadEventsAsync(
                subscription.ResponseStream, expectedCount: 3, timeout.Token);

            ExecuteHandlerResponse defined = await client.ExecuteHandlerAsync(
                database,
                "DefineCourse",
                new DefineCourse("native-dcb", Capacity: 30),
                cancellationToken: timeout.Token);
            ExecuteHandlerResponse ndlSubscription = await client.ExecuteHandlerAsync(
                database,
                "SubscribeStudentNdl",
                new SubscribeStudentToCourse("student-ndl", "native-dcb"),
                cancellationToken: timeout.Token);
            ExecuteHandlerResponse sdkSubscription = await client.ExecuteHandlerAsync(
                database,
                "SubscribeStudentSdk",
                new SubscribeStudentToCourse("student-sdk", "native-dcb"),
                cancellationToken: timeout.Token);

            AssertCommitted(defined, eventId: 1, "CourseDefined");
            AssertCommitted(ndlSubscription, eventId: 2, "StudentSubscribedToCourse");
            AssertCommitted(sdkSubscription, eventId: 3, "StudentSubscribedToCourse");

            IReadOnlyList<EventEnvelope> published = await publishedTask;
            IReadOnlyList<EventEnvelope> persisted = await ReadPersistedEventsAsync(
                events, database, throughEventId: 3, timeout.Token);
            Assert.Equal([1L, 2L, 3L], published.Select(@event => @event.EventId));
            Assert.Equal(published.Select(@event => @event.EventId), persisted.Select(@event => @event.EventId));
            Assert.Equal(
                ["CourseDefined", "StudentSubscribedToCourse", "StudentSubscribedToCourse"],
                published.Select(@event => @event.Type));
            Assert.Equal([defined.CommandId, ndlSubscription.CommandId, sdkSubscription.CommandId],
                published.Select(@event => @event.CommandId));
            Assert.All(published, @event => Assert.Equal(expected: 1U, @event.SchemaVersion));

            AssertPayload(published[index: 0], "native-dcb", capacity: 30);
            AssertPayload(published[index: 1], "student-ndl", "native-dcb");
            AssertPayload(published[index: 2], "student-sdk", "native-dcb");
            Assert.Equal([("course", "native-dcb")], Keys(published[index: 0]));
            Assert.Equal([("student", "student-ndl"), ("course", "native-dcb")], Keys(published[index: 1]));
            Assert.Equal([("student", "student-sdk"), ("course", "native-dcb")], Keys(published[index: 2]));
            Assert.Equal(expected: 3L, (await client.GetHeadAsync(database, timeout.Token)).EventId);
        }
        finally
        {
            await StopServerAsync(server);
            await DeleteDirectoryAsync(databaseRoot);
        }
    }

    private static Process StartServer(
        string serverAssembly,
        string workingDirectory,
        string address,
        string databaseRoot,
        int siloPort,
        int gatewayPort,
        ConcurrentQueue<string> output)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(serverAssembly);
        startInfo.Environment["ASPNETCORE_URLS"] = address;
        startInfo.Environment["DatabaseRoot"] = databaseRoot;
        startInfo.Environment["Orleans__SiloPort"] = siloPort.ToString();
        startInfo.Environment["Orleans__GatewayPort"] = gatewayPort.ToString();

        Process process = new() { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) => Capture(output, "stdout", args.Data);
        process.ErrorDataReceived += (_, args) => Capture(output, "stderr", args.Data);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("NativeDCB.Server did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task RunSeederAsync(
        string sampleAssembly,
        string workingDirectory,
        string address,
        string database,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(sampleAssembly);
        startInfo.ArgumentList.Add("seed");
        startInfo.ArgumentList.Add(address);
        startInfo.ArgumentList.Add(database);

        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("CourseSubscriptions seeder did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await standardOutput;
        string error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"CourseSubscriptions seed exited with code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        Assert.Contains($"Seeded database '{database}'", output, StringComparison.Ordinal);
    }

    private static async Task WaitUntilReadyAsync(
        DatabaseService.DatabaseServiceClient databases,
        Process server,
        ConcurrentQueue<string> output,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(StartupTimeout);
        try
        {
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (server.HasExited)
                {
                    await server.WaitForExitAsync(startup.Token);
                    throw new InvalidOperationException(
                        $"NativeDCB.Server exited during startup with code {server.ExitCode}.{Environment.NewLine}{FormatOutput(output)}");
                }

                try
                {
                    GetHealthResponse health = await databases.GetHealthAsync(
                        new GetHealthRequest(),
                        deadline: DateTime.UtcNow.AddSeconds(value: 1),
                        cancellationToken: startup.Token);
                    if (health.Live)
                    {
                        return;
                    }
                }
                catch (RpcException exception) when (exception.StatusCode is
                                                         StatusCode.Unavailable or
                                                         StatusCode.DeadlineExceeded or
                                                         StatusCode.Cancelled)
                {
                    // Kestrel or Orleans can still be completing startup.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds: 100), startup.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"NativeDCB.Server was not healthy within {StartupTimeout}.{Environment.NewLine}{FormatOutput(output)}");
        }
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadPersistedEventsAsync(
        EventService.EventServiceClient events,
        string database,
        long throughEventId,
        CancellationToken cancellationToken)
    {
        ReadEventsByRangeRequest request = new()
        {
            Database = database, AfterEventId = 0, ThroughEventId = throughEventId, Mode = ReadMode.Snapshot
        };
        using AsyncServerStreamingCall<EventEnvelope> call = events.ReadEventsByRange(
            request, cancellationToken: cancellationToken);
        List<EventEnvelope> result = new();
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            result.Add(call.ResponseStream.Current);
        }

        return result;
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadEventsAsync(
        IAsyncStreamReader<EventEnvelope> stream,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        List<EventEnvelope> result = [];
        while (result.Count < expectedCount && await stream.MoveNext(cancellationToken))
        {
            result.Add(stream.Current);
        }

        return result;
    }

    private static void AssertCommitted(ExecuteHandlerResponse response, long eventId, string eventType)
    {
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, response.OutcomeCase);
        Assert.True(Guid.TryParse(response.CommandId, out _));
        Assert.Equal(eventId, response.Committed.FirstEventId);
        Assert.Equal(eventId, response.Committed.LastEventId);
        Assert.Equal(eventType, Assert.Single(response.Committed.Events).Type);
    }

    private static void AssertPayload(EventEnvelope envelope, string courseId, int capacity)
    {
        using JsonDocument payload = JsonDocument.Parse(envelope.DataJson.Memory);
        Assert.Equal(courseId, payload.RootElement.GetProperty("CourseId").GetString());
        Assert.Equal(capacity, payload.RootElement.GetProperty("Capacity").GetInt32());
        Assert.Equal(expected: 2, payload.RootElement.EnumerateObject().Count());
    }

    private static void AssertPayload(EventEnvelope envelope, string studentId, string courseId)
    {
        using JsonDocument payload = JsonDocument.Parse(envelope.DataJson.Memory);
        Assert.Equal(studentId, payload.RootElement.GetProperty("StudentId").GetString());
        Assert.Equal(courseId, payload.RootElement.GetProperty("CourseId").GetString());
        Assert.Equal(expected: 2, payload.RootElement.EnumerateObject().Count());
    }

    private static IEnumerable<(string Key, string Value)> Keys(EventEnvelope envelope)
    {
        return envelope.Keys.Select(key => (key.Key, key.Value));
    }

    private static int[] GetAvailablePorts(int count)
    {
        HashSet<int> ports = [];
        while (ports.Count < count)
        {
            TcpListener listener = new(IPAddress.Loopback, port: 0);
            try
            {
                listener.Start();
                ports.Add(((IPEndPoint)listener.LocalEndpoint).Port);
            }
            finally
            {
                listener.Stop();
            }
        }

        return ports.ToArray();
    }

    private static async Task StopServerAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(seconds: 10));
            await process.WaitForExitAsync(stopTimeout.Token);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                attempt < 5 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private static void Capture(ConcurrentQueue<string> output, string stream, string? line)
    {
        if (line is not null)
        {
            output.Enqueue($"[{stream}] {line}");
        }
    }

    private static string FormatOutput(ConcurrentQueue<string> output)
    {
        return output.IsEmpty ? "No server output was captured." : string.Join(Environment.NewLine, output);
    }

    private sealed record SubscribeStudentToCourse(
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Serialized as the command payload.
        string StudentId,
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Serialized as the command payload.
        string CourseId);

    private sealed record DefineCourse(
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Serialized as the command payload.
        string CourseId,
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Serialized as the command payload.
        int Capacity);
}
