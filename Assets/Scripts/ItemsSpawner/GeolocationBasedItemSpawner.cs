using System;
using System.Collections.Generic;

public class GeolocationBasedItemSpawner : IItemSpawner
{
    private const int MAX_SPAWN_DISTANCE_METERS = 50;
    private const int MIN_SPAWN_DISTANCE_METERS = 10;
    private const double EARTH_RADIUS_METERS = 6378137.0;
    private const int MAX_ATTEMPTS_PER_ITEM = 30;

    private Random random;
    private GeolocationService geoSvc;
    private float minItemSeparationMeters;

    public GeolocationBasedItemSpawner(GeolocationService geoSvc, float minItemSeparationMeters)
    {
        if (minItemSeparationMeters < 0)
            throw new ArgumentOutOfRangeException(nameof(minItemSeparationMeters), "Separation cannot be negative");

        this.geoSvc = geoSvc;
        this.minItemSeparationMeters = minItemSeparationMeters;
        random = new();
    }

    public List<Item> SpawnItems(int count)
    {
        var location = geoSvc.GetLocation();
        var offsets = new List<(double north, double east)>(count);

        double latRad = location.Latitude * Math.PI / 180.0;
        double minSq = MIN_SPAWN_DISTANCE_METERS * MIN_SPAWN_DISTANCE_METERS;
        double maxSq = MAX_SPAWN_DISTANCE_METERS * MAX_SPAWN_DISTANCE_METERS;
        double sepSq = (double)minItemSeparationMeters * minItemSeparationMeters;

        int attempts = 0;
        int maxAttempts = count * MAX_ATTEMPTS_PER_ITEM;

        // Rejection sampling: draw candidates in local meter offsets and
        // discard any that land too close to an already accepted item.
        while (offsets.Count < count && attempts < maxAttempts)
        {
            attempts++;

            // Spread points evenly over the ring between min and max
            double distance = Math.Sqrt(minSq + random.NextDouble() * (maxSq - minSq));
            double bearing = random.NextDouble() * 2.0 * Math.PI;

            double north = distance * Math.Cos(bearing);
            double east = distance * Math.Sin(bearing);

            bool tooClose = false;
            foreach (var (n, e) in offsets)
            {
                double dn = north - n;
                double de = east - e;
                if (dn * dn + de * de < sepSq)
                {
                    tooClose = true;
                    break;
                }
            }

            if (!tooClose)
                offsets.Add((north, east));
        }

        if (offsets.Count < count)
        {
            UnityEngine.Debug.LogWarning(
                $"Only placed {offsets.Count}/{count} items with {minItemSeparationMeters} m separation");
        }

        var items = new List<Item>(offsets.Count);

        foreach (var (north, east) in offsets)
        {
            double dLat = north / EARTH_RADIUS_METERS * (180.0 / Math.PI);
            double dLon = east / (EARTH_RADIUS_METERS * Math.Cos(latRad)) * (180.0 / Math.PI);

            var place = new Location
            {
                Latitude = (float)(location.Latitude + dLat),
                Longitude = (float)(location.Longitude + dLon),
                AccuracyMeters = 0
            };

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
