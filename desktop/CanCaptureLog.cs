using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VwEcuReader;

internal sealed class CanCaptureLog
{
    public const string FileName = "can-frames.sqlite3";
    public const int PreviewSize = 20;
    private const int RecordSize = 21;
    private const int MaxBatchSize = 48;
    private readonly BlockingCollection<(string Line, int Offset)> packets = new(512);
    private readonly Queue<CanFrame> preview = new(PreviewSize);
    private readonly List<CanFrame> pending = new(1024);
    private CanCaptureEnd? end;
    private CanCaptureProgress progress = new(0, 0, 0, 0, 0, [], null);
    private volatile bool ready;

    public CanCaptureLog(string path, string portName, string? firmware)
    {
        Completion = Task.Factory.StartNew(() => Record(path, portName, firmware),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public bool Ready => ready;
    public Task Completion { get; }
    public CanCaptureProgress Progress => Volatile.Read(ref progress);

    public bool Enqueue(string line, int offset) => packets.TryAdd((line, offset));

    public void Finish(CanCaptureEnd result)
    {
        end = result;
        packets.CompleteAdding();
    }

    private void Record(string path, string portName, string? firmware)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Pooling = false, DefaultTimeout = 2,
            }.ToString());
            connection.Open();

            using var session = connection.CreateCommand();
            session.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = FULL;
                CREATE TABLE IF NOT EXISTS can_capture_sessions (
                    id INTEGER PRIMARY KEY,
                    started_at_ms INTEGER NOT NULL,
                    stopped_at_ms INTEGER,
                    port TEXT NOT NULL,
                    firmware TEXT,
                    result TEXT,
                    board_frames INTEGER,
                    received_frames INTEGER,
                    saved_frames INTEGER,
                    missing_frames INTEGER,
                    invalid_packets INTEGER,
                    rx_missed INTEGER,
                    rx_overrun INTEGER,
                    bus_errors INTEGER
                );
                CREATE TABLE IF NOT EXISTS can_frames (
                    session_id INTEGER NOT NULL REFERENCES can_capture_sessions(id),
                    sequence INTEGER NOT NULL,
                    board_time_us INTEGER NOT NULL,
                    can_id INTEGER NOT NULL,
                    is_extended INTEGER NOT NULL,
                    is_rtr INTEGER NOT NULL,
                    dlc INTEGER NOT NULL,
                    data BLOB NOT NULL,
                    PRIMARY KEY (session_id, sequence)
                ) WITHOUT ROWID;
                """;
            session.ExecuteNonQuery();
            session.CommandText = """
                INSERT INTO can_capture_sessions(started_at_ms, port, firmware)
                VALUES ($time, $port, $firmware) RETURNING id;
                """;
            session.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            session.Parameters.AddWithValue("$port", portName);
            session.Parameters.AddWithValue("$firmware", (object?)firmware ?? DBNull.Value);
            long id = (long)session.ExecuteScalar()!;
            using var insert = PrepareInsert(connection, id);
            ready = true;

            long lastCommit = Environment.TickCount64;
            while (true)
            {
                if (packets.TryTake(out var packet, 50))
                    ParsePacket(packet.Line.AsSpan(packet.Offset));

                bool done = packets.IsCompleted;
                long now = Environment.TickCount64;
                if (pending.Count > 0 && (pending.Count >= 1024 || done || now - lastCommit >= 200))
                {
                    Commit(insert);
                    lastCommit = now;
                }
                if (done)
                    break;
            }

            using var finish = connection.CreateCommand();
            CanCaptureEnd result = end!;
            finish.CommandText = """
                UPDATE can_capture_sessions SET stopped_at_ms=$time, result=$result, board_frames=$board,
                    received_frames=$received, saved_frames=$saved, missing_frames=$missing, invalid_packets=$invalid,
                    rx_missed=$missed, rx_overrun=$overrun, bus_errors=$errors
                WHERE id=$id;
                """;
            finish.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            finish.Parameters.AddWithValue("$result", result.Reason != "STOP" || progress.Complete(result)
                ? result.Reason : "INCOMPLETE");
            finish.Parameters.AddWithValue("$board", (object?)result.Total ?? DBNull.Value);
            finish.Parameters.AddWithValue("$received", progress.Received);
            finish.Parameters.AddWithValue("$saved", progress.Saved);
            finish.Parameters.AddWithValue("$missing", progress.MissingAtEnd(result));
            finish.Parameters.AddWithValue("$invalid", progress.Invalid);
            finish.Parameters.AddWithValue("$missed", (object?)result.Stats?.Missed ?? DBNull.Value);
            finish.Parameters.AddWithValue("$overrun", (object?)result.Stats?.Overrun ?? DBNull.Value);
            finish.Parameters.AddWithValue("$errors", (object?)result.Stats?.BusErrors ?? DBNull.Value);
            finish.Parameters.AddWithValue("$id", id);
            finish.ExecuteNonQuery();
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref progress, progress with { Error = error.Message });
        }
    }

    private void ParsePacket(ReadOnlySpan<char> payload)
    {
        Span<byte> bytes = stackalloc byte[RecordSize * MaxBatchSize];
        int separator = payload.IndexOf(' ');
        if (separator < 1 ||
            !long.TryParse(payload[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out long first) ||
            first <= 0 || first > long.MaxValue - MaxBatchSize ||
            !Convert.TryFromBase64Chars(payload[(separator + 1)..], bytes, out int size) ||
            size == 0 || size % RecordSize != 0)
        {
            Volatile.Write(ref progress, progress with { Invalid = progress.Invalid + 1 });
            return;
        }

        long received = progress.Received;
        long last = progress.LastSequence;
        long missing = progress.Missing;
        bool invalid = false;
        for (int offset = 0; offset < size; offset += RecordSize)
        {
            long sequence = first + offset / RecordSize;
            var record = bytes.Slice(offset, RecordSize);
            long timeUs = BinaryPrimitives.ReadInt64LittleEndian(record);
            uint idAndFlags = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            int id = (int)(idAndFlags & 0x1fffffff);
            bool extended = (idAndFlags & 0x80000000) != 0;
            bool remote = (idAndFlags & 0x40000000) != 0;
            byte dlc = record[12];
            if (timeUs < 0 || (idAndFlags & 0x20000000) != 0 || (!extended && id > 0x7ff) || dlc > 15)
            {
                invalid = true;
                continue;
            }
            if (sequence <= last)
                continue;

            int length = remote ? 0 : Math.Min((int)dlc, 8);
            var frame = new CanFrame(sequence, timeUs, id, extended, remote, dlc, record.Slice(13, length).ToArray());
            if (preview.Count == PreviewSize)
                preview.Dequeue();
            preview.Enqueue(frame);
            pending.Add(frame);
            missing += sequence - last - 1;
            last = sequence;
            received++;
        }
        Volatile.Write(ref progress, progress with
        {
            Received = received, LastSequence = last, Missing = missing,
            Invalid = progress.Invalid + (invalid ? 1 : 0), Preview = preview.ToArray(),
        });
    }

    private static SqliteCommand PrepareInsert(SqliteConnection connection, long id)
    {
        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO can_frames (session_id, sequence, board_time_us, can_id, is_extended, is_rtr, dlc, data)
            VALUES ($session, $seq, $time, $id, $extended, $remote, $dlc, $data);
            """;
        insert.Parameters.AddWithValue("$session", id);
        foreach (string name in new[] { "$seq", "$time", "$id", "$extended", "$remote", "$dlc" })
            insert.Parameters.Add(name, SqliteType.Integer);
        insert.Parameters.Add("$data", SqliteType.Blob);
        insert.Prepare();
        return insert;
    }

    private void Commit(SqliteCommand insert)
    {
        using var transaction = insert.Connection!.BeginTransaction();
        insert.Transaction = transaction;
        foreach (CanFrame frame in pending)
        {
            insert.Parameters[1].Value = frame.Sequence;
            insert.Parameters[2].Value = frame.TimeUs;
            insert.Parameters[3].Value = frame.Id;
            insert.Parameters[4].Value = frame.Extended;
            insert.Parameters[5].Value = frame.Remote;
            insert.Parameters[6].Value = frame.Dlc;
            insert.Parameters[7].Value = frame.Data;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        Volatile.Write(ref progress, progress with { Saved = progress.Saved + pending.Count });
        pending.Clear();
    }
}
