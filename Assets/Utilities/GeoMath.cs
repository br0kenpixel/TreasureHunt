using System;

/// <summary>Distances and offsets between latitude/longitude points on the Earth's surface.</summary>
public static class GeoMath
{
    public const double EarthRadiusMeters = 6371008.8;

    /// <summary>Straight-line distance over the Earth's surface between two points, in meters.</summary>
    public static double DistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        // Haversine formula.
        double dLat = ToRadians(latitude2 - latitude1);
        double dLon = ToRadians(longitude2 - longitude1);
        double sinLat = Math.Sin(dLat / 2.0);
        double sinLon = Math.Sin(dLon / 2.0);
        double a = sinLat * sinLat +
                   Math.Cos(ToRadians(latitude1)) * Math.Cos(ToRadians(latitude2)) * sinLon * sinLon;
        return 2.0 * EarthRadiusMeters * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>
    /// The point reached by travelling <paramref name="distanceMeters"/> from a start point
    /// in a compass direction (0° = north, 90° = east, 180° = south, 270° = west).
    /// </summary>
    public static (double latitude, double longitude) Offset(
        double latitude, double longitude, double distanceMeters, double bearingDegrees)
    {
        double lat1 = ToRadians(latitude);
        double lon1 = ToRadians(longitude);
        double bearing = ToRadians(bearingDegrees);
        double angle = distanceMeters / EarthRadiusMeters;

        double lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(angle) +
                                Math.Cos(lat1) * Math.Sin(angle) * Math.Cos(bearing));
        double lon2 = lon1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(angle) * Math.Cos(lat1),
                                        Math.Cos(angle) - Math.Sin(lat1) * Math.Sin(lat2));

        double newLongitude = (ToDegrees(lon2) + 540.0) % 360.0 - 180.0; // keep within -180..180
        return (ToDegrees(lat2), newLongitude);
    }

    static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
    static double ToDegrees(double radians) => radians * 180.0 / Math.PI;
}
