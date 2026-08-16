using System;

namespace Odysseus.Services.Quest;

/// <summary>
/// Which Main Scenario quest comes next for this character.
///
/// <para>
/// Two sources, in order. First the game's own Scenario Guide pointer
/// (<see cref="IQuestStateReader.CurrentScenarioQuest"/>) — the "the game already knows" answer,
/// and what QuestFlow uses too. Only when that is empty or points at something already done, the
/// catalog's chain walk over <c>PreviousQuest</c>, filtered by what the character is (start town,
/// first class, Grand Company) and by mutually exclusive <c>QuestLock</c>s.
/// </para>
/// </summary>
public sealed class StoryFrontier
{
    private readonly IQuestStateReader _quests;
    private readonly QuestCatalog _catalog;
    private readonly Func<byte> _preferredGrandCompany;

    public StoryFrontier(IQuestStateReader quests, QuestCatalog catalog, Func<byte> preferredGrandCompany)
    {
        _quests = quests;
        _catalog = catalog;
        _preferredGrandCompany = preferredGrandCompany;
    }

    private CharacterFacts Facts()
        => _quests.Character() with { PreferredGrandCompany = _preferredGrandCompany() };

    /// <summary>The MSQ quest to do now — accepted or not — or null when the story is finished or blocked.</summary>
    public QuestListing? Current()
    {
        if (FromAgent(exclude: null) is { } fromAgent)
            return fromAgent;
        return _catalog.CurrentMainScenario(_quests.IsComplete, Facts());
    }

    /// <summary>The MSQ quest after <paramref name="completed"/>, or null.</summary>
    public ushort? Next(ushort completed)
    {
        if (FromAgent(exclude: completed) is { } fromAgent)
            return fromAgent.QuestId;
        return _catalog.NextMainScenario(completed, _quests.IsComplete, Facts());
    }

    /// <summary>Which source answered last — for the debug window.</summary>
    public string LastSource { get; private set; } = string.Empty;

    private QuestListing? FromAgent(ushort? exclude)
    {
        var id = _quests.CurrentScenarioQuest();
        if (id is null || id == exclude || _quests.IsComplete(id.Value))
        {
            LastSource = "chain";
            return null;
        }
        var listing = _catalog.ById(id.Value);
        if (listing is null || !listing.IsMainScenario)
        {
            LastSource = "chain";
            return null;
        }
        LastSource = "scenario guide";
        return listing;
    }
}
