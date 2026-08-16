using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace Odysseus.Windows;

/// <summary>
/// Draws game icons (society emblems and the like) inline.
///
/// <para>
/// Dalamud's shared textures are already cached and reference-counted, so this is a thin
/// convenience: ask for the icon, draw it at the row's text height, and fall back to blank space
/// when it is not loaded yet — never a missing-texture box, never a stall while it loads.
/// </para>
/// </summary>
public sealed class GameIcons
{
    private readonly ITextureProvider _textures;

    public GameIcons(ITextureProvider textures) => _textures = textures;

    /// <summary>Draw an icon square of <paramref name="size"/>; reserves the space either way so rows stay aligned.</summary>
    public void Draw(uint iconId, float size)
    {
        if (iconId != 0 && _textures.GetFromGameIcon(new GameIconLookup(iconId)).TryGetWrap(out var wrap, out _))
        {
            ImGui.Image(wrap.Handle, new Vector2(size, size));
            return;
        }
        ImGui.Dummy(new Vector2(size, size));
    }
}
