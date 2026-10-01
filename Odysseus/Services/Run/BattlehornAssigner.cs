using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Run;

/// <summary>
/// "/od battlehorn 1 Cu Sith" — put a Bestiary pet on a battlehorn, the way a right-click does it;
/// also what a Beastmaster quest step asks for ("Assign Cu Sith to first battlehorn").
///
/// Read from a recording of the Master's Bestiary: a right-click on a tile makes the window fire
/// its own callback <c>8, tile</c>, which raises the ContextMenu; the menu's "Assign to First /
/// Second / Third Battlehorn" is entry 0/1/2. Tiles run in XBMPet row order, 25 to a page and
/// counted from each page's start (Cu Sith row 1 is tile 0 of page 1, the goobbue row 30 is tile 4
/// of page 2); the page buttons send <c>3, page</c>. Choosing the slot a pet already holds frees
/// it instead, so the slots are read first. The Bestiary is opened when it is not up — the Character
/// menu's own "Master's Bestiary" main command, the way Daedalus's BattlehornGame does it — and
/// closed again afterwards if it was opened here.
/// </summary>
public sealed unsafe class BattlehornAssigner
{
    private static readonly string[] SlotNames = ["first", "second", "third"];
    private const int PerPage = 25;
    private const string Bestiary = "XBMMonsterNotebook";

    private enum Stage { Idle, Opening, Turning, Clicked, Picked }

    private readonly GameStepWorld _world;
    private readonly IDataManager _data;
    private readonly Action<string> _say;

    private Stage _stage;
    private int _slot;            // 0-based while running
    private uint _pet;
    private string _petName = string.Empty;
    private bool _openedHere;
    private DateTime _since;
    private uint _bestiaryCommand;

    public BattlehornAssigner(GameStepWorld world, IDataManager data, Action<string> say)
    {
        _world = world;
        _data = data;
        _say = say;
    }

    public bool Busy => _stage != Stage.Idle;

    /// <summary>The last assignment's outcome — true when the pet landed on its battlehorn.</summary>
    public bool LastSucceeded { get; private set; }

    /// <summary>The last thing said, for a step that failed to quote.</summary>
    public string LastMessage { get; private set; } = string.Empty;

    /// <summary>"" lists the battlehorns; "&lt;1-3&gt; &lt;pet&gt;" assigns one.</summary>
    public string Start(string args)
    {
        if (args.Length == 0)
            return "Battlehorns: " + DescribeSlots(Pets());

        var space = args.IndexOf(' ');
        if (space < 0 || !int.TryParse(args[..space], out var slot) || slot is < 1 or > 3)
            return "Usage: /od battlehorn <1-3> <pet name>, or /od battlehorn to list them.";
        return Assign(slot, args[(space + 1)..].Trim());
    }

    /// <summary>The pet on a battlehorn (1–3), by name; null when it is empty.</summary>
    public string? PetOn(int slot)
    {
        var row = Slots()[slot - 1];
        return row == 0 ? null : Pets().GetValueOrDefault(row);
    }

    /// <summary>Start putting a pet on a battlehorn (1–3). Returns what was said; <see cref="Busy"/> while it runs.</summary>
    public string Assign(int slot, string name)
    {
        if (Busy)
            return "Still assigning the last one.";
        var pets = Pets();
        var row = 0u;
        foreach (var (id, petName) in pets)
            if (string.Equals(petName, name, StringComparison.OrdinalIgnoreCase))
                row = id;
        if (row == 0)
            return Done(false, $"No Bestiary pet is called \"{name}\".", say: false);
        if (Slots()[slot - 1] == row)
            return Done(true, $"Your {pets[row]} is on the {SlotNames[slot - 1]} battlehorn already.", say: false);

        _slot = slot - 1;
        _pet = row;
        _petName = pets[row];
        _since = DateTime.UtcNow;
        _openedHere = false;
        if (_world.IsAddonVisible(Bestiary))
            return TurnPage();

        if (!OpenBestiary())
            return Done(false, "The Master's Bestiary could not be opened — is this character a Beastmaster?", say: false);
        _openedHere = true;
        _stage = Stage.Opening;
        return $"Opening the Master's Bestiary to put {_petName} on the {SlotNames[_slot]} battlehorn…";
    }

    public void Tick()
    {
        if (_stage == Stage.Idle)
            return;
        var waited = DateTime.UtcNow - _since;

        switch (_stage)
        {
            case Stage.Opening:
                if (_world.IsAddonVisible(Bestiary))
                {
                    if (waited > TimeSpan.FromSeconds(0.5))   // let it fill in
                        TurnPage();
                    return;
                }
                if (waited > TimeSpan.FromSeconds(5))
                    Finish(false, "The Master's Bestiary never opened.");
                return;

            case Stage.Turning:
                if (waited < TimeSpan.FromSeconds(0.3))
                    return;
                // Right-click the tile: the window asks its agent for the menu.
                _stage = Stage.Clicked;
                _since = DateTime.UtcNow;
                if (!_world.FireAddonValues(Bestiary, 8, ((int)_pet - 1) % PerPage))
                    Finish(false, "The Bestiary closed before the pet could be picked.");
                return;

            case Stage.Clicked:
                var menu = _world.ContextMenuEntries();
                if (menu.Count > _slot && menu[_slot].Contains("Battlehorn", StringComparison.OrdinalIgnoreCase))
                {
                    _world.SelectContextMenuIndex(_slot);
                    _stage = Stage.Picked;
                    _since = DateTime.UtcNow;
                }
                else if (menu.Count > 0 || waited > TimeSpan.FromSeconds(3))
                {
                    Finish(false, menu.Count > 0
                        ? $"The menu for {_petName} did not offer the {SlotNames[_slot]} battlehorn: [{string.Join(" | ", menu)}]."
                        : $"No menu opened for {_petName} (page {(_pet - 1) / PerPage + 1}, tile {(_pet - 1) % PerPage}) — not captured yet?");
                }
                return;

            case Stage.Picked:
                // The assignment comes back from the server; give it a moment.
                if (waited < TimeSpan.FromSeconds(1))
                    return;
                var landed = Slots()[_slot] == _pet;
                var pets = Pets();
                Finish(landed, landed
                    ? $"{_petName} is on the {SlotNames[_slot]} battlehorn. Battlehorns: {DescribeSlots(pets)}"
                    : $"{_petName} did not land on the {SlotNames[_slot]} battlehorn. Battlehorns: {DescribeSlots(pets)}");
                return;
        }
    }

    /// <summary>Its page first; the right-click follows once the page has turned.</summary>
    private string TurnPage()
    {
        if (!_world.FireAddonValues(Bestiary, 3, ((int)_pet - 1) / PerPage))
        {
            Finish(false, "The Bestiary did not take the click.");
            return LastMessage;
        }
        _stage = Stage.Turning;
        _since = DateTime.UtcNow;
        return $"Assigning {_petName} to the {SlotNames[_slot]} battlehorn…";
    }

    private void Finish(bool landed, string message)
    {
        _stage = Stage.Idle;
        if (_openedHere)
            _world.CloseAddon(Bestiary);
        Done(landed, message, say: true);
    }

    private string Done(bool landed, string message, bool say)
    {
        LastSucceeded = landed;
        LastMessage = message;
        if (say)
            _say(message);
        return message;
    }

    /// <summary>The Character menu's "Master's Bestiary", by its English MainCommand name — a row id a patch could move.</summary>
    private bool OpenBestiary()
    {
        try
        {
            if (_bestiaryCommand == 0)
                foreach (var row in _data.GetExcelSheet<MainCommand>(Dalamud.Game.ClientLanguage.English))
                    if (row.Name.ExtractText() == "Master's Bestiary")
                        _bestiaryCommand = row.RowId;
            var ui = UIModule.Instance();
            if (_bestiaryCommand == 0 || ui == null || !ui->IsMainCommandUnlocked(_bestiaryCommand))
                return false;
            ui->ExecuteMainCommand(_bestiaryCommand);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Slots()
    {
        var manager = ActionManager.Instance();
        return manager == null ? new byte[3] : manager->BeastmasterPets.ToArray();
    }

    private string DescribeSlots(Dictionary<uint, string> pets)
    {
        var slots = Slots();
        var parts = new string[slots.Length];
        for (var i = 0; i < slots.Length; i++)
            parts[i] = slots[i] == 0 ? "empty" : pets.GetValueOrDefault(slots[i], $"pet {slots[i]}");
        return string.Join(" / ", parts);
    }

    /// <summary>XBMPet row → name. The row's first column is the Pet sheet row that names it.</summary>
    private Dictionary<uint, string> Pets()
    {
        var names = new Dictionary<uint, string>();
        var petNames = _data.GetExcelSheet<Pet>();
        foreach (var row in _data.Excel.GetSheet<RawRow>(name: "XBMPet"))
        {
            if (row.RowId == 0)
                continue;
            var pet = Convert.ToUInt32(row.ReadColumn(0));
            var name = petNames.GetRowOrDefault(pet)?.Name.ExtractText();
            if (!string.IsNullOrEmpty(name))
                names[row.RowId] = name;
        }
        return names;
    }
}
