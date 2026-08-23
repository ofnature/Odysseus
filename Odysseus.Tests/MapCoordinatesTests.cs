using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

public class MapCoordinatesTests
{
    private const float Middle = 1024f * (50 * 41) / 2048f;

    [Fact]
    public void The_map_centre_is_the_world_origin()
    {
        Assert.Equal(0f, MapCoordinates.ToWorld(Middle, 100, 0), 3);
    }

    [Fact]
    public void Size_factor_scales_and_offset_shifts()
    {
        // A map at twice the size covers twice the world per pixel, so the same pixel is half as far.
        var atHundred = MapCoordinates.ToWorld(Middle + 100f, 100, 0);
        var atTwoHundred = MapCoordinates.ToWorld(Middle + 100f, 200, 0);
        Assert.Equal(atHundred / 2f, atTwoHundred, 3);

        // The offset is in thousandths and moves the whole map.
        Assert.Equal(atHundred - 0.5f, MapCoordinates.ToWorld(Middle + 100f, 100, 500), 3);
    }

    [Fact]
    public void A_missing_size_factor_is_treated_as_unscaled()
    {
        Assert.Equal(MapCoordinates.ToWorld(1200f, 100, 0), MapCoordinates.ToWorld(1200f, 0, 0), 3);
    }
}
