using Odysseus.Services.Ipc;

namespace Odysseus.Tests;

public class DutyAiProviderTests
{
    [Theory]
    [InlineData(true, "/bmrai on")]
    [InlineData(false, "/bmrai off")]
    public void BossMod_Reborns_AI_is_switched_on_for_the_fight_and_off_for_the_travel(bool on, string expected)
        => Assert.Equal(expected, PluginPresence.DutyAiCommand(PluginPresence.BossModRebornProvider, on));

    /// <summary>
    /// Minerva has no on/off switch — it publishes guidance (must-not-act, safe spots, presets) for
    /// a rotation plugin to act on. Sending it /bmrai would be a command to a plugin that has never
    /// heard of it, so nothing is sent and its own settings stand.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Minerva_is_left_to_its_own_settings(bool on)
        => Assert.Null(PluginPresence.DutyAiCommand(PluginPresence.MinervaProvider, on));

    /// <summary>
    /// A config written before the setting existed — or by hand — names no provider we know. The
    /// old behaviour is the right default: this fleet ran BossMod Reborn before there was a choice.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("BossMod")]
    [InlineData("something else entirely")]
    public void Anything_that_is_not_Minerva_is_driven_the_way_it_always_was(string provider)
        => Assert.Equal("/bmrai on", PluginPresence.DutyAiCommand(provider, true));

    [Fact]
    public void The_provider_name_is_matched_however_it_is_cased()
        => Assert.Null(PluginPresence.DutyAiCommand("minerva", true));
}
