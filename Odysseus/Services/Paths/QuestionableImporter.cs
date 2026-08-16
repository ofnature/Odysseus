using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Odysseus.Services.Paths;

/// <summary>What one import pass did.</summary>
public sealed class ImportReport
{
    public int Converted { get; set; }
    public int Failed { get; set; }
    /// <summary>Files that are not quests (no <c>{id}_</c> prefix — allied-society and delivery bundles). Expected, not an error.</summary>
    public int Skipped { get; set; }
    public int UnknownKinds { get; set; }
    public string DataVersion { get; set; } = string.Empty;
    public string GeneratedAt { get; set; } = string.Empty;
    public List<string> Errors { get; } = [];

    public override string ToString()
        => $"{Converted} converted, {Failed} failed" + (Skipped > 0 ? $", {Skipped} non-quest files skipped" : "")
           + (UnknownKinds > 0 ? $", {UnknownKinds} steps of unknown kind" : "")
           + (DataVersion.Length > 0 ? $" · bundle {DataVersion} ({GeneratedAt})" : "");
}

/// <summary>
/// Reads the user's own installed Questionable path bundle and converts each quest into
/// <see cref="QuestPath"/>.
///
/// <para>
/// <b>Licence rule.</b> The bundle is read from the user's machine, converted on the user's
/// machine, and the result is stored in the user's config directory. Nothing from it is ever
/// shipped in this repository. Questionable ≥6.9 is proprietary and ≤6.8 is AGPL; this converter
/// reads a data format, it does not lift code.
/// </para>
///
/// <para>
/// <b>Schema is checked, not assumed.</b> <c>manifest.json</c> carries <c>schemaVersion</c>; anything
/// but 1 refuses loudly instead of mis-parsing. Inside a file, an unknown <c>InteractionType</c>
/// becomes <see cref="StepKind.Unknown"/> with the name kept, and an unreadable file is counted
/// and skipped — one broken quest never blocks the other 4,737.
/// </para>
/// </summary>
public static class QuestionableImporter
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>Where the installed plugin keeps its bundle.</summary>
    public static string DefaultBundlePath(string pluginConfigsRoot)
        => Path.Combine(pluginConfigsRoot, "Questionable", "PathData", "bundle.zip");

    /// <summary>
    /// Convert every quest in the bundle. <paramref name="filter"/> sees the in-bundle folder path
    /// (e.g. <c>QuestPaths/3.x - Heavensward/MSQ/A-3.0</c>) and can restrict what is converted.
    /// </summary>
    public static (List<QuestPath> Paths, ImportReport Report) Import(string bundlePath, Func<string, bool>? filter = null)
    {
        var report = new ImportReport();
        var paths = new List<QuestPath>();

        using var zip = ZipFile.OpenRead(bundlePath);

        var manifest = zip.GetEntry("manifest.json")
                       ?? throw new InvalidDataException("bundle has no manifest.json — not a path bundle");
        using (var doc = JsonDocument.Parse(ReadAll(manifest)))
        {
            var root = doc.RootElement;
            var schema = root.TryGetProperty("schemaVersion", out var sv) ? sv.GetInt32() : -1;
            if (schema != SupportedSchemaVersion)
                throw new InvalidDataException(
                    $"bundle schemaVersion {schema} is not supported (importer speaks {SupportedSchemaVersion}) — refusing to guess");
            report.DataVersion = root.TryGetProperty("dataVersion", out var dv) ? dv.ToString() : string.Empty;
            report.GeneratedAt = root.TryGetProperty("generatedAt", out var ga) ? ga.ToString() : string.Empty;
        }

        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith("QuestPaths/", StringComparison.Ordinal)
                || !entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var folder = entry.FullName[..^entry.Name.Length].TrimEnd('/');
            if (filter is not null && !filter(folder))
                continue;

            try
            {
                var json = ReadAll(entry);
                var path = Parse(entry.Name, folder, json, out var unknownKinds);
                if (path is null)
                {
                    report.Skipped++;
                    continue;
                }
                report.UnknownKinds += unknownKinds;
                paths.Add(path);
                report.Converted++;
            }
            catch (Exception ex)
            {
                report.Failed++;
                if (report.Errors.Count < 50)
                    report.Errors.Add($"{entry.FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return (paths, report);
    }

    /// <summary>
    /// Convert one file. Public and pure so it can be tested against sample JSON without a zip.
    /// <paramref name="fileName"/> is <c>{questId}_{Name}.json</c>; the id comes from there because
    /// the JSON body does not carry it.
    /// </summary>
    public static QuestPath? Parse(string fileName, string folder, string json, out int unknownKinds)
    {
        unknownKinds = 0;

        var underscore = fileName.IndexOf('_');
        if (underscore <= 0 || !ushort.TryParse(fileName.AsSpan(0, underscore), out var questId))
            return null;

        var name = Path.GetFileNameWithoutExtension(fileName)[(underscore + 1)..];
        var category = folder.StartsWith("QuestPaths/", StringComparison.Ordinal) ? folder["QuestPaths/".Length..] : folder;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var path = new QuestPath
        {
            QuestId = questId,
            Name = name,
            Category = category,
            Author = Str(root, "Author"),
            SourceHash = Hash(json),
        };
        if (root.TryGetProperty("LastChecked", out var lc) && lc.ValueKind == JsonValueKind.Object)
            path.LastChecked = Str(lc, "Date");

        if (root.TryGetProperty("QuestSequence", out var sequences) && sequences.ValueKind == JsonValueKind.Array)
        {
            foreach (var seq in sequences.EnumerateArray())
            {
                var block = new QuestSequence { Sequence = (byte)(seq.TryGetProperty("Sequence", out var sn) ? sn.GetInt32() : 0) };
                if (seq.TryGetProperty("Steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var step in steps.EnumerateArray())
                    {
                        var converted = ParseStep(step);
                        if (converted.Kind == StepKind.Unknown)
                            unknownKinds++;
                        block.Steps.Add(converted);
                    }
                }
                path.Sequences.Add(block);
            }
        }

        return path;
    }

    private static QuestStep ParseStep(JsonElement e)
    {
        var kindName = Str(e, "InteractionType") ?? "None";
        var step = new QuestStep
        {
            KindName = kindName,
            Kind = Enum.TryParse<StepKind>(kindName, ignoreCase: false, out var kind) ? kind : StepKind.Unknown,
            DataId = U32(e, "DataId"),
            TerritoryId = U32(e, "TerritoryId") ?? 0,
            TargetTerritoryId = U32(e, "TargetTerritoryId"),
            StopDistance = F32(e, "StopDistance"),
            Fly = Bool(e, "Fly") ?? false,
            Mount = Bool(e, "Mount"),
            DisableNavmesh = Bool(e, "DisableNavmesh") ?? false,
            AetheryteShortcut = Str(e, "AetheryteShortcut"),
            AetherCurrentId = U32(e, "AetherCurrentId"),
            ItemId = U32(e, "ItemId"),
            Emote = Str(e, "Emote"),
            DelaySecondsAtStart = F32(e, "DelaySecondsAtStart"),
            Comment = Str(e, "$") ?? Str(e, "Comment"),
            MinimumKillCount = I32(e, "MinimumKillCount"),
        };

        if (U32(e, "PickUpQuestId") is { } pick && pick <= ushort.MaxValue)
            step.PickUpQuestId = (ushort)pick;

        if (e.TryGetProperty("Position", out var pos) && pos.ValueKind == JsonValueKind.Object)
            step.Position = new Vector3(F32(pos, "X") ?? 0, F32(pos, "Y") ?? 0, F32(pos, "Z") ?? 0);

        if (e.TryGetProperty("AethernetShortcut", out var aethernet) && aethernet.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var p in aethernet.EnumerateArray()) parts.Add(p.GetString() ?? string.Empty);
            step.AethernetShortcut = parts.ToArray();
        }

        if (e.TryGetProperty("CompletionQuestVariablesFlags", out var flags))
            step.CompletionQuestVariablesFlags = ParseFlags(flags);

        if (e.TryGetProperty("ChatMessage", out var chat) && chat.ValueKind == JsonValueKind.Object)
            step.ChatMessageKey = Str(chat, "Key");

        step.ActionName = Str(e, "Action");
        step.GroundTarget = Bool(e, "GroundTarget") ?? false;
        if (e.TryGetProperty("RequiredQuestVariables", out var required) && required.ValueKind == JsonValueKind.Array)
            step.RequiredQuestVariables = ParseRequired(required);

        if (e.TryGetProperty("DialogueChoices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            step.DialogueChoices = [];
            foreach (var c in choices.EnumerateArray())
                step.DialogueChoices.Add(new DialogueChoice(Str(c, "Type") ?? "?", Str(c, "Prompt"), Str(c, "Answer"), Bool(c, "Yes")));
        }

        if (e.TryGetProperty("SkipConditions", out var skip) && skip.ValueKind == JsonValueKind.Object)
        {
            step.SkipConditions = new SkipConditions
            {
                StepIf = skip.TryGetProperty("StepIf", out var si) ? ParseCondition(si) : null,
                AetheryteShortcutIf = skip.TryGetProperty("AetheryteShortcutIf", out var ai) ? ParseCondition(ai) : null,
                AethernetShortcutIf = skip.TryGetProperty("AethernetShortcutIf", out var ni) ? ParseCondition(ni) : null,
            };
        }

        if (Str(e, "EnemySpawnType") is { } spawn)
            step.EnemySpawnType = Enum.TryParse<EnemySpawnType>(spawn, out var st) ? st : Paths.EnemySpawnType.Unknown;

        if (e.TryGetProperty("KillEnemyDataIds", out var kills) && kills.ValueKind == JsonValueKind.Array)
        {
            step.KillEnemyDataIds = [];
            foreach (var k in kills.EnumerateArray()) if (k.TryGetUInt32(out var id)) step.KillEnemyDataIds.Add(id);
        }

        if (e.TryGetProperty("ComplexCombatData", out var complex) && complex.ValueKind == JsonValueKind.Array)
        {
            step.KillEnemyDataIds ??= [];
            foreach (var c in complex.EnumerateArray())
            {
                if (U32(c, "DataId") is { } id) step.KillEnemyDataIds.Add(id);
                if (I32(c, "MinimumKillCount") is { } min) step.MinimumKillCount = Math.Max(step.MinimumKillCount ?? 0, min);
            }
        }

        foreach (var optionsName in new[] { "DutyOptions", "SinglePlayerDutyOptions" })
        {
            if (!e.TryGetProperty(optionsName, out var opts) || opts.ValueKind != JsonValueKind.Object)
                continue;
            step.DutyEnabled = Bool(opts, "Enabled");
            step.ContentFinderConditionId ??= U32(opts, "ContentFinderConditionId");
        }
        step.ContentFinderConditionId ??= U32(e, "ContentFinderConditionId");

        return step;
    }

    private static StepCondition? ParseCondition(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;

        var c = new StepCondition
        {
            InTerritory = U32List(e, "InTerritory"),
            NotInTerritory = U32List(e, "NotInTerritory"),
            QuestsCompleted = U16List(e, "QuestsCompleted"),
            QuestsAccepted = U16List(e, "QuestsAccepted"),
            Flying = Str(e, "Flying"),
            AetheryteUnlocked = Bool(e, "AetheryteUnlocked"),
            NotInInventory = U32(e, "NotInInventory"),
        };
        if (e.TryGetProperty("CompletionQuestVariablesFlags", out var flags))
            c.CompletionQuestVariablesFlags = ParseFlags(flags);
        return c;
    }

    /// <summary>
    /// The six-slot mask. Slots are a number (bits that must be set), null (don't care), or an
    /// object <c>{High, Low}</c> naming nibbles — folded into one byte mask <c>(High &lt;&lt; 4) | Low</c>.
    /// The nibble form's exact upstream semantics may be stricter than a bitmask; if field data ever
    /// disagrees, this is the line to revisit.
    /// </summary>
    private static byte?[]? ParseFlags(JsonElement flags)
    {
        if (flags.ValueKind != JsonValueKind.Array)
            return null;
        var result = new byte?[Quest.QuestSnapshot.VariableCount];
        var i = 0;
        foreach (var slot in flags.EnumerateArray())
        {
            if (i >= result.Length) break;
            result[i++] = slot.ValueKind switch
            {
                JsonValueKind.Number => (byte)(slot.GetInt32() & 0xFF),
                JsonValueKind.Object => (byte)(((I32(slot, "High") ?? 0) << 4) | (I32(slot, "Low") ?? 0)),
                _ => null,
            };
        }
        return result;
    }

    /// <summary>
    /// Six slots; each is null, a number (exact byte), an object <c>{High}/{Low}</c> (nibble), or an
    /// array of those (any-of). Normalised to a list per slot.
    /// </summary>
    private static List<VariableMatch>?[]? ParseRequired(JsonElement required)
    {
        var result = new List<VariableMatch>?[Quest.QuestSnapshot.VariableCount];
        var i = 0;
        var any = false;
        foreach (var slot in required.EnumerateArray())
        {
            if (i >= result.Length) break;
            List<VariableMatch>? list = null;
            void AddOne(JsonElement v)
            {
                var m = v.ValueKind switch
                {
                    JsonValueKind.Number => new VariableMatch((byte)(v.GetInt32() & 0xFF), null, null),
                    JsonValueKind.Object => new VariableMatch(null, I32(v, "High") is { } h ? (byte)h : null, I32(v, "Low") is { } l ? (byte)l : null),
                    _ => null,
                };
                if (m is null) return;
                list ??= [];
                list.Add(m);
            }
            if (slot.ValueKind == JsonValueKind.Array)
                foreach (var v in slot.EnumerateArray()) AddOne(v);
            else
                AddOne(slot);
            result[i++] = list;
            any |= list is not null;
        }
        return any ? result : null;
    }

    // ── JSON helpers ──

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static uint? U32(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var u) ? u : null;

    private static int? I32(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static float? F32(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;

    private static List<uint>? U32List(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<uint>();
        foreach (var x in v.EnumerateArray()) if (x.TryGetUInt32(out var u)) list.Add(u);
        return list;
    }

    private static List<ushort>? U16List(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        var list = new List<ushort>();
        foreach (var x in v.EnumerateArray()) if (x.TryGetUInt16(out var u)) list.Add(u);
        return list;
    }

    private static string ReadAll(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Hash(string json)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..16];
}
