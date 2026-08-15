using System.Numerics;
using Odysseus.Services.Paths;

namespace Odysseus.Tests;

public class QuestionableImporterTests
{
    // Trimmed from the real 1622_Mogwin's Trial.json / 5427_Through the Thunder.json in the bundle.
    private const string Mogwin = """
        {
          "$schema": "https://qstxiv.github.io/schema/quest-v1.json",
          "Author": "alydev",
          "LastChecked": {"Username": "alydev", "Date": "2026-07-27"},
          "QuestSequence": [
            { "Sequence": 0, "Steps": [
              { "DataId": 1012083, "Position": { "X": 355.8556, "Y": -74.53787, "Z": 639.6123 }, "TerritoryId": 400, "InteractionType": "AcceptQuest" } ] },
            { "Sequence": 1, "Steps": [
              { "DataId": 1012081, "Position": { "X": 364.09546, "Y": -73.26239, "Z": 678.004 }, "TerritoryId": 400, "InteractionType": "Interact",
                "$": "0 0 0 0 0 0 -> 16 16 0 0 0 32", "CompletionQuestVariablesFlags": [null,null,null,null,null,32] },
              { "DataId": 1015174, "Position": { "X": 360.58594, "Y": -72.66654, "Z": 706.2333 }, "TerritoryId": 400, "InteractionType": "Interact",
                "CompletionQuestVariablesFlags": [null,{"High": 2},null,null,null,128],
                "SkipConditions": { "StepIf": { "Flying": "Unlocked", "InTerritory": [400] }, "AetheryteShortcutIf": { "QuestsCompleted": [1828] } },
                "AetheryteShortcut": "Churning Mists - Moghome",
                "AethernetShortcut": [ "[Ishgard] Aetheryte Plaza", "[Ishgard] The Last Vigil" ],
                "Fly": true, "Mount": false, "StopDistance": 4,
                "DialogueChoices": [ { "Type": "YesNo", "Prompt": "TEXT_X", "Yes": true } ] },
              { "Position": { "X": 1, "Y": 2, "Z": 3 }, "TerritoryId": 400, "InteractionType": "Combat", "EnemySpawnType": "AutoOnEnterArea",
                "KillEnemyDataIds": [ 4015 ], "ComplexCombatData": [ { "DataId": 6626, "MinimumKillCount": 2 } ] },
              { "TerritoryId": 400, "InteractionType": "SomethingNew", "DataId": 5 } ] },
            { "Sequence": 2 },
            { "Sequence": 255, "Steps": [
              { "DataId": 1012083, "Position": { "X": 355.8556, "Y": -74.53787, "Z": 639.6123 }, "TerritoryId": 400, "InteractionType": "CompleteQuest",
                "SinglePlayerDutyOptions": { "Enabled": true, "TestedBossModVersion": "0.1.0.1" } } ] }
          ]
        }
        """;

    [Fact]
    public void Parses_id_name_and_category_from_the_file_and_folder()
    {
        var path = QuestionableImporter.Parse("1622_Mogwin's Trial.json", "QuestPaths/3.x - Heavensward/MSQ/D-3.0", Mogwin, out _)!;
        Assert.Equal(1622, path.QuestId);
        Assert.Equal("Mogwin's Trial", path.Name);
        Assert.Equal("3.x - Heavensward/MSQ/D-3.0", path.Category);
        Assert.True(path.IsMainScenario);
        Assert.Equal("alydev", path.Author);
        Assert.Equal("2026-07-27", path.LastChecked);
        Assert.Equal(16, path.SourceHash.Length);
    }

    [Fact]
    public void Sequences_and_steps_round_trip_including_empty_blocks()
    {
        var path = QuestionableImporter.Parse("1622_x.json", "QuestPaths/3.x - Heavensward/MSQ", Mogwin, out var unknown)!;
        Assert.Equal(4, path.Sequences.Count);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, path.Sequences.Select(s => s.Sequence).ToArray());
        Assert.Empty(path.Block(2)!.Steps);
        Assert.Null(path.Block(7));

        var accept = path.Block(0)!.Steps[0];
        Assert.Equal(StepKind.AcceptQuest, accept.Kind);
        Assert.Equal(1012083u, accept.DataId);
        Assert.Equal(new Vector3(355.8556f, -74.53787f, 639.6123f), accept.Position);
        Assert.Equal(400u, accept.TerritoryId);

        Assert.Equal(1, unknown);
        var odd = path.Block(1)!.Steps[3];
        Assert.Equal(StepKind.Unknown, odd.Kind);
        Assert.Equal("SomethingNew", odd.KindName);
    }

    [Fact]
    public void Completion_flags_take_numbers_nulls_and_nibble_objects()
    {
        var path = QuestionableImporter.Parse("1622_x.json", "QuestPaths/x", Mogwin, out _)!;
        var first = path.Block(1)!.Steps[0].CompletionQuestVariablesFlags!;
        Assert.Equal(new byte?[] { null, null, null, null, null, 32 }, first);

        var second = path.Block(1)!.Steps[1].CompletionQuestVariablesFlags!;
        Assert.Equal(new byte?[] { null, 0x20, null, null, null, 128 }, second);
    }

    [Fact]
    public void Travel_dialogue_and_skip_conditions_are_kept()
    {
        var step = QuestionableImporter.Parse("1622_x.json", "QuestPaths/x", Mogwin, out _)!.Block(1)!.Steps[1];
        Assert.Equal("Churning Mists - Moghome", step.AetheryteShortcut);
        Assert.Equal(new[] { "[Ishgard] Aetheryte Plaza", "[Ishgard] The Last Vigil" }, step.AethernetShortcut);
        Assert.True(step.Fly);
        Assert.False(step.Mount);
        Assert.Equal(4f, step.StopDistance);
        Assert.Equal("Unlocked", step.SkipConditions!.StepIf!.Flying);
        Assert.Equal(new List<uint> { 400 }, step.SkipConditions.StepIf.InTerritory);
        Assert.Equal(new List<ushort> { 1828 }, step.SkipConditions.AetheryteShortcutIf!.QuestsCompleted);
        var choice = Assert.Single(step.DialogueChoices!);
        Assert.Equal("YesNo", choice.Type);
        Assert.True(choice.Yes);
    }

    [Fact]
    public void Combat_data_folds_complex_entries_into_kill_list_and_min_count()
    {
        var step = QuestionableImporter.Parse("1622_x.json", "QuestPaths/x", Mogwin, out _)!.Block(1)!.Steps[2];
        Assert.Equal(StepKind.Combat, step.Kind);
        Assert.Equal(EnemySpawnType.AutoOnEnterArea, step.EnemySpawnType);
        Assert.Equal(new List<uint> { 4015, 6626 }, step.KillEnemyDataIds);
        Assert.Equal(2, step.MinimumKillCount);
    }

    [Fact]
    public void Duty_options_surface_as_enabled_flag()
    {
        var step = QuestionableImporter.Parse("1622_x.json", "QuestPaths/x", Mogwin, out _)!.Block(255)!.Steps[0];
        Assert.True(step.DutyEnabled);
    }

    [Fact]
    public void A_file_without_an_id_prefix_is_rejected_not_thrown()
    {
        Assert.Null(QuestionableImporter.Parse("readme.json", "QuestPaths/x", "{}", out _));
    }

    [Fact]
    public void Real_bundle_on_this_machine_converts_the_whole_HW_MSQ_cleanly()
    {
        // Regression against the live data, when it is present. 138 HW MSQ files is the measured
        // count from the plan doc; the classification cross-check in QuestCatalog rests on it too.
        var bundle = QuestionableImporter.DefaultBundlePath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs"));
        if (!File.Exists(bundle))
            return;

        var (paths, report) = QuestionableImporter.Import(bundle, folder => folder.Contains("3.x - Heavensward/MSQ"));
        Assert.Equal(0, report.Failed);
        Assert.Equal(138, paths.Count);
        Assert.Equal(0, report.UnknownKinds);
        Assert.All(paths, p => Assert.True(p.IsMainScenario));
        Assert.Contains(paths, p => p.QuestId == 1622 && p.Name == "Mogwin's Trial");
    }
}
