using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Quest;

/// <summary>Resolves the path data's dialogue text keys to on-screen text.</summary>
public interface IDialogueTexts
{
    /// <summary>The localized text for a key such as <c>TEXT_STMBDF104_03182_A1_000_001</c>, or null.</summary>
    string? Resolve(ushort questId, string key);
}

/// <summary>
/// The quest dialogue sheets, read on demand.
///
/// <para>
/// Every quest has its own two-column sheet at <c>quest/{first three digits of its number}/{Quest.Id}</c>
/// (verified 2026-08-15: 3182 → <c>quest/031/StmBdf104_03182</c>, 126 rows). The path data
/// carries the <b>keys</b>, never the words, so a <c>List</c> dialogue choice is answered by
/// resolving the answer key here and picking the menu entry with that text — in whatever language
/// the client runs. Sheets are cached per quest; a missing sheet resolves to null and the step
/// says so rather than guessing an index.
/// </para>
/// </summary>
public sealed class DialogueCatalog : IDialogueTexts
{
    private readonly IDataManager _data;
    private readonly Action<string> _log;
    private readonly Dictionary<ushort, Dictionary<string, string>?> _sheets = new();

    public DialogueCatalog(IDataManager data, Action<string> log)
    {
        _data = data;
        _log = log;
    }

    public string? Resolve(ushort questId, string key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        var sheet = SheetFor(questId);
        return sheet is not null && sheet.TryGetValue(key, out var text) ? text : null;
    }

    private Dictionary<string, string>? SheetFor(ushort questId)
    {
        if (_sheets.TryGetValue(questId, out var cached))
            return cached;

        Dictionary<string, string>? result = null;
        try
        {
            var quest = _data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(QuestCatalog.RowIdBase + questId);
            var internalId = quest?.Id.ExtractText();
            if (!string.IsNullOrEmpty(internalId))
            {
                var underscore = internalId.LastIndexOf('_');
                var number = underscore >= 0 ? internalId[(underscore + 1)..] : string.Empty;
                if (number.Length >= 3)
                {
                    var name = $"quest/{number[..3]}/{internalId}";
                    var sheet = _data.GetExcelSheet<RawRow>(name: name);
                    if (sheet is not null)
                    {
                        result = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (var row in sheet)
                        {
                            var k = row.ReadStringColumn(0).ExtractText();
                            if (k.Length > 0)
                                result[k] = row.ReadStringColumn(1).ExtractText();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Dialogue sheet for quest {questId} unavailable: {ex.GetType().Name}: {ex.Message}");
        }

        _sheets[questId] = result;
        return result;
    }
}
