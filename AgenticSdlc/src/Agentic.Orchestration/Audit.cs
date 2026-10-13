using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentic.Orchestration;

public sealed record AuditEvent(
    long Seq, DateTimeOffset At, string RunId, string Type, string? NodeId, string Message,
    IReadOnlyDictionary<string, string>? Data, string PrevHash, string Hash);

/// <summary>Append-only, hash-chained audit trail (each event commits to its predecessor). Optionally mirrored to JSONL.</summary>
public sealed class AuditLog
{
    private readonly object _lock = new();
    private readonly List<AuditEvent> _events = new();
    private readonly string? _path;
    private readonly TimeProvider _clock;
    private string _prev = Genesis;

    public AuditLog(string? jsonlPath = null, TimeProvider? clock = null)
    {
        _path = jsonlPath;
        _clock = clock ?? TimeProvider.System;
        if (_path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, string.Empty);
        }
    }

    public static string Genesis { get; } = new('0', 64);

    public IReadOnlyList<AuditEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToList();
            }
        }
    }

    public AuditEvent Append(string runId, string type, string? nodeId, string message, IReadOnlyDictionary<string, string>? data = null)
    {
        lock (_lock)
        {
            var seq = _events.Count + 1;
            var at = _clock.GetUtcNow();
            var hash = Compute(seq, at, runId, type, nodeId, message, data, _prev);
            var evt = new AuditEvent(seq, at, runId, type, nodeId, message, data, _prev, hash);
            _events.Add(evt);
            _prev = hash;
            if (_path is not null)
            {
                File.AppendAllText(_path, JsonSerializer.Serialize(evt) + Environment.NewLine);
            }

            return evt;
        }
    }

    public bool VerifyChain() => Verify(Events);

    public static bool Verify(IReadOnlyList<AuditEvent> events)
    {
        var prev = Genesis;
        foreach (var e in events)
        {
            if (e.PrevHash != prev || e.Hash != Compute(e.Seq, e.At, e.RunId, e.Type, e.NodeId, e.Message, e.Data, e.PrevHash))
            {
                return false;
            }

            prev = e.Hash;
        }

        return true;
    }

    private static string Compute(long seq, DateTimeOffset at, string runId, string type, string? nodeId, string message,
        IReadOnlyDictionary<string, string>? data, string prev)
    {
        var dataText = data is null ? "" : string.Join(";", data.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
        var canonical = $"{seq}|{at.UtcTicks}|{runId}|{type}|{nodeId}|{message}|{dataText}|{prev}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
