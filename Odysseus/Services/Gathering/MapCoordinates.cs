namespace Odysseus.Services.Gathering;

/// <summary>
/// The game's map coordinates, converted to world coordinates.
///
/// <para>
/// Fishing spots and spearfishing holes are recorded in map pixels, not world units — the numbers
/// behind the X and Y a map link shows. The conversion is the documented one
/// (<c>ffxiv-datamining/docs/MapCoordinates.md</c>, the same maths Dalamud's <c>MapLinkPayload</c>
/// uses): a fixed factor to normalise the pixel scale, then the map's own size factor and offsets.
/// </para>
///
/// <para>
/// Height is not recorded at all, which is why a fishing destination is a point to stand near
/// rather than a point to stand on.
/// </para>
/// </summary>
public static class MapCoordinates
{
    /// <summary>2048 map pixels across 50 units of 41 — the constant behind the published formula.</summary>
    private const float PixelFactor = 2048.0f / (50 * 41);

    /// <param name="sizeFactor">The map's <c>SizeFactor</c>; 100 when unknown, which is the identity.</param>
    /// <param name="offset">The map's <c>OffsetX</c> or <c>OffsetY</c>, in thousandths.</param>
    public static float ToWorld(float coordinate, ushort sizeFactor, short offset)
    {
        var scale = (sizeFactor == 0 ? 100 : sizeFactor) * 0.01f;
        return (coordinate * PixelFactor - 1024f) / scale - offset * 0.001f;
    }
}
