using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class RunLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static StepRecord Rec(string outcome, int step = 0, string? reason = null) => new()
    {
        UtcStart = new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc), Seconds = 1.5, QuestId = 1622, QuestName = "Mogwin's Trial",
        Sequence = 1, StepIndex = step, Kind = "Interact", DataId = 5, Outcome = outcome, Reason = reason,
    };

    [Fact]
    public void Failures_group_by_step_and_sort_repeat_offenders_first()
    {
        var log = new RunLog(null);
        log.Record(Rec("Failed", step: 0, reason: "no path"));
        log.Record(Rec("Done", step: 1));
        log.Record(Rec("Failed", step: 2, reason: "never appeared"));
        log.Record(Rec("Failed", step: 2, reason: "never appeared again"));

        var failures = log.Failures();
        Assert.Equal(2, failures.Count);
        Assert.Equal(2, failures[0].Example.StepIndex);
        Assert.Equal(2, failures[0].Count);
        Assert.Equal("never appeared again", failures[0].LastReason);
        Assert.Contains("2x  Mogwin's Trial seq 1 step 3 Interact 5", log.FailuresText());
    }

    [Fact]
    public void Appends_jsonl_to_disk_and_keeps_a_ring_in_memory()
    {
        var file = Path.Combine(_dir, "runlog.jsonl");
        var log = new RunLog(file);
        for (var i = 0; i < 3; i++) log.Record(Rec("Done", i));

        var lines = File.ReadAllLines(file);
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"Outcome\":\"Done\"", lines[0]);
        Assert.Equal(3, log.Count);
        Assert.Equal(2, log.Recent.First().StepIndex); // newest first
    }
}
