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
/// following the player until <see cref="Recenter"/> or <see cref="ResetView"/> is called.
/// Use <see cref="AddMarker"/> and <see cref="RemoveMarker"/> to show places such as treasures.
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

    [Header("Markers added with AddMarker")]
    public Color markerColor = new(1f, 0.76f, 0.1f);
    [Tooltip("Diameter of a marker, in canvas units")]
    [Min(4)] public float markerSize = 26f;

    [Header("Editor preview (GPS doesn't work in the Editor)")]
    public bool previewInEditor = true;
    public double previewLatitude = 48.8584;
    public double previewLongitude = 2.2945;

    /// <summary>True while the map keeps the player in the middle; false after the map was dragged away.</summary>
    public bool IsFollowingPlayer { get; private set; } = true;

    bool built;
    string apiKey;
    bool ready;
    float defaultZoom;
    RectTransform rect;
    RectTransform tileLayer;
    RectTransform markerLayer;
    RectTransform playerMarker;

    // Positions on the whole-world map (0..1, see MapTiles.LatLonToWorld).
    double playerX, playerY;
    double playerLatitude, playerLongitude;
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

    sealed class Marker
    {
        public double Latitude, Longitude;
        public double WorldX, WorldY;
        public RectTransform Rect;
    }

    readonly List<Marker> markers = new();
    static Sprite circleSprite;

    /// <summary>Updates where the player marker is. While following, the map moves along with it.</summary>
    public void SetPlayerLocation(double latitude, double longitude)
    {
        playerLatitude = latitude;
        playerLongitude = longitude;
        (playerX, playerY) = MapTiles.LatLonToWorld(latitude, longitude);
        hasPlayer = true;
    }

    /// <summary>
    /// The last location passed to <see cref="SetPlayerLocation"/> (in the Editor, the preview location).
    /// Returns false if the player's location isn't known yet.
    /// </summary>
    public bool TryGetPlayerLocation(out double latitude, out double longitude)
    {
        latitude = playerLatitude;
        longitude = playerLongitude;
        return hasPlayer;
    }

    /// <summary>Moves the map back to the player and keeps following them. Hook a "recenter" button to this.</summary>
    public void Recenter()
    {
        IsFollowingPlayer = true;
    }

    /// <summary>Goes back to the zoom the map started with, centered on and following the player.</summary>
    public void ResetView()
    {
        Build();
        zoom = defaultZoom;
        IsFollowingPlayer = true;
    }

    /// <summary>
    /// Places a marker at a latitude/longitude. Returns the marker's RectTransform, so you can change its look
    /// (e.g. <c>GetComponent&lt;Image&gt;().color</c>) or add your own UI objects to it as children.
    /// </summary>
    public RectTransform AddMarker(double latitude, double longitude)
    {
        Build();

        var rt = CreateChild("Marker", markerLayer);
        rt.sizeDelta = new Vector2(markerSize, markerSize);

        var image = rt.gameObject.AddComponent<Image>();
        image.sprite = CircleSprite();
        image.color = markerColor;
        image.raycastTarget = false;

        var outline = rt.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.6f);
        outline.effectDistance = new Vector2(2f, 2f);

        var (worldX, worldY) = MapTiles.LatLonToWorld(latitude, longitude);
        markers.Add(new Marker
        {
            Latitude = latitude,
            Longitude = longitude,
            WorldX = worldX,
            WorldY = worldY,
            Rect = rt,
        });
        return rt;
    }

    /// <summary>
    /// Removes the marker at a latitude/longitude. Every marker within <paramref name="toleranceMeters"/> of that
    /// point is removed, so small rounding differences (e.g. float vs double) don't matter.
    /// Returns true if a marker was removed.
    /// </summary>
    public bool RemoveMarker(double latitude, double longitude, double toleranceMeters = 1.0)
    {
        bool removed = false;
        for (int i = markers.Count - 1; i >= 0; i--)
        {
            var m = markers[i];
            if (GeoMath.DistanceMeters(latitude, longitude, m.Latitude, m.Longitude) > toleranceMeters)
                continue;

            if (m.Rect != null)
                Destroy(m.Rect.gameObject);
            markers.RemoveAt(i);
            removed = true;
        }
        return removed;
    }

    void Awake()
    {
        Build();
    }

    /// <summary>Creates the map's child objects and reads the API key. Runs once, even if called again.</summary>
    void Build()
    {
        if (built)
            return;
        built = true;

        rect = (RectTransform)transform;
        if (!TryGetComponent<RectMask2D>(out _))
            gameObject.AddComponent<RectMask2D>();

        // Touch and mouse events only reach this object if it has a raycast target.
        if (!TryGetComponent<Graphic>(out _))
            gameObject.AddComponent<Image>().color = Color.clear;

        tileLayer = CreateChild("Tiles", transform);
        tileLayer.SetAsFirstSibling();
        markerLayer = CreateChild("Markers", transform);
        markerLayer.SetSiblingIndex(1); // above the tiles, below the player marker
        markerLayer.gameObject.SetActive(false); // shown once the map knows where it is
        CreatePlayerMarker();
        CreateAttribution();
        defaultZoom = Mathf.Clamp(zoom, minZoom, maxZoom);

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

        playerMarker.gameObject.SetActive(hasPlayer && hasView);
        markerLayer.gameObject.SetActive(hasView);
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
            playerMarker.anchoredPosition = WorldToLocal(playerX, playerY);

        foreach (var m in markers)
        {
            if (m.Rect != null)
                m.Rect.anchoredPosition = WorldToLocal(m.WorldX, m.WorldY);
        }
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
        playerMarker = CreateChild("Player Marker", transform);
        playerMarker.sizeDelta = new Vector2(playerMarkerSize, playerMarkerSize);
        playerMarker.localRotation = Quaternion.Euler(0f, 0f, 45f);

        var image = playerMarker.gameObject.AddComponent<Image>();
        image.color = playerMarkerColor;
        image.raycastTarget = false;

        var outline = playerMarker.gameObject.AddComponent<Outline>();
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

    /// <summary>A white circle with smooth edges, drawn in code so no image asset is needed.</summary>
    static Sprite CircleSprite()
    {
        if (circleSprite != null)
            return circleSprite;

        const int size = 64;
        float radius = size / 2f;
        var center = new Vector2(radius, radius);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
            byte alpha = (byte)(Mathf.Clamp01(radius - distance) * 255f);
            pixels[y * size + x] = new Color32(255, 255, 255, alpha);
        }

        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        circleSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return circleSprite;
    }

    static RectTransform CreateChild(string name, Transform parent)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        return rt;
    }
}
