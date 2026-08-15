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
    public void Missing_directory_is_an_empty_store_not_an_error()
    {
        var store = new PathStore(Path.Combine(_dir, "nope"));
        Assert.Equal(0, store.Count);
        Assert.Null(store.ForQuest(1));
    }
}
