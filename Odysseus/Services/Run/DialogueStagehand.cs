using System;
using System.Collections.Generic;

namespace Odysseus.Services.Run;

/// <summary>
/// The two dialogue chores a run needs done every frame that the step machinery does not own:
/// advancing the subtitle box, and answering the "Skip cutscene?" prompt. TextAdvance does both
/// when it is installed and held; this is the built-in version, so a client without TextAdvance
/// still talks its way through a quest.
///
/// <para>
/// Deliberately not here: the cutscene ESC press (a code hook and patch in TextAdvance's stack —
/// not worth owning), and reward picking (TextAdvance's memory call; without it a quest with
/// optional rewards stops at the window and says so). Quest accept/complete and request
/// fill/hand-over are the step machinery's own, elsewhere.
/// </para>
/// </summary>
public sealed class DialogueStagehand
{
    /// <summary>
    /// The game's own "Yes." entry in the skip-cutscene prompt, in every client language. The
    /// prompt itself is only matched by circumstance — during a cutscene, the only list on
    /// screen is that one.
    /// </summary>
    private static readonly string[] YesStrings = ["Yes.", "是", "Ja", "Oui", "はい", "예"];

    private static readonly TimeSpan SelectThrottle = TimeSpan.FromSeconds(1);

    private DateTime _lastSelect;

    public void Tick(IStepWorld world)
    {
        if (world.IsAddonVisible("Talk"))
            world.AdvanceTalk();

        if (!world.InCutscene || !world.IsAddonVisible("SelectString"))
            return;
        if (world.UtcNow - _lastSelect < SelectThrottle)
            return;

        var entries = world.SelectStringEntries();
        for (var i = 0; i < entries.Count; i++)
        {
            if (!IsYes(entries[i]))
                continue;
            _lastSelect = world.UtcNow;
            world.SelectStringIndex(i);
            return;
        }
    }

    private static bool IsYes(string entry)
    {
        foreach (var yes in YesStrings)
            if (entry.Equals(yes, StringComparison.OrdinalIgnoreCase) || entry.StartsWith(yes, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
