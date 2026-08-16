using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Odysseus.Services.Run;

namespace Odysseus.Windows;

/// <summary>
/// The step log: what ran, how long it took, and what failed — with the repeat offenders on top.
/// This is how a run's problems become a list to work through in the editor.
/// </summary>
public sealed class LogWindow : Window
{
    private readonly RunLog _log;
    private bool _failuresOnly;

    public LogWindow(RunLog log) : base("Odysseus Log##OdysseusLog")
    {
        _log = log;
        Size = new Vector2(760, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 240), MaximumSize = new Vector2(1600, 1000) };
    }

    public override void Draw()
    {
        var failures = _log.Failures();
        if (failures.Count > 0)
        {
            OdysseusTheme.SectionHeader($"REPEAT OFFENDERS ({failures.Count})", OdysseusTheme.StatusRed);
            if (ImGui.BeginTable("##fails", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("×", ImGuiTableColumnFlags.WidthFixed, 28f);
                ImGui.TableSetupColumn("Step", ImGuiTableColumnFlags.WidthStretch, 1.2f);
                ImGui.TableSetupColumn("Last reason", ImGuiTableColumnFlags.WidthStretch, 2f);
                foreach (var (example, count, reason) in failures.Take(8))
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn(); ImGui.TextColored(count > 1 ? OdysseusTheme.StatusRed : OdysseusTheme.TextSecondary, count.ToString());
                    ImGui.TableNextColumn(); ImGui.TextColored(OdysseusTheme.TextPrimary, example.Describe());
                    ImGui.TableNextColumn(); ImGui.TextColored(OdysseusTheme.TextSecondary, reason);
                }
                ImGui.EndTable();
            }
            if (ImGui.SmallButton("Copy failures"))
                ImGui.SetClipboardText(_log.FailuresText());
            ImGui.SameLine();
        }

        ImGui.Checkbox("Failures only", ref _failuresOnly);
        ImGui.SameLine();
        ImGui.TextColored(OdysseusTheme.TextSecondary, $"{_log.Count} steps this session");
        ImGui.SameLine();
        if (ImGui.SmallButton("Clear"))
            _log.Clear();

        OdysseusTheme.SectionHeader("STEPS (NEWEST FIRST)");
        if (!ImGui.BeginTable("##steps", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 62f);
        ImGui.TableSetupColumn("Quest", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Seq/step", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Outcome", ImGuiTableColumnFlags.WidthFixed, 66f);
        ImGui.TableSetupColumn("s", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn("Reason / phase", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableHeadersRow();

        foreach (var r in _log.Recent)
        {
            if (_failuresOnly && r.Outcome != "Failed")
                continue;
            var color = r.Outcome switch
            {
                "Failed" => OdysseusTheme.StatusRed,
                "Skipped" => OdysseusTheme.TextDisabled,
                "Cancelled" => OdysseusTheme.StatusYellow,
                _ => OdysseusTheme.TextPrimary,
            };
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); ImGui.TextColored(OdysseusTheme.TextSecondary, r.UtcStart.ToLocalTime().ToString("HH:mm:ss"));
            ImGui.TableNextColumn(); ImGui.TextColored(OdysseusTheme.TextPrimary, r.QuestName);
            ImGui.TableNextColumn(); ImGui.TextColored(OdysseusTheme.TextSecondary, $"{r.Sequence} / {r.StepIndex + 1}");
            ImGui.TableNextColumn(); ImGui.TextColored(OdysseusTheme.TextPrimary, r.Kind + (r.DataId is { } d ? $" {d}" : ""));
            ImGui.TableNextColumn(); ImGui.TextColored(color, r.Outcome);
            ImGui.TableNextColumn(); ImGui.TextColored(OdysseusTheme.TextSecondary, r.Seconds.ToString("F1"));
            ImGui.TableNextColumn(); ImGui.TextColored(r.Outcome == "Failed" ? OdysseusTheme.StatusRed : OdysseusTheme.TextDisabled, r.Reason ?? r.Phase ?? string.Empty);
        }
        ImGui.EndTable();
    }
}
