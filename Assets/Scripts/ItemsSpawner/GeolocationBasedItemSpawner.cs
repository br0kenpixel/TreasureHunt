using System;
using System.Collections.Generic;

public class GeolocationBasedItemSpawner : IItemSpawner
{
    private const int MAX_SPAWN_DISTANCE_METERS = 50;
    private const int MIN_SPAWN_DISTANCE_METERS = 10;
    private const double EARTH_RADIUS_METERS = 6378137.0;

    private Random random;
    private GeolocationService geoSvc;

    public GeolocationBasedItemSpawner(GeolocationService geoSvc)
    {
        this.geoSvc = geoSvc;
        random = new();
    }

    public List<Item> SpawnItems(int count)
    {
        var location = geoSvc.GetLocation();
        var points = new List<Location>(count);

        double latRad = location.Latitude * Math.PI / 180.0;
        double minSq = MIN_SPAWN_DISTANCE_METERS * MIN_SPAWN_DISTANCE_METERS;
        double maxSq = MAX_SPAWN_DISTANCE_METERS * MAX_SPAWN_DISTANCE_METERS;

        for (int i = 0; i < count; i++)
        {
            // Spread points evenly over the ring between min and max

            double distance = Math.Sqrt(minSq + random.NextDouble() * (maxSq - minSq));
            double bearing = random.NextDouble() * 2.0 * Math.PI;

            double north = distance * Math.Cos(bearing);
            double east = distance * Math.Sin(bearing);

            double dLat = north / EARTH_RADIUS_METERS * (180.0 / Math.PI);
            double dLon = east / (EARTH_RADIUS_METERS * Math.Cos(latRad)) * (180.0 / Math.PI);

            points.Add(new Location
            {
                Latitude = (float)(location.Latitude + dLat),
                Longitude = (float)(location.Longitude + dLon),
                AccuracyMeters = 0
            });
        }

        var items = new List<Item>(count);

        foreach (var place in points)
        {
            var type = random.Next(0, 2 /* exclusive */);

            switch (type)
            {
                case 0:
                    items.Add(new KeyItem(place));
                    break;
                case 1:
                    items.Add(new ChestItem(place));
                    break;
                default:
                    throw new Exception("Invalid number was generated");
            }
        }

        return items;
    }
}