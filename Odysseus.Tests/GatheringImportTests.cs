using System.Numerics;
using Odysseus.Services.Gathering;
using Odysseus.Services.Paths;

namespace Odysseus.Tests;

public class GatheringImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Trimmed from QuestFlow's 974_Chabameki_MIN.json, keeping every shape it uses.</summary>
    private const string Sample = """
    {
      "$schema": "https://git.carvel.li/liza/QuestFlow/raw/branch/master/GatheringPaths/gatheringlocation-v1.json",
      "Author": "liza",
      "FlyBetweenNodes": true,
      "Steps": [
        {
          "TerritoryId": 1187,
          "InteractionType": "None",
          "AetheryteShortcut": "Urqopacha - Wachunpelo",
          "SkipConditions": { "AetheryteShortcutIf": { "InSameTerritory": true } }
        }
      ],
      "Groups": [
        {
          "Nodes": [
            {
              "DataId": 34000,
              "Locations": [
                { "Position": { "X": -392.8, "Y": -47.0, "Z": -386.8 }, "MinimumAngle": -10, "MaximumAngle": 240 },
                { "Position": { "X": -380.1, "Y": -46.2, "Z": -390.4 }, "MinimumDistance": 1.5, "MaximumDistance": 3 }
              ]
            },
            { "DataId": 34001, "Fly": true, "Locations": [ { "Position": { "X": -370.0, "Y": -45.0, "Z": -380.0 } } ] }
          ]
        }
      ]
    }
    """;

    [Fact]
    public void A_gathering_path_carries_its_nodes_angles_and_the_way_there()
    {
        var path = GatheringPathImporter.Parse("974_Chabameki_MIN.json", "7.x - Dawntrail/Urqopacha", Sample);

        Assert.NotNull(path);
        Assert.Equal(974u, path!.PointBaseId);
        Assert.Equal("Chabameki", path.Name);
        Assert.Equal("MIN", path.Job);
        Assert.Equal("7.x - Dawntrail/Urqopacha", path.Category);
        Assert.Equal("liza", path.Author);
        Assert.True(path.FlyBetweenNodes);
        Assert.NotEmpty(path.SourceHash);

        // The travel steps go through the quest importer, so they arrive as ordinary steps.
        var step = Assert.Single(path.Steps);
        Assert.Equal(1187u, step.TerritoryId);
        Assert.Equal("Urqopacha - Wachunpelo", step.AetheryteShortcut);

        var group = Assert.Single(path.Groups);
        Assert.Equal(2, group.Nodes.Count);
        Assert.Equal(34000u, group.Nodes[0].DataId);
        Assert.True(group.Nodes[1].Fly);

        var first = group.Nodes[0].Locations[0];
        Assert.Equal(new Vector3(-392.8f, -47.0f, -386.8f), first.Position);
        Assert.Equal(-10f, first.MinimumAngle);
        Assert.Equal(240f, first.MaximumAngle);

        // The two ways a location can be qualified are independent; neither is invented.
        var second = group.Nodes[0].Locations[1];
        Assert.Null(second.MinimumAngle);
        Assert.Equal(1.5f, second.MinimumDistance);

        Assert.Equal(3, path.AllLocations().Count());
    }

    [Fact]
    public void A_file_that_is_not_a_path_is_skipped_rather_than_guessed_at()
    {
        // The schema documents sit in the same tree; they have no numeric prefix and no Groups.
        Assert.Null(GatheringPathImporter.Parse("gatheringlocation-v1.json", "", """{"version":1}"""));
        Assert.Null(GatheringPathImporter.Parse("974_Chabameki_MIN.json", "", """{"Author":"liza"}"""));
    }

    [Fact]
    public void Stored_paths_come_back_whole_and_the_folder_is_re_readable()
    {
        var path = GatheringPathImporter.Parse("974_Chabameki_MIN.json", "7.x - Dawntrail/Urqopacha", Sample)!;
        var store = new GatheringStore(_dir);
        store.Save(path);

        var reopened = new GatheringStore(_dir);
        var again = reopened.ForPointBase(974);
        Assert.NotNull(again);
        Assert.Equal(path.Name, again!.Name);
        Assert.Equal(path.AllLocations().Count(), again.AllLocations().Count());
        Assert.Equal(new Vector3(-392.8f, -47.0f, -386.8f), again.AllLocations().First().Position);
        Assert.Equal("Urqopacha - Wachunpelo", Assert.Single(again.Steps).AetheryteShortcut);

        // Written by another client after this one looked: nothing until it looks again.
        var other = GatheringPathImporter.Parse("992_Chabameki_BTN.json", "7.x - Dawntrail/Urqopacha", Sample)!;
        new GatheringStore(_dir).Save(other);
        Assert.Null(reopened.ForPointBase(992));
        reopened.Reload();
        Assert.NotNull(reopened.ForPointBase(992));
    }

    [Fact]
    public void An_item_worked_by_two_points_takes_the_one_with_more_spawns()
    {
        var many = GatheringPathImporter.Parse("974_Chabameki_MIN.json", "", Sample)!;
        var few = GatheringPathImporter.Parse("992_Chabameki_BTN.json", "", Sample)!;
        few.Groups[0].Nodes.RemoveAt(1); // one location fewer

        var store = new GatheringStore(_dir);
        store.Save(many);
        store.Save(few);

        var order = store.ForPointBases([992u, 974u]);
        Assert.Equal([974u, 992u], order.Select(p => p.PointBaseId));

        // A point nobody imported is left out rather than returned empty.
        Assert.Empty(store.ForPointBases([983u]));
    }
}
