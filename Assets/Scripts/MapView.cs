using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// Top-down street map built from MapTiler raster tiles, centered on a latitude/longitude.
/// Put this on a UI object under a Canvas; it creates its tiles, player marker and
/// attribution label at runtime. Call <see cref="SetCenter"/> whenever the GPS position changes.
/// The map fills whatever size its RectTransform has (a small corner minimap or the whole
/// screen) and adapts if that size changes at runtime.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class MapView : MonoBehaviour
{
    // The key is read from Assets/Resources/MapTilerKey.txt, which is ignored by git.
    const string KeyResourceName = "MapTilerKey";
    const string KeyPlaceholder = "PASTE_YOUR_MAPTILER_KEY_HERE";
    const int MaxCachedTiles = 100;
    static readonly Vector3Int NoTile = new(-1, -1, -1);
    static readonly Color LoadingColor = new(0.9f, 0.9f, 0.88f);

    [Tooltip("Raster tile URL. {z}, {x}, {y} and {key} are filled in at runtime.")]
    public string urlTemplate = "https://api.maptiler.com/maps/streets-v2/{z}/{x}/{y}.png?key={key}";

    [Tooltip("16 = neighbourhood, 17 = a few streets, 18 = individual buildings")]
    [Range(3, 20)] public int zoom = 17;

    [Tooltip("Size of one tile on screen, in canvas units")]
    [Min(32)] public float tileDisplaySize = 384f;

    public Color playerMarkerColor = new(0.9f, 0.15f, 0.15f);

    [Tooltip("Width/height of the player marker, in canvas units")]
    [Min(4)] public float playerMarkerSize = 24f;

    [Header("Editor preview (GPS doesn't work in the Editor)")]
    public bool previewInEditor = true;
    public double previewLatitude = 48.8584;
    public double previewLongitude = 2.2945;

    string apiKey;
    bool ready;
    RectTransform rect;
    RectTransform tileLayer;

    double centerLat, centerLon;
    bool hasCenter;

    // One RawImage per grid cell, and the tile (x, y, zoom) each cell is showing.
    readonly List<RawImage> slots = new();
    readonly List<Vector3Int> slotTiles = new();
    int halfCols = -1, halfRows = -1;
    float builtTileSize;
    Vector2Int centerTile;
    int centerZoom = -1;

    readonly Dictionary<Vector3Int, Texture2D> cache = new();
    readonly HashSet<Vector3Int> loading = new();
    bool loggedDownloadError;

    /// <summary>Moves the map so this latitude/longitude is in the middle, under the player marker.</summary>
    public void SetCenter(double latitude, double longitude)
    {
        centerLat = latitude;
        centerLon = longitude;
        hasCenter = true;
    }

    void Awake()
    {
        rect = (RectTransform)transform;
        if (!TryGetComponent<RectMask2D>(out _))
            gameObject.AddComponent<RectMask2D>();

        tileLayer = CreateChild("Tiles", transform);
        tileLayer.SetAsFirstSibling();
        CreatePlayerMarker();
        CreateAttribution();

        var keyAsset = Resources.Load<TextAsset>(KeyResourceName);
        apiKey = keyAsset != null ? keyAsset.text.Trim() : "";
        ready = apiKey != "" && apiKey != KeyPlaceholder;
        if (!ready)
            Debug.LogError("MapView: no MapTiler API key. Paste it into Assets/Resources/MapTilerKey.txt.");
    }

    void Start()
    {
#if UNITY_EDITOR
        if (previewInEditor && !hasCenter)
            SetCenter(previewLatitude, previewLongitude);
#endif
    }

    void LateUpdate()
    {
        if (!ready || !hasCenter)
            return;

        var (fx, fy) = MapTiles.LatLonToTile(centerLat, centerLon, zoom);
        var center = new Vector2Int((int)System.Math.Floor(fx), (int)System.Math.Floor(fy));

        // Enough tiles to cover this RectTransform, wherever inside the center tile the player is.
        int cols = Mathf.CeilToInt(rect.rect.width / (2f * tileDisplaySize));
        int rows = Mathf.CeilToInt(rect.rect.height / (2f * tileDisplaySize));
        if (cols != halfCols || rows != halfRows || !Mathf.Approximately(tileDisplaySize, builtTileSize))
        {
            halfCols = cols;
            halfRows = rows;
            builtTileSize = tileDisplaySize;
            RebuildSlots();
            centerZoom = -1;
        }

        if (center != centerTile || zoom != centerZoom)
        {
            centerTile = center;
            centerZoom = zoom;
            AssignTiles();
        }

        // Shift the whole grid so the player's exact position sits at the middle of the map.
        tileLayer.anchoredPosition = new Vector2(
            (float)(-(fx - center.x) * tileDisplaySize),
            (float)((fy - center.y) * tileDisplaySize));
    }

    void RebuildSlots()
    {
        foreach (var slot in slots)
            Destroy(slot.gameObject);
        slots.Clear();
        slotTiles.Clear();

        for (int dy = -halfRows; dy <= halfRows; dy++)
        for (int dx = -halfCols; dx <= halfCols; dx++)
        {
            var rt = CreateChild($"Tile {dx},{dy}", tileLayer);
            rt.pivot = new Vector2(0f, 1f); // top-left, like tile coordinates
            rt.sizeDelta = new Vector2(tileDisplaySize, tileDisplaySize);
            rt.anchoredPosition = new Vector2(dx * tileDisplaySize, -dy * tileDisplaySize);

            var image = rt.gameObject.AddComponent<RawImage>();
            image.raycastTarget = false;
            slots.Add(image);
            slotTiles.Add(NoTile);
        }
    }

    void AssignTiles()
    {
        int n = 1 << zoom;
        int i = 0;
        for (int dy = -halfRows; dy <= halfRows; dy++)
        for (int dx = -halfCols; dx <= halfCols; dx++, i++)
        {
            var image = slots[i];
            int y = centerTile.y + dy;
            if (y < 0 || y >= n)
            {
                // Above the top or below the bottom edge of the world map.
                image.enabled = false;
                slotTiles[i] = NoTile;
                continue;
            }

            int x = ((centerTile.x + dx) % n + n) % n; // wrap around at longitude ±180
            var tile = new Vector3Int(x, y, zoom);
            slotTiles[i] = tile;
            image.enabled = true;

            if (cache.TryGetValue(tile, out var texture))
            {
                image.texture = texture;
                image.color = Color.white;
            }
            else
            {
                image.texture = null;
                image.color = LoadingColor;
                if (loading.Add(tile))
                    StartCoroutine(Download(tile));
            }
        }

        TrimCache();
    }

    IEnumerator Download(Vector3Int tile)
    {
        string url = urlTemplate
            .Replace("{z}", tile.z.ToString())
            .Replace("{x}", tile.x.ToString())
            .Replace("{y}", tile.y.ToString())
            .Replace("{key}", apiKey);

        using var request = UnityWebRequestTexture.GetTexture(url);
        yield return request.SendWebRequest();
        loading.Remove(tile);

        if (request.result != UnityWebRequest.Result.Success)
        {
            // Log once (and never the URL, since it contains the key).
            if (!loggedDownloadError)
            {
                loggedDownloadError = true;
                Debug.LogError($"MapView: tile download failed ({request.responseCode} {request.error}). " +
                               "403 usually means the MapTiler key is wrong or restricted.");
            }
            yield break;
        }

        var texture = DownloadHandlerTexture.GetContent(request);
        texture.wrapMode = TextureWrapMode.Clamp;
        cache[tile] = texture;

        for (int i = 0; i < slots.Count; i++)
        {
            if (slotTiles[i] == tile)
            {
                slots[i].texture = texture;
                slots[i].color = Color.white;
            }
        }
    }

    void TrimCache()
    {
        if (cache.Count <= MaxCachedTiles)
            return;

        var visible = new HashSet<Vector3Int>(slotTiles);
        var evict = new List<Vector3Int>();
        foreach (var tile in cache.Keys)
        {
            if (cache.Count - evict.Count <= MaxCachedTiles)
                break;
            if (!visible.Contains(tile))
                evict.Add(tile);
        }

        foreach (var tile in evict)
        {
            Destroy(cache[tile]);
            cache.Remove(tile);
        }
    }

    void OnDestroy()
    {
        foreach (var texture in cache.Values)
            Destroy(texture);
        cache.Clear();
    }

    void CreatePlayerMarker()
    {
        var rt = CreateChild("Player Marker", transform);
        rt.sizeDelta = new Vector2(playerMarkerSize, playerMarkerSize);
        rt.localRotation = Quaternion.Euler(0f, 0f, 45f);

        var image = rt.gameObject.AddComponent<Image>();
        image.color = playerMarkerColor;
        image.raycastTarget = false;

        var outline = rt.gameObject.AddComponent<Outline>();
        outline.effectColor = Color.white;
        outline.effectDistance = new Vector2(3f, 3f);
    }

    void CreateAttribution()
    {
        // MapTiler's terms require this credit to be visible on the map.
        // It spans the bottom edge of the map and shrinks its font so it still fits on a small minimap.
        var rt = CreateChild("Attribution", transform);
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0f);
        rt.anchoredPosition = new Vector2(-4f, 3f);
        rt.sizeDelta = new Vector2(-8f, 20f); // map width minus 4 units of padding on each side

        var text = rt.gameObject.AddComponent<TextMeshProUGUI>();
        text.text = "<mark=#FFFFFFC0> © MapTiler © OpenStreetMap contributors </mark>";
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.enableAutoSizing = true;
        text.fontSizeMin = 6f;
        text.fontSizeMax = 14f;
        text.color = new Color(0.2f, 0.2f, 0.2f);
        text.alignment = TextAlignmentOptions.BottomRight;
        text.raycastTarget = false;
    }

    static RectTransform CreateChild(string name, Transform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        return rt;
    }
}
