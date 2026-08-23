using System.Text;
using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

public class BaitCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "odysseus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Trimmed from AutoHook's own fish_list.json, keeping the fields and the extras.</summary>
    private const string Sample = """
    [
      { "ItemId": 4776, "HookType": 4179, "BiteType": 36, "InitialBait": 2585, "Mooches": [],
        "Predators": [], "Nodes": [], "IsSpearFish": false, "SpotIds": [35, 36, 44] },
      { "ItemId": 44334, "InitialBait": 0, "Mooches": [4776, 4777], "IsSpearFish": false },
      { "ItemId": 41060, "InitialBait": 0, "Mooches": [], "IsSpearFish": true },
      { "ItemId": 0, "InitialBait": 999 }
    ]
    """;

    private static Dictionary<uint, BaitPlan> Parsed()
        => BaitCatalog.Parse(new MemoryStream(Encoding.UTF8.GetBytes(Sample)));

    [Fact]
    public void Bait_mooch_chains_and_spearfish_all_come_through()
    {
        var fish = Parsed();
        Assert.Equal(3, fish.Count); // the id-less row is not a fish

        Assert.Equal(2585u, fish[4776].Bait);
        Assert.Empty(fish[4776].Mooches);
        Assert.False(fish[4776].IsEmpty);

        // Mooch-only: no bait of its own, but a chain to get there.
        Assert.Equal(0u, fish[44334].Bait);
        Assert.Equal([4776u, 4777u], fish[44334].Mooches);
        Assert.False(fish[44334].IsEmpty);

        // Speared: no bait is correct rather than missing.
        Assert.True(fish[41060].IsSpearFish);
        Assert.False(fish[41060].IsEmpty);
    }

    [Fact]
    public void A_fish_with_nothing_to_cast_and_nothing_to_mooch_says_so()
    {
        var lost = new BaitPlan(1, 0, [], false);
        Assert.True(lost.IsEmpty);
    }

    [Fact]
    public void Without_AutoHook_it_says_what_it_needs_rather_than_guessing()
    {
        var logged = new List<string>();
        var catalogue = new BaitCatalog(() => null, logged.Add);
        Assert.Equal(0, catalogue.Count);
        Assert.Null(catalogue.For(4776));
        Assert.Contains(logged, m => m.Contains("AutoHook is not installed"));

        var wrong = new BaitCatalog(() => _dir, logged.Add);
        Assert.Equal(0, wrong.Count);
        Assert.Contains(logged, m => m.Contains("not where it was expected"));
    }

    [Fact]
    public void It_reads_the_file_from_an_AutoHook_folder()
    {
        var data = Path.Combine(_dir, "Data", "FishData");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "fish_list.json"), Sample);

        var catalogue = new BaitCatalog(() => _dir);
        Assert.Equal(3, catalogue.Count);
        Assert.Equal(2585u, catalogue.For(4776)!.Bait);
        Assert.Null(catalogue.For(12345));
    }
}
