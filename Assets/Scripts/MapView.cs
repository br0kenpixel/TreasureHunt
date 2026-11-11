using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;

/// <summary>
/// Top-down street map built from MapTiler raster tiles.
/// Put this on a UI object under a Canvas; it creates its tiles, player marker and
/// attribution label at runtime. Call <see cref="SetPlayerLocation"/> whenever the GPS position changes.
/// The map fills whatever size its RectTransform has (a small corner minimap or the whole
/// screen) and adapts if that size changes at runtime.
/// Drag to move the map, pinch or use the mouse wheel to zoom. Dragging stops the map from
/// following the player until <see cref="Recenter"/> is called.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class MapView : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, IScrollHandler
{
    // The key is read from Assets/Resources/MapTilerKey.txt, which is ignored by git.
    const string KeyResourceName = "MapTilerKey";
    const string KeyPlaceholder = "PASTE_YOUR_MAPTILER_KEY_HERE";
    const int MaxCachedTiles = 100;
    const int MaxTileZoom = 22;
    static readonly Vector3Int NoTile = new(-1, -1, -1);
    static readonly Color LoadingColor = new(0.9f, 0.9f, 0.88f);

    [Tooltip("Raster tile URL. {z}, {x}, {y} and {key} are filled in at runtime.")]
    public string urlTemplate = "https://api.maptiler.com/maps/streets-v2/{z}/{x}/{y}.png?key={key}";

    [Header("Zoom")]
    [Tooltip("Current zoom: 16 = neighbourhood, 17 = a few streets, 18 = individual buildings. Pinching changes it.")]
    [Range(1, MaxTileZoom)] public float zoom = 17f;
    [Range(1, MaxTileZoom)] public int minZoom = 3;
    [Range(1, MaxTileZoom)] public int maxZoom = 19;

    [Tooltip("Size of one tile on screen at a whole zoom level, in canvas units")]
    [Min(32)] public float tileDisplaySize = 384f;

    [Header("Touch / mouse")]
    public bool allowPan = true;
    public bool allowZoom = true;
    [Tooltip("Zoom levels per mouse-wheel notch")]
    [Min(0)] public float scrollZoomSpeed = 0.25f;
    [Tooltip("After the map is dragged away from the player, follow the player again after this many " +
             "seconds without touching the map. 0 = only when Recenter() is called.")]
    [Min(0)] public float autoRecenterSeconds = 0f;

    [Header("Player marker")]
    public Color playerMarkerColor = new(0.9f, 0.15f, 0.15f);
    [Tooltip("Width/height of the player marker, in canvas units")]
    [Min(4)] public float playerMarkerSize = 24f;

    [Header("Editor preview (GPS doesn't work in the Editor)")]
    public bool previewInEditor = true;
    public double previewLatitude = 48.8584;
    public double previewLongitude = 2.2945;

    /// <summary>True while the map keeps the player in the middle; false after the map was dragged away.</summary>
    public bool IsFollowingPlayer { get; private set; } = true;

    string apiKey;
    bool ready;
    RectTransform rect;
    RectTransform tileLayer;
    RectTransform marker;

    // Positions on the whole-world map (0..1, see MapTiles.LatLonToWorld).
    double playerX, playerY;
    bool hasPlayer;
    double viewX, viewY; // the point shown in the middle of the map
    bool hasView;

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

    // Fingers (or the mouse) currently pressed on the map, by pointer id, in map-local coordinates.
    readonly Dictionary<int, Vector2> pointers = new();
    float lastInteractionTime;

    /// <summary>Updates where the player marker is. While following, the map moves along with it.</summary>
    public void SetPlayerLocation(double latitude, double longitude)
    {
        (playerX, playerY) = MapTiles.LatLonToWorld(latitude, longitude);
        hasPlayer = true;
    }

    /// <summary>Moves the map back to the player and keeps following them. Hook a "recenter" button to this.</summary>
    public void Recenter()
    {
        IsFollowingPlayer = true;
    }

    void Awake()
    {
        rect = (RectTransform)transform;
        if (!TryGetComponent<RectMask2D>(out _))
            gameObject.AddComponent<RectMask2D>();

        // Touch and mouse events only reach this object if it has a raycast target.
        if (!TryGetComponent<Graphic>(out _))
            gameObject.AddComponent<Image>().color = Color.clear;

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
        if (previewInEditor && !hasPlayer)
            SetPlayerLocation(previewLatitude, previewLongitude);
#endif
    }

    void OnValidate()
    {
        maxZoom = Mathf.Max(minZoom, maxZoom);
        zoom = Mathf.Clamp(zoom, minZoom, maxZoom);
    }

    void OnDisable()
    {
        pointers.Clear();
    }

    void LateUpdate()
    {
        if (!ready)
            return;

        if (!IsFollowingPlayer && autoRecenterSeconds > 0f && pointers.Count == 0 &&
            Time.unscaledTime - lastInteractionTime >= autoRecenterSeconds)
            IsFollowingPlayer = true;

        if (IsFollowingPlayer && hasPlayer)
        {
            viewX = playerX;
            viewY = playerY;
            hasView = true;
        }

        marker.gameObject.SetActive(hasPlayer && hasView);
        if (!hasView)
            return;

        // Tiles only exist for whole zoom levels; in-between zooms scale the nearest level by up to ±41%.
        zoom = Mathf.Clamp(zoom, minZoom, maxZoom);
        int tileZoom = Mathf.Clamp(Mathf.RoundToInt(zoom), minZoom, maxZoom);
        float scale = Mathf.Pow(2f, zoom - tileZoom);
        double n = 1 << tileZoom;
        double fx = viewX * n, fy = viewY * n;
        var center = new Vector2Int((int)Math.Floor(fx), (int)Math.Floor(fy));

        // Enough tiles to cover this RectTransform, even when tiles are shown at their smallest.
        float smallestTile = tileDisplaySize * 0.7071f;
        int cols = Mathf.CeilToInt(rect.rect.width / (2f * smallestTile));
        int rows = Mathf.CeilToInt(rect.rect.height / (2f * smallestTile));
        if (cols != halfCols || rows != halfRows || !Mathf.Approximately(tileDisplaySize, builtTileSize))
        {
            halfCols = cols;
            halfRows = rows;
            builtTileSize = tileDisplaySize;
            RebuildSlots();
            centerZoom = -1;
        }

        if (center != centerTile || tileZoom != centerZoom)
        {
            centerTile = center;
            centerZoom = tileZoom;
            AssignTiles();
        }

        // Scale and shift the whole grid so the view point sits at the middle of the map.
        float size = tileDisplaySize * scale;
        tileLayer.localScale = new Vector3(scale, scale, 1f);
        tileLayer.anchoredPosition = new Vector2(
            (float)(-(fx - center.x) * size),
            (float)((fy - center.y) * size));

        if (hasPlayer)
            marker.anchoredPosition = WorldToLocal(playerX, playerY);
    }

    // ---- Touch / mouse input ----

    public void OnPointerDown(PointerEventData e)
    {
        if (e.button != PointerEventData.InputButton.Left)
            return;
        pointers[e.pointerId] = ToLocal(e);
        lastInteractionTime = Time.unscaledTime;
    }

    public void OnPointerUp(PointerEventData e)
    {
        pointers.Remove(e.pointerId);
        lastInteractionTime = Time.unscaledTime;
    }

    public void OnDrag(PointerEventData e)
    {
        if (!hasView || !pointers.TryGetValue(e.pointerId, out var previous))
            return;

        var current = ToLocal(e);
        pointers[e.pointerId] = current;
        lastInteractionTime = Time.unscaledTime;

        if (pointers.Count == 1)
        {
            if (allowPan)
                Pan(current - previous);
            return;
        }

        // Two fingers: spreading/pinching zooms, moving both together pans.
        int otherId = e.pointerId;
        foreach (var id in pointers.Keys)
        {
            if (id != e.pointerId)
            {
                otherId = id;
                break;
            }
        }

        var other = pointers[otherId];
        float distanceBefore = Vector2.Distance(previous, other);
        float distanceAfter = Vector2.Distance(current, other);
        var middleBefore = (previous + other) * 0.5f;
        var middleAfter = (current + other) * 0.5f;

        // While following the player, zoom around the player instead of between the fingers.
        if (allowZoom && distanceBefore > 1f && distanceAfter > 1f)
            ZoomAround(IsFollowingPlayer ? Vector2.zero : middleBefore, Mathf.Log(distanceAfter / distanceBefore, 2f));
        if (allowPan && !IsFollowingPlayer)
            Pan(middleAfter - middleBefore);
    }

    public void OnScroll(PointerEventData e)
    {
        if (!allowZoom || !hasView)
            return;

        float notches = Mathf.Clamp(e.scrollDelta.y, -1f, 1f);
        ZoomAround(IsFollowingPlayer ? Vector2.zero : ToLocal(e), notches * scrollZoomSpeed);
        lastInteractionTime = Time.unscaledTime;
    }

    void Pan(Vector2 delta)
    {
        if (delta == Vector2.zero)
            return;

        IsFollowingPlayer = false;
        double worldPerUnit = WorldPerCanvasUnit(zoom);
        viewX -= delta.x * worldPerUnit;
        viewY += delta.y * worldPerUnit;
        ClampView();
    }

    /// <summary>Zooms by <paramref name="levels"/> while keeping the map point under <paramref name="local"/> in place.</summary>
    void ZoomAround(Vector2 local, float levels)
    {
        float newZoom = Mathf.Clamp(zoom + levels, minZoom, maxZoom);
        if (Mathf.Approximately(newZoom, zoom))
            return;

        double change = WorldPerCanvasUnit(zoom) - WorldPerCanvasUnit(newZoom);
        viewX += local.x * change;
        viewY -= local.y * change;
        zoom = newZoom;
        ClampView();
    }

    double WorldPerCanvasUnit(float atZoom) => 1.0 / (tileDisplaySize * Math.Pow(2.0, atZoom));

    void ClampView()
    {
        viewX -= Math.Floor(viewX); // wrap around at longitude ±180
        viewY = Math.Max(0.0, Math.Min(1.0, viewY));
    }

    /// <summary>Converts a whole-world map position to a position relative to the middle of this map.</summary>
    Vector2 WorldToLocal(double worldX, double worldY)
    {
        double unitsPerWorld = 1.0 / WorldPerCanvasUnit(zoom);
        double dx = worldX - viewX;
        if (dx > 0.5) dx -= 1.0; // take the short way around the date line
        else if (dx < -0.5) dx += 1.0;
        return new Vector2((float)(dx * unitsPerWorld), (float)(-(worldY - viewY) * unitsPerWorld));
    }

    /// <summary>Pointer position relative to the middle of this map, in canvas units.</summary>
    Vector2 ToLocal(PointerEventData e)
    {
        var cam = e.pressEventCamera != null ? e.pressEventCamera : e.enterEventCamera;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, e.position, cam, out var local);
        return local - rect.rect.center;
    }

    // ---- Tiles ----

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
        int n = 1 << centerZoom;
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
            var tile = new Vector3Int(x, y, centerZoom);
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

    // ---- Overlays ----

    void CreatePlayerMarker()
    {
        marker = CreateChild("Player Marker", transform);
        marker.sizeDelta = new Vector2(playerMarkerSize, playerMarkerSize);
        marker.localRotation = Quaternion.Euler(0f, 0f, 45f);

        var image = marker.gameObject.AddComponent<Image>();
        image.color = playerMarkerColor;
        image.raycastTarget = false;

        var outline = marker.gameObject.AddComponent<Outline>();
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
