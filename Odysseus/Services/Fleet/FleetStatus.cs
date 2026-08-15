using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odysseus.Services.Fleet;

/// <summary>
/// One box's line on the dashboard. What every Odysseus publishes on the relay every ~2s;
/// nothing here is time-critical.
///
/// <para>
/// <b>Extend-only.</b> New fields may be added; existing ones never change meaning or type. Older
/// peers ignore what they do not know (<see cref="JsonSerializerOptions"/> below is lenient), so
/// a fleet on mixed versions still draws every row. Inherited from <c>docs/lan-ipc-plan.md</c>.
/// </para>
/// </summary>
public sealed class FleetStatus
{
    /// <summary>Stable per-character key: <c>Name@World</c>.</summary>
    public string SenderId { get; set; } = string.Empty;
    public string Character { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public int Level { get; set; }
    public ushort QuestId { get; set; }
    public string QuestName { get; set; } = string.Empty;
    public int Sequence { get; set; }
    /// <summary>The run state name (Idle, Step, Travel, Combat, Handoff, Advance, Faulted…).</summary>
    public string State { get; set; } = "Idle";
    /// <summary>The run window's status line, so a fault reason travels with the row.</summary>
    public string StatusLine { get; set; } = string.Empty;
    /// <summary>Sender's clock, unix ms. Used for ordering only; staleness is judged by receipt time.</summary>
    public long SentUnixMs { get; set; }
    /// <summary>Schema marker for the extend-only rule.</summary>
    public int Version { get; set; } = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        // Unknown fields from a newer sender are simply dropped.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null on anything unparseable — a bad frame is dropped, never thrown.</summary>
    public static FleetStatus? FromJson(string json)
    {
        try
        {
            var s = JsonSerializer.Deserialize<FleetStatus>(json, Json);
            return s is { SenderId.Length: > 0 } ? s : null;
        }
        catch
        {
            return null;
        }
    }
}
