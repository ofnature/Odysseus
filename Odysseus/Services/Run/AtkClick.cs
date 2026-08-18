using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Odysseus.Services.Run;

/// <summary>
/// Clicks addon buttons and checkboxes the way ECommons' ClickHelper does in production: a
/// <b>fresh, zeroed</b> <c>AtkEvent</c> carrying only Target (the component's node) and Listener
/// (the addon), plus a zeroed <c>AtkEventData</c>, handed to the addon's <c>ReceiveEvent</c> with
/// the type and param read off the node's own registered event.
///
/// <para>
/// Ported from Charon, where two crash traps in this exact mechanism were paid for with a live
/// client crash inside <c>AddonReconstructionBox.ReceiveEvent+0x247</c>: do <b>not</b> replay the
/// node's own live <c>AtkEvent</c> object, and do <b>not</b> omit the <c>AtkEventData</c> — the
/// handler dereferences both. Odysseus had been replaying the live event on the quest reward
/// window; it happened not to crash there, which is not the same as being safe.
/// </para>
/// </summary>
public static unsafe class AtkClick
{
    /// <summary>Click a button — a real click as far as the addon knows.</summary>
    public static bool Button(AtkUnitBase* addon, AtkComponentButton* button)
        => addon != null && button != null && Click(addon, button->AtkComponentBase.OwnerNode);

    /// <summary>Same for a checkbox, then mark it checked.</summary>
    public static bool CheckBox(AtkUnitBase* addon, AtkComponentCheckBox* checkbox)
    {
        if (addon == null || checkbox == null)
            return false;
        if (!Click(addon, checkbox->AtkComponentButton.AtkComponentBase.OwnerNode))
            return false;
        checkbox->IsChecked = true;
        return true;
    }

    /// <summary>
    /// ECommons' <c>SelectYesno.Yes()</c> move: a button still greyed because a checkbox gates it
    /// — and the gate is UI-only — is force-enabled by flipping NodeFlags bit 5 before clicking.
    /// </summary>
    public static void ForceEnable(AtkComponentButton* button)
    {
        if (button == null || button->IsEnabled)
            return;
        var node = button->AtkComponentBase.OwnerNode;
        if (node == null)
            return;
        var flags = (ushort*)&node->AtkResNode.NodeFlags;
        *flags ^= 1 << 5;
    }

    private static bool Click(AtkUnitBase* addon, AtkComponentNode* node)
    {
        if (node == null)
            return false;

        // The node's registered event supplies the true type and param; everything else is fresh.
        var registered = node->AtkResNode.AtkEventManager.Event;
        if (registered == null)
            return false;

        var evt = default(AtkEvent);
        evt.Target = (AtkEventTarget*)node;
        evt.Listener = (AtkEventListener*)addon;
        var eventData = default(AtkEventData);

        addon->ReceiveEvent(registered->State.EventType, (int)registered->Param, &evt, &eventData);
        return true;
    }
}
