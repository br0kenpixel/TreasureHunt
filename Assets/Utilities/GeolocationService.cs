using System;
using UnityEngine;
using UnityEngine.Android;

public sealed record Location
{
    public float Latitude;
    public float Longitude;
    public float AccuracyMeters;
}

public sealed class GeolocationService
{
    private const float DESIRED_ACCURACY_METERS = 1;
    private const float UPDATE_DISTANCE_METERS = 0.5f;

    public GeolocationService()
    {
        if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
        {
            Debug.LogError("Fine location access not granted");
            throw new Exception("Fine location access not granted");
        }

        if (!Input.location.isEnabledByUser)
        {
            Debug.LogError("Location services are turned off on the device");
            throw new Exception("Location services are turned off on the device");
        }

        Resume();
    }

    public LocationServiceStatus GetStatus()
    {
        return Input.location.status;
    }

    public Location GetLocation()
    {
        LocationInfo d = Input.location.lastData;

        return new Location()
        {
            Latitude = d.latitude,
            Longitude = d.longitude,
            AccuracyMeters = d.horizontalAccuracy
        };
    }

    public void Resume()
    {
        Input.location.Start(desiredAccuracyInMeters: DESIRED_ACCURACY_METERS, updateDistanceInMeters: UPDATE_DISTANCE_METERS);
    }

    public void Shutdown()
    {
        Input.location.Stop();
    }
}
