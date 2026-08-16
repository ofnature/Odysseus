using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Run;

/// <summary>One step execution, as it happened.</summary>
public sealed class StepRecord
{
    public DateTime UtcStart { get; set; }
    public double Seconds { get; set; }
    public ushort QuestId { get; set; }
    public string QuestName { get; set; } = string.Empty;
    public int Sequence { get; set; }
    public int StepIndex { get; set; }
    public string Kind { get; set; } = string.Empty;
    public uint? DataId { get; set; }
    /// <summary>Done · Failed · Skipped · Cancelled.</summary>
    public string Outcome { get; set; } = string.Empty;
    public string? Reason { get; set; }
    /// <summary>Executor phase at the end — where a failure was, not just that it was.</summary>
    public string? Phase { get; set; }

    public string Describe() => $"{QuestName} seq {Sequence} step {StepIndex + 1} {Kind}{(DataId is { } d ? $" {d}" : "")}";
}

/// <summary>Where the controller reports each step. Interface so tests can capture without a disk.</summary>
public interface IStepLog
{
    void Record(StepRecord record);
}

/// <summary>
/// The step-execution log: an in-memory ring for the window and an append-only JSONL file for
/// afterwards.
///
/// <para>
/// From the plan's milestone: <i>measure it, don't eyeball it</i>. A quest that stalls twice on
/// the same step is a broken path to fix in the editor, not a bug to debug — but only if the
/// stalls are a table. Every step lands here with its quest, sequence, kind, outcome, duration
/// and reason; <see cref="Failures"/> groups the failed ones by step so the repeat offenders float.
/// </para>
/// </summary>
public sealed class RunLog : IStepLog
{
    private const int RingSize = 1000;
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly string? _file;
    private readonly Action<string>? _log;
    private readonly LinkedList<StepRecord> _ring = new();

    /// <param name="file">JSONL path to append to; null keeps the log in memory only.</param>
    public RunLog(string? file, Action<string>? log = null)
    {
        _file = file;
        _log = log;
    }

    public IReadOnlyCollection<StepRecord> Recent => _ring;

    public int Count => _ring.Count;

    public void Record(StepRecord record)
    {
        _ring.AddFirst(record);
        while (_ring.Count > RingSize)
            _ring.RemoveLast();

        if (_file is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.AppendAllText(_file, JsonSerializer.Serialize(record, Json) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Run log write failed: {ex.Message}");
        }
    }

    /// <summary>Failed steps grouped by (quest, sequence, step), most repeated first.</summary>
    public IReadOnlyList<(StepRecord Example, int Count, string LastReason)> Failures()
        => _ring.Where(r => r.Outcome == "Failed")
            .GroupBy(r => (r.QuestId, r.Sequence, r.StepIndex))
            .Select(g => (g.First(), g.Count(), g.First().Reason ?? string.Empty))
            .OrderByDescending(x => x.Item2)
            .ThenByDescending(x => x.Item1.UtcStart)
            .ToList();

    /// <summary>Plain-text failure summary for the clipboard.</summary>
    public string FailuresText()
    {
        var lines = Failures().Select(f => $"{f.Count}x  {f.Example.Describe()}  — {f.LastReason}");
        return string.Join(Environment.NewLine, lines);
    }

    public void Clear() => _ring.Clear();
}
