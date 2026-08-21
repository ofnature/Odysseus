using System.Numerics;
using Odysseus.Services.Paths;

namespace Odysseus.Tests;

public class PathStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Rescanning_picks_up_paths_another_client_wrote_after_we_looked()
    {
        // Two clients share the folder. This one looked first and found nothing.
        var store = new PathStore(_dir);
        Assert.False(store.Has(3729));
        Assert.Equal(0, store.Count);

        new PathStore(_dir).Save(new QuestPath { QuestId = 3729, Name = "Oh, Beehive Yourself" });
        Assert.False(store.Has(3729)); // still holding what it read the first time

        store.Reload();
        Assert.True(store.Has(3729));
    }

    [Fact]
    public void A_path_round_trips_through_disk_with_positions_flags_and_enums_intact()
    {
        var store = new PathStore(_dir);
        var path = new QuestPath
        {
            QuestId = 1622,
            Name = "Mogwin's Trial",
            Category = "3.x - Heavensward/MSQ/D-3.0",
            SourceHash = "ABCDEF0123456789",
            Sequences =
            [
                new QuestSequence
                {
                    Sequence = 1,
                    Steps =
                    [
                        new QuestStep
                        {
                            Kind = StepKind.Combat, KindName = "Combat", TerritoryId = 400,
                            Position = new Vector3(1.5f, -2.25f, 3f),
                            EnemySpawnType = EnemySpawnType.AutoOnEnterArea,
                            KillEnemyDataIds = [4015],
                            CompletionQuestVariablesFlags = [null, 0x20, null, null, null, 128],
                            SkipConditions = new SkipConditions { StepIf = new StepCondition { Flying = "Unlocked" } },
                            DialogueChoices = [new DialogueChoice("YesNo", "P", null, true)],
                        },
                    ],
                },
                new QuestSequence { Sequence = 255 },
            ],
        };
        store.Save(path);

        var reloaded = new PathStore(_dir).ForQuest(1622)!;
        Assert.Equal("Mogwin's Trial", reloaded.Name);
        Assert.True(reloaded.IsMainScenario);
        var step = reloaded.Block(1)!.Steps[0];
        Assert.Equal(StepKind.Combat, step.Kind);
        Assert.Equal(new Vector3(1.5f, -2.25f, 3f), step.Position);
        Assert.Equal(EnemySpawnType.AutoOnEnterArea, step.EnemySpawnType);
        Assert.Equal(new byte?[] { null, 0x20, null, null, null, 128 }, step.CompletionQuestVariablesFlags);
        Assert.Equal("Unlocked", step.SkipConditions!.StepIf!.Flying);
        Assert.True(Assert.Single(step.DialogueChoices!).Yes);
        Assert.Empty(reloaded.Block(255)!.Steps);
    }

    [Fact]
    public void A_stored_path_from_an_older_converter_is_reconverted_even_when_the_source_is_unchanged()
    {
        // The bug this pins: adding a step kind changed nothing for quests that had not changed
        // upstream, because the skip test only compared source hashes.
        var bundle = QuestionableImporter.DefaultBundlePath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs"));
        if (!File.Exists(bundle))
            return;

        var store = new PathStore(_dir);
        var first = store.ImportBundle(bundle, folder => folder.Contains("3.x - Heavensward/MSQ"));
        Assert.True(first.Converted > 0);

        // Re-importing the same bundle with the same converter writes nothing…
        var again = new PathStore(_dir).ImportBundle(bundle, folder => folder.Contains("3.x - Heavensward/MSQ"));
        Assert.Equal(0, again.Reconverted);

        // …but a path stored under an older format version is replaced.
        var one = new PathStore(_dir).All.First();
        one.FormatVersion = QuestPath.CurrentFormatVersion - 1;
        new PathStore(_dir).Save(one);
        var third = new PathStore(_dir).ImportBundle(bundle, folder => folder.Contains("3.x - Heavensward/MSQ"));
        Assert.Equal(1, third.Reconverted);
        Assert.Equal(QuestPath.CurrentFormatVersion, new PathStore(_dir).ForQuest(one.QuestId)!.FormatVersion);
    }

    [Fact]
    public void Missing_directory_is_an_empty_store_not_an_error()
    {
        var store = new PathStore(Path.Combine(_dir, "nope"));
        Assert.Equal(0, store.Count);
        Assert.Null(store.ForQuest(1));
    }
}
