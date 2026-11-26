using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

public class GpsDemoSceneLogic : MonoBehaviour
{
    public TMP_Text debugText;
    public MapView mapView;

    public int randomPointCount = 5;
    public float randomPointMinMeters = 30f;
    public float randomPointMaxMeters = 150f;

    private GeolocationService geoSvc;
    private IItemSpawner itemSpawner;
    private List<Item> items;

    void Start()
    {
        debugText.SetText("Starting...");

        if (mapView == null)
            mapView = FindAnyObjectByType<MapView>();

        geoSvc = new();
        itemSpawner = new GeolocationBasedItemSpawner(geoSvc);

        Invoke(nameof(GenerateMapPoints), 10f);
    }

    void Update()
    {
        var location = geoSvc.GetLocation();

        switch (geoSvc.GetStatus())
        {
            case LocationServiceStatus.Failed:
                debugText.SetText("Failed");
                break;
            case LocationServiceStatus.Initializing:
                debugText.SetText("Initializing...");
                break;
            case LocationServiceStatus.Running:
                debugText.SetText($"Running:\nlat={location.Latitude}°,\nlon={location.Longitude}°,\nacc=±{location.AccuracyMeters}m");
                if (mapView != null)
                    mapView.SetPlayerLocation(location.Latitude, location.Longitude);
                if (items is not null && items.Count > 0)
                {
                    debugText.text += "\nItems:";
                    items.ForEach(i =>
                    {
                        debugText.text += $"\n- {i} ({i.DistanceFromMeters(location):F0}m away)";
                    });
                }
                break;
            case LocationServiceStatus.Stopped:
                debugText.SetText("Stopped");
                break;
        }

        foreach (var item in items)
        {
            if (item.DistanceFromMeters(location) < 10)
            {
                mapView.RemoveMarker(item.Location);
                items.Remove(item);
            }
        }
    }

    private void GenerateMapPoints()
    {
        items = itemSpawner.SpawnItems(6);
        items.ForEach(i =>
        {
            mapView.AddMarker(i.Location.Latitude, i.Location.Longitude);
        });
    }

    public void ResetMapView()
    {
        mapView.ResetView();
    }

    void OnDestroy()
    {
        geoSvc.Shutdown();
    }

    void OnApplicationPause(bool pause)
    {
        if (pause)
            geoSvc.Shutdown();
        else
            geoSvc.Resume();
    }

    public void StopAndExit()
    {
        geoSvc.Shutdown();
        Application.Quit();
    }
}
