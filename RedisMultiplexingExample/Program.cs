using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using StackExchange.Redis;

// This benchmark compares Redis performance using a singleton multiplexer vs multiple instances vs a simple pool.
// It stresses Redis with parallel SET/GET operations and now tracks errors/timeouts for each scenario.

public class RedisMultiplexingBenchmarks
{
    private const string RedisConnection = "localhost:6379";
    private const int Parallelism = 100; // Number of parallel tasks per operation
    private const int OperationsPerTask = 1000; // Number of operations per task
    private static readonly string TestKey = "bench:key";
    private static readonly string TestValue = new string('x', 100);

    // Singleton multiplexer
    private static readonly ConnectionMultiplexer SingletonMux = ConnectionMultiplexer.Connect(RedisConnection);

    // Pool of multiplexers (optional, size 8)
    private static readonly ConnectionMultiplexer[] Pool = new ConnectionMultiplexer[8];
    private static int poolIndex = 0;

    // Error tracking
    public static ConcurrentDictionary<string, (int errorCount, string firstError)> ErrorStats = new();

    static RedisMultiplexingBenchmarks()
    {
        for (int i = 0; i < Pool.Length; i++)
            Pool[i] = ConnectionMultiplexer.Connect(RedisConnection);
    }

    [Benchmark(Description = "Singleton Multiplexer")]
    public async Task SingletonMultiplexerAsync()
    {
        var db = SingletonMux.GetDatabase();
        await RunParallelAsync(() => db.StringSetAsync(TestKey, TestValue), "Singleton Multiplexer");
        await RunParallelAsync(() => db.StringGetAsync(TestKey), "Singleton Multiplexer");
    }

    [Benchmark(Description = "Multiple Multiplexers")]
    public async Task MultipleMultiplexersAsync()
    {
        await RunParallelAsync(async () =>
        {
            using var mux = await ConnectionMultiplexer.ConnectAsync(RedisConnection);
            var db = mux.GetDatabase();
            await db.StringSetAsync(TestKey, TestValue);
            await db.StringGetAsync(TestKey);
        }, "Multiple Multiplexers");
    }

    [Benchmark(Description = "Pooled Multiplexer (8)")]
    public async Task PooledMultiplexerAsync()
    {
        await RunParallelAsync(() =>
        {
            var mux = Pool[System.Threading.Interlocked.Increment(ref poolIndex) % Pool.Length];
            var db = mux.GetDatabase();
            return db.StringSetAsync(TestKey, TestValue).ContinueWith(_ => db.StringGetAsync(TestKey));
        }, "Pooled Multiplexer (8)");
    }

    // Helper to run many parallel tasks and track errors
    private static async Task RunParallelAsync(Func<Task> op, string scenario)
    {
        var errorCount = 0;
        string firstError = null;
        var tasks = new Task[Parallelism];
        for (int i = 0; i < Parallelism; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                for (int j = 0; j < OperationsPerTask; j++)
                {
                    try
                    {
                        await op();
                    }
                    catch (Exception ex)
                    {
                        System.Threading.Interlocked.Increment(ref errorCount);
                        if (firstError == null)
                            firstError = ex.Message;
                    }
                }
            });
        }
        await Task.WhenAll(tasks);
        ErrorStats[scenario] = (errorCount, firstError);
    }

    // Connectivity check for diagnostics
    public static bool CheckRedisConnection(out string error)
    {
        try
        {
            using var mux = ConnectionMultiplexer.Connect(RedisConnection);
            var db = mux.GetDatabase();
            db.StringSet("diagnostic:test", "ok");
            var val = db.StringGet("diagnostic:test");
            if (val == "ok")
            {
                error = string.Empty;
                return true;
            }
            error = "Unexpected value from Redis.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

// Entry point: runs diagnostics, benchmarks, and explains results
class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Redis Multiplexing Benchmark\n==============================\n");
        Console.WriteLine("Checking connectivity to Redis at localhost:6379...");
        if (!RedisMultiplexingBenchmarks.CheckRedisConnection(out var error))
        {
            Console.WriteLine("\n[ERROR] Could not connect to Redis: " + error);
            Console.WriteLine("\nTroubleshooting steps:");
            Console.WriteLine("  1. Make sure Docker is running and the Redis container is started.");
            Console.WriteLine("  2. Check that you ran: docker run -d --name redis-stack -p 6379:6379 -p 8001:8001 redis/redis-stack:latest");
            Console.WriteLine("  3. Ensure your firewall allows connections to port 6379.");
            Console.WriteLine("  4. Try connecting to Redis using redis-cli or another tool to verify it's up.");
            Console.WriteLine("  5. If running on WSL or a VM, check network bridging and localhost mapping.");
            Console.WriteLine("\nFix the above issues and re-run this app. Exiting.");
            return;
        }
        Console.WriteLine("Connection to Redis successful!\n");

        PrintExplanation();

        var summary = BenchmarkRunner.Run<RedisMultiplexingBenchmarks>();
        Console.WriteLine("\nBenchmark complete. Review the table above for performance comparison.\n");
        PrintResultInterpretation();
        PrintErrorSummary();
        Console.WriteLine("\nIf you see timeouts, try increasing Docker's CPU/memory limits or lowering Parallelism/OperationsPerTask.");
    }

    // Explains what the benchmark is doing and why
    static void PrintExplanation()
    {
        Console.WriteLine("This benchmark compares three ways of connecting to Redis from .NET:\n");
        Console.WriteLine("  1. Singleton Multiplexer: All threads share a single ConnectionMultiplexer instance (recommended by StackExchange.Redis).\n" +
                          "  2. Multiple Multiplexers: Each thread creates its own ConnectionMultiplexer (not recommended, can cause resource exhaustion).\n" +
                          "  3. Pooled Multiplexer: Threads share a small pool of multiplexers (sometimes used for very high concurrency).\n");
        Console.WriteLine("Each test runs many parallel SET/GET operations to stress Redis and measure throughput and latency.\n");
    }

    // Explains how to interpret the results for new users
    static void PrintResultInterpretation()
    {
        Console.WriteLine("How to interpret the results:\n");
        Console.WriteLine("- Lower mean/median time means better performance.\n" +
                          "- Singleton Multiplexer should perform best under most workloads, as it reuses connections efficiently.\n" +
                          "- Multiple Multiplexers may show worse performance and more timeouts, as creating many connections is expensive for Redis.\n" +
                          "- Pooled Multiplexer can help in rare cases, but usually singleton is best.\n");
        Console.WriteLine("If you see timeouts or errors, your Redis server may be overloaded. Try increasing Docker's CPU/memory limits or reducing the number of parallel tasks.\n");
    }

    // Prints error summary for each scenario
    static void PrintErrorSummary()
    {
        Console.WriteLine("Error summary per scenario:\n");
        foreach (var kvp in RedisMultiplexingBenchmarks.ErrorStats)
        {
            Console.WriteLine($"  {kvp.Key}: {kvp.Value.errorCount} errors");
            if (kvp.Value.errorCount > 0)
                Console.WriteLine($"    First error: {kvp.Value.firstError}");
        }
    }
}
