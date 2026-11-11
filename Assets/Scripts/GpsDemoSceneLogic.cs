using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

public class GpsDemoSceneLogic : MonoBehaviour
{
    public TMP_Text debugText;
    public MapView mapView;

    private GeolocationService geoSvc;

    void Start()
    {
        debugText.SetText("Starting...");
        if (mapView == null)
            mapView = FindAnyObjectByType<MapView>();
        geoSvc = new();
    }

    // Update is called once per frame
    void Update()
    {
        switch (geoSvc.GetStatus())
        {
            case LocationServiceStatus.Failed:
                debugText.SetText("Failed");
                break;
            case LocationServiceStatus.Initializing:
                debugText.SetText("Initializing...");
                break;
            case LocationServiceStatus.Running:
                var location = geoSvc.GetLocation();
                debugText.SetText($"Running:\nlat={location.Latitude}°,\nlon={location.Longitude}°,\nacc=±{location.AccuracyMeters}m");
                if (mapView != null)
                    mapView.SetPlayerLocation(location.Latitude, location.Longitude);
                break;
            case LocationServiceStatus.Stopped:
                debugText.SetText("Stopped");
                break;
        }
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
