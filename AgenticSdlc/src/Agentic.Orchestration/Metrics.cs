using System.Diagnostics;

namespace Agentic.Orchestration;

public sealed record MetricsReport(
    int Nodes, int NodesSucceeded, int Attempts, int AttemptSuccesses, double AttemptSuccessRate,
    int Retries, int Rollbacks, int Replans, int GateFailures, int PolicyViolations, int Approvals,
    double? MttrSeconds, double EndToEndSeconds, IReadOnlyDictionary<string, double> NodeSeconds);

/// <summary>Reliability metrics. MTTR = mean time from a node's first failed attempt to its next success.</summary>
public sealed class MetricsCollector
{
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _firstFailure = new();
    private readonly List<double> _recoveries = new();
    private readonly Dictionary<string, double> _nodeSeconds = new();
    private int _attempts, _successes, _retries, _rollbacks, _replans, _gateFailures, _violations, _approvals;
    private long _startTs, _endTs;

    public void Start() => _startTs = Stopwatch.GetTimestamp();

    public void Stop() => _endTs = Stopwatch.GetTimestamp();

    public void AttemptStarted() { lock (_lock) { _attempts++; } }

    public void AttemptSucceeded(string node, TimeSpan duration)
    {
        lock (_lock)
        {
            _successes++;
            _nodeSeconds[node] = (_nodeSeconds.TryGetValue(node, out var s) ? s : 0) + duration.TotalSeconds;
            if (_firstFailure.Remove(node, out var failedAt))
            {
                _recoveries.Add(Stopwatch.GetElapsedTime(failedAt).TotalSeconds);
            }
        }
    }

    public void AttemptFailed(string node)
    {
        lock (_lock)
        {
            _firstFailure.TryAdd(node, Stopwatch.GetTimestamp());
        }
    }

    public void Retry() { lock (_lock) { _retries++; } }
    public void Rollback() { lock (_lock) { _rollbacks++; } }
    public void Replan() { lock (_lock) { _replans++; } }
    public void GateFailure() { lock (_lock) { _gateFailures++; } }
    public void PolicyViolation() { lock (_lock) { _violations++; } }
    public void Approval() { lock (_lock) { _approvals++; } }

    public MetricsReport Report(int nodes, int nodesSucceeded)
    {
        lock (_lock)
        {
            var end = _endTs == 0 ? Stopwatch.GetTimestamp() : _endTs;
            return new MetricsReport(
                nodes, nodesSucceeded, _attempts, _successes,
                _attempts == 0 ? 0 : (double)_successes / _attempts,
                _retries, _rollbacks, _replans, _gateFailures, _violations, _approvals,
                _recoveries.Count == 0 ? null : _recoveries.Average(),
                Stopwatch.GetElapsedTime(_startTs, end).TotalSeconds,
                new Dictionary<string, double>(_nodeSeconds));
        }
    }
}
