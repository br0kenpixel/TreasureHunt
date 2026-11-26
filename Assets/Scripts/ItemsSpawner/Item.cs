using UnityEngine;

public partial class Item
{
    private const double EARTH_RADIUS_METERS = 6371008.8;

    public string Name { get; protected set; }
    public Location Location { get; protected set; }

    /// <summary>
    /// Offset in meters from <paramref name="location"/> to this item:
    /// x = east (+) / west (-), y = north (+) / south (-).
    /// Use <c>.magnitude</c> for the straight-line distance.
    /// Uses an equirectangular approximation, which is accurate for the short
    /// distances (up to a few km) relevant to item pickup.
    /// </summary>
    public Vector2 DistanceFrom(Location location)
    {
        double lat1 = location.Latitude * Mathf.Deg2Rad;
        double lat2 = Location.Latitude * Mathf.Deg2Rad;

        double deltaLonDeg = Location.Longitude - location.Longitude;
        // Wrap to [-180, 180] so points across the antimeridian stay close
        deltaLonDeg = ((deltaLonDeg + 540.0) % 360.0) - 180.0;

        double deltaLat = lat2 - lat1;
        double deltaLon = deltaLonDeg * Mathf.Deg2Rad;
        double meanLat = (lat1 + lat2) / 2.0;

        double east = deltaLon * System.Math.Cos(meanLat) * EARTH_RADIUS_METERS;
        double north = deltaLat * EARTH_RADIUS_METERS;

        return new Vector2((float)east, (float)north);
    }

    /// <summary>
    /// Great-circle distance in meters from <paramref name="location"/> to this item
    /// (haversine formula).
    /// </summary>
    public float DistanceFromMeters(Location location)
    {
        double lat1 = location.Latitude * Mathf.Deg2Rad;
        double lat2 = Location.Latitude * Mathf.Deg2Rad;
        double deltaLat = lat2 - lat1;
        double deltaLon = (Location.Longitude - location.Longitude) * Mathf.Deg2Rad;

        double sinLat = System.Math.Sin(deltaLat / 2.0);
        double sinLon = System.Math.Sin(deltaLon / 2.0);
        double a = sinLat * sinLat + System.Math.Cos(lat1) * System.Math.Cos(lat2) * sinLon * sinLon;
        double c = 2.0 * System.Math.Asin(System.Math.Min(1.0, System.Math.Sqrt(a)));

        return (float)(EARTH_RADIUS_METERS * c);
    }

    public override string ToString()
    {
        return $"Item named '{Name}' at '{Location}'";
    }
}