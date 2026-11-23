using Microsoft.Data.Sqlite;
using StackExchange.Redis;

namespace VwEcuReader;

internal enum StorageBackend { SQLite, Memurai }

internal sealed class SampleStore(string databasePath, string memuraiStream = "vw-ecu-reader:readings") : IDisposable
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VwEcuReader", "readings.sqlite3");

    public string DatabasePath { get; } = Path.GetFullPath(databasePath);
    private string SettingsPath => Path.Combine(Path.GetDirectoryName(DatabasePath)!, "storage-backend.txt");
    public const string MemuraiAddress = "127.0.0.1:6379";
    public string MemuraiStream { get; } = memuraiStream;
    private static readonly string[] MetricFields = ["rpm", "coolant_c", "speed_kmh", "load_pct", "throttle_pct", "intake_c"];

    // Dashboard submits samples and reads Completion on its UI thread.
    // Each continuation writes one row on a worker thread, even if a prior write failed.
    public Task Completion { get; private set; } = Task.CompletedTask;
    private bool schemaReady;
    private ConnectionMultiplexer? memurai;

    public StorageBackend LoadBackend() =>
        File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Trim() == nameof(StorageBackend.Memurai)
            ? StorageBackend.Memurai
            : StorageBackend.SQLite;

    public void SaveBackend(StorageBackend backend)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, backend.ToString());
    }

    public Task CheckMemuraiAsync() => Enqueue(async () =>
    {
        IDatabase db = await GetMemuraiAsync();
        await db.PingAsync();
    });

    public Task AppendAsync(Sample sample, string portName, StorageBackend backend) => Enqueue(async () =>
    {
        if (backend == StorageBackend.Memurai)
            await AppendToMemuraiAsync(sample, portName);
        else
            Insert(sample, portName);
    });

    private Task Enqueue(Func<Task> action)
    {
        Completion = Completion.ContinueWith(
            _ => action(),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        return Completion;
    }

    private async Task<IDatabase> GetMemuraiAsync()
    {
        if (memurai is null)
        {
            var options = ConfigurationOptions.Parse(MemuraiAddress);
            options.ClientName = "vw-ecu-reader";
            options.ConnectRetry = 0;
            options.ConnectTimeout = 1500;
            options.AsyncTimeout = 1500;
            options.AbortOnConnectFail = true;
            options.BacklogPolicy = BacklogPolicy.FailFast;
            memurai = await ConnectionMultiplexer.ConnectAsync(options);
        }
        return memurai.GetDatabase();
    }

    private async Task AppendToMemuraiAsync(Sample sample, string portName)
    {
        IDatabase db = await GetMemuraiAsync();
        var fields = new List<NameValueEntry>(9)
        {
            new("captured_at_ms", sample.Time.ToUnixTimeMilliseconds()),
            new("port", portName),
        };
        for (int index = 0; index < MetricFields.Length; index++)
        {
            if (sample.Values[index] is double value)
                fields.Add(new(MetricFields[index], value));
        }
        if (sample.Error is not null)
            fields.Add(new("error", sample.Error));
        await db.StreamAddAsync(MemuraiStream, fields.ToArray());
    }

    public void Dispose() => memurai?.Dispose();

    private void Insert(Sample sample, string portName)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            DefaultTimeout = 2,
            Pooling = false,
        };
        using var connection = new SqliteConnection(connectionString.ToString());
        connection.Open();
        if (!schemaReady)
        {
            using var schema = connection.CreateCommand();
            schema.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS readings (
                    id INTEGER PRIMARY KEY,
                    captured_at_ms INTEGER NOT NULL,
                    port TEXT NOT NULL,
                    rpm REAL,
                    coolant_c REAL,
                    speed_kmh REAL,
                    load_pct REAL,
                    throttle_pct REAL,
                    intake_c REAL,
                    error TEXT
                );
                CREATE INDEX IF NOT EXISTS readings_by_time ON readings(captured_at_ms);
                """;
            schema.ExecuteNonQuery();
            schemaReady = true;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO readings (
                captured_at_ms, port, rpm, coolant_c, speed_kmh,
                load_pct, throttle_pct, intake_c, error
            )
            VALUES ($time, $port, $rpm, $coolant, $speed, $load, $throttle, $intake, $error);
            """;
        insert.Parameters.AddWithValue("$time", sample.Time.ToUnixTimeMilliseconds());
        insert.Parameters.AddWithValue("$port", portName);
        string[] metrics = ["$rpm", "$coolant", "$speed", "$load", "$throttle", "$intake"];
        for (int i = 0; i < metrics.Length; i++)
            insert.Parameters.Add(metrics[i], SqliteType.Real).Value = (object?)sample.Values[i] ?? DBNull.Value;
        insert.Parameters.Add("$error", SqliteType.Text).Value = (object?)sample.Error ?? DBNull.Value;
        insert.ExecuteNonQuery();
    }
}
