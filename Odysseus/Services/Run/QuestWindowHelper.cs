using System;

namespace Odysseus.Services.Run;

/// <summary>
/// Quest accept, reward choice and complete when no run is going — what a player had TextAdvance for
/// while questing by hand. The same three presses a run makes (<see cref="IStepWorld.AcceptOfferedQuest"/>,
/// <see cref="IStepWorld.PickBestReward"/>, <see cref="IStepWorld.CompleteQuestRewardWindow"/>), with
/// the same guards — a window on screen, a button that is enabled — once a second at most, and never
/// while a run (or anything else of ours) has the windows.
/// </summary>
public sealed class QuestWindowHelper
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    private readonly IStepWorld _world;
    private DateTime _last;

    public QuestWindowHelper(IStepWorld world) => _world = world;

    /// <summary>What it last did, or why it is idle — for the settings line.</summary>
    public string Status { get; private set; } = "off";

    /// <param name="enabled">The setting, and Odysseus switched on.</param>
    /// <param name="somethingRunning">A run, or any other Odysseus job, is driving: it handles its own windows.</param>
    public void Tick(bool enabled, bool somethingRunning, DateTime now)
    {
        if (!enabled)
        {
            Status = "off";
            return;
        }
        if (somethingRunning)
        {
            Status = "idle — a run is handling quest windows itself";
            return;
        }
        if (now - _last < Every)
            return;
        _last = now;

        if (_world.IsAddonVisible("JournalAccept"))
        {
            Status = _world.AcceptOfferedQuest()
                ? $"accepted a quest at {now.ToLocalTime():HH:mm:ss}"
                : "a quest offer is up, but its Accept is not available";
            return;
        }

        if (_world.IsAddonVisible("JournalResult"))
        {
            // Complete first: greyed means an optional reward still needs choosing.
            if (_world.CompleteQuestRewardWindow())
                Status = $"completed a quest at {now.ToLocalTime():HH:mm:ss}";
            else if (_world.PickBestReward() is { } picked)
                Status = $"picked {picked}";
            else
                Status = "the reward window needs a choice — reward picking is off, or the window did not read as expected";
            return;
        }

        if (!Status.StartsWith("accepted", StringComparison.Ordinal) && !Status.StartsWith("completed", StringComparison.Ordinal)
            && !Status.StartsWith("picked", StringComparison.Ordinal))
            Status = "waiting for a quest window";
    }
}
