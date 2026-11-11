using System;

/// <summary>
/// Web Mercator "slippy map" tile math: the z/x/y tile scheme used by MapTiler,
/// OpenStreetMap, Google and most other web map providers.
/// </summary>
public static class MapTiles
{
    /// <summary>Web Mercator can't show the poles; latitudes are clamped to this.</summary>
    public const double MaxLatitude = 85.05112878;

    /// <summary>
    /// Position on the whole-world map, from 0 to 1 on each axis:
    /// x goes west (-180°) to east (+180°), y goes north (≈85°) to south (≈-85°).
    /// Multiply by 2^zoom to get tile coordinates.
    /// </summary>
    public static (double x, double y) LatLonToWorld(double latitude, double longitude)
    {
        double lat = Math.Max(-MaxLatitude, Math.Min(MaxLatitude, latitude)) * Math.PI / 180.0;
        double x = (longitude + 180.0) / 360.0;
        double y = (1.0 - Math.Log(Math.Tan(lat) + 1.0 / Math.Cos(lat)) / Math.PI) / 2.0;
        return (x, y);
    }

    /// <summary>
    /// Fractional tile coordinates of a latitude/longitude at a zoom level.
    /// The integer part is the tile index; the fraction is the position inside that tile
    /// (0,0 = top-left corner, 1,1 = bottom-right corner).
    /// </summary>
    public static (double x, double y) LatLonToTile(double latitude, double longitude, int zoom)
    {
        var (x, y) = LatLonToWorld(latitude, longitude);
        double n = 1 << zoom;
        return (x * n, y * n);
    }
}
