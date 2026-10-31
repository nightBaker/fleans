// Sample Fleans external-task worker over Kafka (#979).
//
// Reads task records from `fleans.tasks.<TASK_TYPE>` in its own consumer group, "does the work"
// (waits `delayMs` from the task's variables, else WORK_MS), and completes the task with
// POST /Execution/complete-activity — passing activityInstanceId, so a record delivered twice
// completes the task once. Up to CONCURRENCY tasks run at a time.
//
// Settings (environment):
//   KAFKA_BROKERS   bootstrap servers                 (default localhost:9092)
//   FLEANS_URL      Fleans REST base URL              (default http://localhost:8080)
//   TASK_TYPE       service-task type to work on      (required)
//   GROUP_ID        Kafka consumer group              (default fleans-worker-<TASK_TYPE>)
//   CONCURRENCY     tasks in flight                   (default 64)
//   WORK_MS         work time when the task has no delayMs variable (default 0)
//   FAIL_WITH       when set, fail every task with this BPMN error code instead of completing it
//
// The worker owns delivery: Kafka keeps the record until its offset is committed, so a worker that
// dies mid-task leaves the record to the next member of the group. An offset is committed only once
// every record before it in its partition has been reported (OffsetTracker), and a report that may
// pass later — 429, 5xx, a dropped connection — is retried until it does. A 4xx is logged and skipped:
// sending it again cannot help (e.g. the instance was deleted).

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;

var brokers = Env("KAFKA_BROKERS", "localhost:9092");
var fleansUrl = Env("FLEANS_URL", "http://localhost:8080");
var taskType = Environment.GetEnvironmentVariable("TASK_TYPE")
    ?? throw new InvalidOperationException("TASK_TYPE is required");
var groupId = Env("GROUP_ID", $"fleans-worker-{taskType}");
var concurrency = int.Parse(Env("CONCURRENCY", "64"));
var workMs = int.Parse(Env("WORK_MS", "0"));
var failWith = Environment.GetEnvironmentVariable("FAIL_WITH");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = concurrency })
{
    BaseAddress = new Uri(fleansUrl),
};

var offsets = new OffsetTracker();
using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
{
    BootstrapServers = brokers,
    GroupId = groupId,
    AutoOffsetReset = AutoOffsetReset.Earliest,
    EnableAutoCommit = true,
    // Store an offset only after its task completed; auto-commit then commits what was stored.
    EnableAutoOffsetStore = false,
})
    // A revoked partition's records in flight are redelivered to its next owner; forget them here.
    .SetPartitionsRevokedHandler((_, revoked) => offsets.Forget(revoked.Select(p => p.TopicPartition)))
    .Build();

var topic = $"fleans.tasks.{taskType}";
consumer.Subscribe(topic);
Console.WriteLine($"Working on {topic} as {groupId} → {fleansUrl} (concurrency {concurrency})");

using var slots = new SemaphoreSlim(concurrency);
long completed = 0;

try
{
    while (!cts.IsCancellationRequested)
    {
        var result = consumer.Consume(cts.Token);
        await slots.WaitAsync(cts.Token);
        offsets.Started(result.TopicPartitionOffset);
        _ = Task.Run(async () =>
        {
            try
            {
                await Handle(result.Message.Value, cts.Token);
                if (Interlocked.Increment(ref completed) % 1000 == 0)
                    Console.WriteLine($"{DateTime.UtcNow:O} {completed} tasks done");
            }
            catch (HttpRequestException ex) when (!cts.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Task at {result.TopicPartitionOffset} rejected, skipped: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                return; // shutting down: not reported, so not committed — the group redelivers it
            }
            finally
            {
                slots.Release();
            }

            if (offsets.Finished(result.TopicPartitionOffset) is { } next)
            {
                try { consumer.StoreOffset(next); }
                catch (KafkaException) { } // revoked meanwhile; its next owner reads from the last commit
            }
        });
    }
}
catch (OperationCanceledException)
{
}
finally
{
    consumer.Close();
}

async Task Handle(string value, CancellationToken ct)
{
    var task = JsonNode.Parse(value)!;
    var variables = task["variables"] as JsonObject;
    var delay = variables?["delayMs"] is JsonValue v && v.TryGetValue<int>(out var ms) ? ms : workMs;
    if (delay > 0)
        await Task.Delay(delay, ct);

    var (path, body) = failWith is null
        ? ("Execution/complete-activity", (object)new
        {
            WorkflowInstanceId = task["workflowInstanceId"]!.GetValue<Guid>(),
            ActivityId = task["activityId"]!.GetValue<string>(),
            ActivityInstanceId = task["activityInstanceId"]!.GetValue<Guid>(),
            Variables = new Dictionary<string, object> { ["workedBy"] = groupId },
        })
        : ("Execution/fail-activity", new
        {
            WorkflowInstanceId = task["workflowInstanceId"]!.GetValue<Guid>(),
            ActivityId = task["activityId"]!.GetValue<string>(),
            ActivityInstanceId = task["activityInstanceId"]!.GetValue<Guid>(),
            ErrorCode = failWith,
            ErrorMessage = $"{groupId} failed the task",
        });

    // Safe to repeat: the engine ignores a second report for the same activity instance. The work
    // is done, so a report that may pass later is retried until it does: 429 (admission control
    // sheds load), 5xx, a dropped connection.
    for (var attempt = 1; ; attempt++)
    {
        TimeSpan wait;
        try
        {
            using var response = await http.PostAsJsonAsync(path, body, ct);
            var status = (int)response.StatusCode;
            if (status != 429 && status < 500)
            {
                response.EnsureSuccessStatusCode(); // a 4xx surfaces as HttpRequestException
                return;
            }
            wait = response.Headers.RetryAfter?.Delta ?? Backoff(attempt);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            wait = Backoff(attempt); // no response at all
        }
        await Task.Delay(wait, ct);
    }
}

static TimeSpan Backoff(int attempt) =>
    TimeSpan.FromMilliseconds(Math.Min(5000, 100 * (1 << Math.Min(attempt, 6))) * (0.5 + Random.Shared.NextDouble()));

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

/// <summary>
/// Which offset of each partition is safe to commit while tasks finish out of order: the lowest
/// offset still in flight, or one past the highest finished when none is.
/// </summary>
sealed class OffsetTracker
{
    private readonly Dictionary<TopicPartition, (SortedSet<long> InFlight, long Done)> _partitions = new();

    public void Started(TopicPartitionOffset record)
    {
        lock (_partitions)
        {
            if (!_partitions.TryGetValue(record.TopicPartition, out var p))
                _partitions[record.TopicPartition] = p = (new SortedSet<long>(), -1);
            p.InFlight.Add(record.Offset.Value);
        }
    }

    /// <returns>The offset to store, or <c>null</c> when the partition was revoked meanwhile.</returns>
    public TopicPartitionOffset? Finished(TopicPartitionOffset record)
    {
        lock (_partitions)
        {
            if (!_partitions.TryGetValue(record.TopicPartition, out var p) || !p.InFlight.Remove(record.Offset.Value))
                return null;
            p.Done = Math.Max(p.Done, record.Offset.Value);
            _partitions[record.TopicPartition] = p;
            var next = p.InFlight.Count > 0 ? p.InFlight.Min : p.Done + 1;
            return new TopicPartitionOffset(record.TopicPartition, next);
        }
    }

    public void Forget(IEnumerable<TopicPartition> revoked)
    {
        lock (_partitions)
            foreach (var partition in revoked)
                _partitions.Remove(partition);
    }
}
