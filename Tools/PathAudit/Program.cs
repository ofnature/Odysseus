// PathAudit — how much of a quest path the game's own sheets already know.
//
// Every recorded path in paths.pak is compared against what the Quest sheet says on its own:
// where the quest is taken, where it is handed in, and the Level rows behind each journal
// objective. The point is to measure, before anything depends on it, how far a path-free
// "smart mode" could get — and to count the two failures the recorded paths actually have:
// steps that teleport out of the zone they are already in, and quests with no path at all.
//
//   dotnet run --project Tools/PathAudit                     the whole library, summary only
//   dotnet run --project Tools/PathAudit -- --quest 5491     one quest, derived beside recorded
//   dotnet run --project Tools/PathAudit -- --worst 40       the most bloated paths
//
// It never references the plugin project: building that writes the dev-plugin DLL and reloads
// a live run.

using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using Lumina;
using Lumina.Excel.Sheets;

const float Near = 25f;          // "the same place" — a stop distance plus slack
const uint QuestRowBase = 65536;

var argv = args;
string? Arg(string name)
{
    var i = Array.IndexOf(argv, name);
    return i >= 0 && i + 1 < argv.Length ? argv[i + 1] : null;
}

var sqpack = Arg("--sqpack") ?? @"C:\game\sqpack";
var pakFile = Arg("--pak") ?? Path.Combine("Odysseus", "Assets", "paths.pak");
var only = Arg("--quest") is { } q ? ushort.Parse(q, CultureInfo.InvariantCulture) : (ushort?)null;
var worst = Arg("--worst") is { } w ? int.Parse(w, CultureInfo.InvariantCulture) : 0;

if (!Directory.Exists(sqpack)) { Console.Error.WriteLine($"No sqpack at {sqpack} — pass --sqpack."); return 1; }
if (!File.Exists(pakFile)) { Console.Error.WriteLine($"No pack at {pakFile} — pass --pak."); return 1; }

// ── the recorded library ──

var paths = ReadPack(pakFile);
Console.WriteLine($"{paths.Count} recorded paths from {pakFile}");

// ── the game's own sheets ──

var game = new GameData(sqpack);
var questSheet = game.GetExcelSheet<Quest>()!;
var levelSheet = game.GetExcelSheet<Level>()!;
var aetheryteSheet = game.GetExcelSheet<Aetheryte>()!;

// Territory → its place name, so a derived mark reads as a place rather than a number.
var zoneName = new Dictionary<uint, string>();
foreach (var t in game.GetExcelSheet<TerritoryType>()!)
{
    var name = t.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
    if (name.Length > 0) zoneName.TryAdd(t.RowId, name);
}
string Zone(uint territory) => zoneName.TryGetValue(territory, out var n) ? $"{n} ({territory})" : $"territory {territory}";

// Where each placed object stands. The quest sheet names the turn-in NPC but not its position;
// the Level sheet is the only thing that knows, so it is indexed once by what it places.
var levelOfObject = new Dictionary<uint, Mark>();
foreach (var lvl in levelSheet)
{
    if (lvl.Object.RowId == 0 || lvl.Territory.RowId == 0) continue;
    levelOfObject.TryAdd(lvl.Object.RowId, new Mark(lvl.Territory.RowId, new Vector3(lvl.X, lvl.Y, lvl.Z), lvl.Object.RowId));
}

// Aetheryte name → the territory it lands in, so a shortcut can be checked against the zone the
// step is actually in. Spelled the way the path data spells them, which is the way a player would:
// the same canonicalisation and city aliases as Services/Travel/AetheryteCatalog, or two thousand
// perfectly ordinary shortcuts go unresolved and the count below means nothing.
var aetheryteTerritory = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
foreach (var a in aetheryteSheet)
{
    if (!a.IsAetheryte || a.Territory.RowId == 0) continue;
    var name = a.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
    var zone = a.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
    if (name.Length == 0) continue;
    aetheryteTerritory.TryAdd(name, a.Territory.RowId);
    aetheryteTerritory.TryAdd(Canon(name), a.Territory.RowId);
    aetheryteTerritory.TryAdd($"{Canon(zone)} - {Canon(name)}", a.Territory.RowId);
}
foreach (var (alias, real) in new Dictionary<string, string>
         {
             ["Ishgard"] = "Foundation", ["Crystarium"] = "The Crystarium", ["Gridania"] = "New Gridania",
             ["Limsa Lominsa"] = "Limsa Lominsa Lower Decks", ["Ul'dah"] = "Ul'dah - Steps of Nald",
             ["Mor Dhona"] = "Revenant's Toll", ["Doman Enclave"] = "The Doman Enclave",
             ["Gold Saucer"] = "The Gold Saucer",
         })
    if (aetheryteTerritory.TryGetValue(real, out var territory))
        aetheryteTerritory[alias] = territory;

static string Canon(string part)
{
    var s = part.Trim();
    if (s.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) s = s[4..];
    return s.Equals("Rak'tika Greatwood", StringComparison.OrdinalIgnoreCase) ? "Rak'tika" : s;
}

var derived = new Dictionary<ushort, Derived>();
foreach (var quest in questSheet)
{
    if (quest.RowId <= QuestRowBase) continue;
    var id = (ushort)(quest.RowId - QuestRowBase);
    var name = quest.Name.ExtractText();
    if (string.IsNullOrEmpty(name)) continue;

    Mark? accept = quest.IssuerLocation.ValueNullable is { } issued && issued.Territory.RowId != 0
        ? new Mark(issued.Territory.RowId, new Vector3(issued.X, issued.Y, issued.Z), issued.Object.RowId)
        : null;
    var giver = quest.IssuerStart.RowId;
    var receiver = quest.TargetEnd.RowId;
    var turnIn = receiver != 0 && levelOfObject.TryGetValue(receiver, out var t) ? t : null;

    var bySeq = new Dictionary<byte, List<Mark>>();
    foreach (var todo in quest.TodoParams)
    {
        var seq = todo.ToDoCompleteSeq;
        if (seq == 0) continue;
        foreach (var levelRef in todo.ToDoLocation)
        {
            if (levelRef.ValueNullable is not { } lvl || lvl.Territory.RowId == 0) continue;
            if (!bySeq.TryGetValue(seq, out var list)) bySeq[seq] = list = [];
            list.Add(new Mark(lvl.Territory.RowId, new Vector3(lvl.X, lvl.Y, lvl.Z), lvl.Object.RowId));
        }
    }

    derived[id] = new Derived(id, name, accept, giver, turnIn, receiver, bySeq);
}

Console.WriteLine($"{derived.Count} quests in the sheet, "
    + $"{derived.Values.Count(d => d.Accept is not null)} with a placed quest giver, "
    + $"{derived.Values.Count(d => d.TurnIn is not null)} with a placed turn-in, "
    + $"{derived.Values.Count(d => d.BySeq.Count > 0)} with journal objectives.\n");

// ── one quest, side by side ──

if (only is { } wanted)
{
    PrintOne(wanted);
    return 0;
}

// ── quests by name, with whether anyone has recorded them ──

if (Arg("--find") is { } needle)
{
    foreach (var d in derived.Values
                 .Where(d => d.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(d => d.QuestId))
        Console.WriteLine($"  {d.QuestId,5} {Trim(d.Name, 46)} {(paths.ContainsKey(d.QuestId) ? "path" : "NO PATH"),7}"
            + $" | {d.BySeq.Count} objective sequence(s), {(d.Accept is null ? "no" : "a")} placed giver");
    return 0;
}

// ── the whole library ──

int acceptSeen = 0, acceptNpc = 0, acceptNear = 0;
int turnInSeen = 0, turnInNpc = 0, turnInNear = 0;
int seqSeen = 0, seqDerived = 0, seqCovered = 0, seqZoneOnly = 0;
int stepsPositioned = 0, marksDerived = 0;
int crossingTeleports = 0, questsWithCrossing = 0, unresolvedAetherytes = 0;
var bloat = new List<(ushort Quest, string Name, int Steps, int Marks)>();
var crossers = new List<(ushort Quest, string Name, int Count)>();

foreach (var path in paths.Values)
{
    if (!derived.TryGetValue(path.QuestId, out var d)) continue;

    // Accept and turn-in: the sheet names both NPCs outright.
    if (StepIn(path, 0, "AcceptQuest") is { } acceptStep && d.Accept is { } acceptMark)
    {
        acceptSeen++;
        if (acceptStep.DataId == d.AcceptNpc) acceptNpc++;
        if (acceptStep.Pos is { } p && acceptStep.Territory == acceptMark.Territory
            && Vector3.Distance(p, acceptMark.Pos) <= Near) acceptNear++;
    }
    if (StepIn(path, 255, "CompleteQuest") is { } endStep && d.TurnIn is { } endMark)
    {
        turnInSeen++;
        if (endStep.DataId == d.TurnInNpc) turnInNpc++;
        if (endStep.Pos is { } p && endStep.Territory == endMark.Territory
            && Vector3.Distance(p, endMark.Pos) <= Near) turnInNear++;
    }

    // Objectives: does the sheet know where the sequence ends?
    var questSteps = 0; var questMarks = 0;
    foreach (var seq in path.Seqs)
    {
        if (seq.Sequence is 0 or 255) continue;
        var positioned = seq.Steps.Where(s => s.Pos is not null).ToList();
        if (positioned.Count == 0) continue;
        seqSeen++;
        questSteps += positioned.Count;

        if (!d.BySeq.TryGetValue(seq.Sequence, out var marks) || marks.Count == 0) continue;
        seqDerived++;
        questMarks += marks.Count;

        // The step that finishes the sequence is its last positioned one; that is what a derived
        // mark has to agree with for derivation to be able to stand in for the path.
        var last = positioned[^1];
        if (marks.Any(m => m.Territory == last.Territory && Vector3.Distance(m.Pos, last.Pos!.Value) <= Near))
            seqCovered++;
        else if (marks.Any(m => m.Territory == last.Territory))
            seqZoneOnly++;
    }
    stepsPositioned += questSteps;
    marksDerived += questMarks;
    if (questSteps > 0 && questMarks > 0 && questSteps - questMarks >= 4)
        bloat.Add((path.QuestId, path.Name, questSteps, questMarks));

    // Teleports that leave the zone the step is in — the shape that throws a cold start away
    // from an NPC it is already standing next to.
    var crossing = 0;
    foreach (var step in path.Seqs.SelectMany(s => s.Steps))
    {
        if (step.Aetheryte is not { } shortcut || step.Territory == 0) continue;
        if (ResolveAetheryte(shortcut) is not { } lands) { unresolvedAetherytes++; continue; }
        if (lands != step.Territory) crossing++;
    }
    crossingTeleports += crossing;
    if (crossing > 0) { questsWithCrossing++; crossers.Add((path.QuestId, path.Name, crossing)); }
}

var pathless = derived.Values.Where(d => !paths.ContainsKey(d.QuestId)).ToList();

Console.WriteLine("── what the sheets already know ─────────────────────────────");
Console.WriteLine($"Accept step   : {acceptSeen,5} compared | NPC id matches {Pct(acceptNpc, acceptSeen)} | position within {Near:F0}y {Pct(acceptNear, acceptSeen)}");
Console.WriteLine($"Turn-in step  : {turnInSeen,5} compared | NPC id matches {Pct(turnInNpc, turnInSeen)} | position within {Near:F0}y {Pct(turnInNear, turnInSeen)}");
Console.WriteLine($"Objective seqs: {seqSeen,5} with a placed step | sheet names a place for {Pct(seqDerived, seqSeen)}");
Console.WriteLine($"                      of those, the sequence's last step is within {Near:F0}y {Pct(seqCovered, seqDerived)}"
                + $" | right zone, wrong spot {Pct(seqZoneOnly, seqDerived)}");
Console.WriteLine($"Step count    : {stepsPositioned,5} positioned steps against {marksDerived} derived marks"
                + $" ({(marksDerived == 0 ? 0 : (double)stepsPositioned / marksDerived):F1}x)");
Console.WriteLine();
Console.WriteLine("── what the recorded paths get wrong ────────────────────────");
Console.WriteLine($"Teleports leaving the step's own zone: {crossingTeleports} across {questsWithCrossing} quests"
                + $" ({unresolvedAetherytes} shortcuts unresolved here)");
Console.WriteLine($"Quests with objectives but no path   : {pathless.Count(p => p.BySeq.Count > 0 || p.Accept is not null)}");
Console.WriteLine();

if (worst > 0)
{
    Console.WriteLine($"── the {worst} paths with the most steps the sheets do not need ──");
    foreach (var b in bloat.OrderByDescending(b => b.Steps - b.Marks).Take(worst))
        Console.WriteLine($"  {b.Quest,5} {Trim(b.Name, 44)} {b.Steps,4} steps / {b.Marks,3} marks");
    Console.WriteLine();
    Console.WriteLine($"── the {worst} paths that teleport out of their own zone most ──");
    foreach (var c in crossers.OrderByDescending(c => c.Count).Take(worst))
        Console.WriteLine($"  {c.Quest,5} {Trim(c.Name, 44)} {c.Count,3} steps");
}

return 0;

// ── helpers ──

string Pct(int n, int of) => of == 0 ? "   n/a" : $"{100.0 * n / of,5:F1}% ({n})";
static string Trim(string s, int n) => s.Length <= n ? s.PadRight(n) : s[..(n - 1)] + "…";

uint? ResolveAetheryte(string shortcut)
{
    if (aetheryteTerritory.TryGetValue(shortcut, out var t)) return t;
    var dash = shortcut.LastIndexOf(" - ", StringComparison.Ordinal);
    if (dash >= 0 && aetheryteTerritory.TryGetValue(shortcut[(dash + 3)..], out t)) return t;
    return null;
}

static PathStep? StepIn(PathDoc path, byte sequence, string kind)
{
    foreach (var seq in path.Seqs)
        if (seq.Sequence == sequence)
            foreach (var step in seq.Steps)
                if (step.Kind == kind)
                    return step;
    return null;
}

void PrintOne(ushort id)
{
    if (!derived.TryGetValue(id, out var d)) { Console.WriteLine($"Quest {id} is not in the sheet."); return; }
    Console.WriteLine($"Quest {id} — {d.Name}");
    Console.WriteLine();
    Console.WriteLine("  derived from the sheets:");
    Console.WriteLine($"    accept   : npc {d.AcceptNpc} {Show(d.Accept)}");
    foreach (var (seq, marks) in d.BySeq.OrderBy(p => p.Key))
        foreach (var m in marks)
            Console.WriteLine($"    seq {seq,3}  : {Show(m)}{(m.Object != 0 ? $" (object {m.Object})" : "")}");
    Console.WriteLine($"    turn-in  : npc {d.TurnInNpc} {Show(d.TurnIn)}");
    Console.WriteLine();

    if (!paths.TryGetValue(id, out var path)) { Console.WriteLine("  no recorded path."); return; }
    Console.WriteLine($"  recorded path ({path.Seqs.Sum(s => s.Steps.Count)} steps):");
    foreach (var seq in path.Seqs)
        foreach (var step in seq.Steps)
            Console.WriteLine($"    seq {seq.Sequence,3}  : {step.Kind,-16} {(step.DataId is { } dd ? $"npc {dd,-9}" : new string(' ', 13))}"
                + $"{(step.Pos is { } p ? $"t{step.Territory} {p.X:F0},{p.Y:F0},{p.Z:F0}" : "")}"
                + $"{(step.Aetheryte is { } a ? $"  via {a}" : "")}");
}

string Show(Mark? m) => m is { } v ? $"{Zone(v.Territory)} {v.Pos.X:F0},{v.Pos.Y:F0},{v.Pos.Z:F0}" : "(nowhere named)";

static Dictionary<ushort, PathDoc> ReadPack(string file)
{
    var result = new Dictionary<ushort, PathDoc>();
    using var stream = File.OpenRead(file);
    using var gzip = new GZipStream(stream, CompressionMode.Decompress);
    using var reader = new StreamReader(gzip);
    while (reader.ReadLine() is { } line)
    {
        if (line.Length == 0) continue;
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var id = (ushort)root.GetProperty("QuestId").GetUInt32();
        var seqs = new List<PathSeq>();
        if (root.TryGetProperty("Sequences", out var sequences))
        {
            foreach (var seq in sequences.EnumerateArray())
            {
                var steps = new List<PathStep>();
                if (seq.TryGetProperty("Steps", out var stepArray))
                {
                    foreach (var step in stepArray.EnumerateArray())
                    {
                        Vector3? pos = step.TryGetProperty("Position", out var p)
                            ? new Vector3(p.GetProperty("X").GetSingle(), p.GetProperty("Y").GetSingle(), p.GetProperty("Z").GetSingle())
                            : null;
                        steps.Add(new PathStep(
                            step.TryGetProperty("Kind", out var k) ? k.GetString() ?? "" : "",
                            step.TryGetProperty("DataId", out var dd) ? dd.GetUInt32() : null,
                            step.TryGetProperty("TerritoryId", out var tt) ? tt.GetUInt32() : 0,
                            pos,
                            step.TryGetProperty("AetheryteShortcut", out var a) ? a.GetString() : null));
                    }
                }
                seqs.Add(new PathSeq(seq.TryGetProperty("Sequence", out var s) ? (byte)s.GetUInt32() : (byte)0, steps));
            }
        }
        result[id] = new PathDoc(id, root.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
            root.TryGetProperty("Category", out var c) ? c.GetString() ?? "" : "", seqs);
    }
    return result;
}

record Mark(uint Territory, Vector3 Pos, uint Object);
record Derived(ushort QuestId, string Name, Mark? Accept, uint AcceptNpc, Mark? TurnIn, uint TurnInNpc,
    Dictionary<byte, List<Mark>> BySeq);
record PathStep(string Kind, uint? DataId, uint Territory, Vector3? Pos, string? Aetheryte);
record PathSeq(byte Sequence, List<PathStep> Steps);
record PathDoc(ushort QuestId, string Name, string Category, List<PathSeq> Seqs);
