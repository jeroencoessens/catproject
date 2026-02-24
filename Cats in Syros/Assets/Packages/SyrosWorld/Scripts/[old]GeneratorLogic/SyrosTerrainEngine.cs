using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Globalization;

/// <summary>
/// A named POI that acts as a terrain height anchor.
/// Height 0 = sea level, 1 = maximum elevation.
/// </summary>
[System.Serializable]
public class TerrainAnchor
{
    [Tooltip("Exact POI name from the OSM data (case-sensitive, Unicode).")]
    public string poiName;

    [Tooltip("Target normalised height: 0 = sea level, 1 = maximum elevation.")]
    [Range(0f, 1f)] public float height;
}

/// <summary>
/// Generates terrain for Ermoupoli / Ano Syros from OSM GeoJSON using
/// anchor-point driven Gaussian interpolation.
///
/// Algorithm:
///   1. Parse features and build a per-feature coordinate lookup.
///   2. Resolve each TerrainAnchor's POI name to a heightmap grid position.
///   3. Compute heights via Gaussian-weighted interpolation from all anchors.
///   4. Optionally overlay OSM building-density for subtle surface detail.
///   5. Apply heightmap to Unity Terrain and spawn POI markers.
/// </summary>
[RequireComponent(typeof(Terrain))]
public class SyrosTerrainEngine : MonoBehaviour
{
    [Header("Data Source")]
    public TextAsset osmJsonFile;
    public GameObject markerPrefab;

    [Header("Terrain Settings")]
    public float terrainScale = 2000f;
    public float maxElevation = 185f;

    [Header("Anchor Points")]
    [Tooltip("Named POIs that define terrain heights. Each anchor creates a smooth hill or valley.")]
    public List<TerrainAnchor> anchors = new List<TerrainAnchor>
    {
        new TerrainAnchor { poiName = "Άγιος Γεώργιος",          height = 1.00f },
        new TerrainAnchor { poiName = "Αναστάσεως του Σωτήρος",  height = 0.85f },
        new TerrainAnchor { poiName = "Axaopoulou Jewelry",       height = 0.00f },
    };

    [Tooltip("How far each anchor's influence spreads (in heightmap pixels). Larger = broader hills.")]
    [Range(10, 300)] public int anchorSpread = 100;

    [Tooltip("Default terrain height where no anchor has strong influence (sea-level base).")]
    [Range(0f, 0.15f)] public float baseHeight = 0.02f;

    [Header("Density Overlay")]
    [Tooltip("Blend in OSM building-density as subtle surface texture (0 = pure anchor terrain).")]
    [Range(0f, 0.3f)] public float densityBlend = 0.05f;

    [Tooltip("Gaussian blur radius for the density overlay layer.")]
    [Range(2, 40)] public int densityBlurRadius = 15;

    [Header("POI Markers")]
    [Tooltip("Uniform scale applied to each spawned POI marker prefab.")]
    public float markerScale = 5f;

    [Tooltip("Extra height above the terrain surface so markers don't clip into the ground.")]
    public float markerYOffset = 2f;

    // ── Ermoupoli + Ano Syros bounds (with coastal padding) ───────────
    // Core city: lon ~24.930–24.950, lat ~37.435–37.455
    // Padding gives a natural coastline fringe so edges don't clip.
    const float MinLon = 24.922f;
    const float MaxLon = 24.958f;
    const float MinLat = 37.427f;
    const float MaxLat = 37.463f;

    // ── Lightweight JSON classes (name + type only, coords via regex) ─
    [System.Serializable] public class OSMData    { public List<Feature> features; }
    [System.Serializable] public class Feature    { public Properties properties; public Geometry geometry; }
    [System.Serializable] public class Properties { public string name; public string building; }
    [System.Serializable] public class Geometry   { public string type; }

    // Reusable compiled coord regex
    static readonly Regex CoordRegex = new Regex(@"\[\s*(24\.\d+)\s*,\s*(37\.\d+)\s*\]", RegexOptions.Compiled);

    // Internal: resolved anchor with grid coordinates
    struct ResolvedAnchor { public float row, col, height; public string name; }

    // ───────────────────────────────────────────────────────────────────
    //  MAIN ENTRY POINT
    // ───────────────────────────────────────────────────────────────────
    public bool GenerateFromOSM()
    {
        if (osmJsonFile == null) { Debug.LogError("[SyrosEngine] Assign the JSON file!"); return false; }

        try
        {
            EditorUtility.DisplayProgressBar("Syros Terrain", "Initialising…", 0f);

            Terrain terrain = GetComponent<Terrain>();
            ApplyURPShader(terrain);
            ClearChildren();

            string rawJson = osmJsonFile.text;
            int res = terrain.terrainData.heightmapResolution;
            Debug.Log($"[SyrosEngine] Heightmap res: {res}, bounds: lon [{MinLon},{MaxLon}] lat [{MinLat},{MaxLat}]");

            // Step 1 — Parse features and build coordinate map
            EditorUtility.DisplayProgressBar("Syros Terrain", "Parsing features…", 0.05f);
            OSMData data = JsonUtility.FromJson<OSMData>(rawJson);
            var featureCoords = BuildFeatureCoordMap(rawJson, data.features.Count);

            // Step 2 — Resolve anchor POI names to grid positions
            EditorUtility.DisplayProgressBar("Syros Terrain", "Resolving anchors…", 0.12f);
            var resolved = ResolveAnchors(data, featureCoords, res);

            // Step 3 — Compute heightmap
            float[,] heights;
            if (resolved.Count >= 2)
            {
                // Anchor-driven Gaussian-weighted interpolation
                heights = ComputeAnchorHeightmap(res, resolved, anchorSpread);
            }
            else
            {
                Debug.LogWarning("[SyrosEngine] Fewer than 2 anchors resolved — falling back to density-only terrain.");
                heights = BinCoordinates(rawJson, res);
                heights = GaussianBlur(heights, res, densityBlurRadius);
                NormalizeHeights(heights, res, baseHeight);
            }

            // Step 4 — Optional density overlay for surface texture
            if (densityBlend > 0f)
            {
                EditorUtility.DisplayProgressBar("Syros Terrain", "Computing density overlay…", 0.65f);
                float[,] density = BinCoordinates(rawJson, res);
                density = GaussianBlur(density, res, densityBlurRadius);
                NormalizeDensity(density, res);
                BlendDensity(heights, density, res, densityBlend);
            }

            // Step 5 — Apply to terrain
            EditorUtility.DisplayProgressBar("Syros Terrain", "Applying heightmap…", 0.85f);
            terrain.terrainData.size = new Vector3(terrainScale, maxElevation, terrainScale);
            terrain.terrainData.SetHeights(0, 0, heights);

            // Step 6 — Spawn POI markers
            EditorUtility.DisplayProgressBar("Syros Terrain", "Spawning POIs…", 0.92f);
            SpawnPOIMarkers(data, featureCoords, terrain);

            EditorUtility.DisplayProgressBar("Syros Terrain", "Done!", 1f);
            Debug.Log("[SyrosEngine] Generation complete.");
            return true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  RESOLVE — Match anchor POI names to heightmap grid positions
    // ───────────────────────────────────────────────────────────────────
    List<ResolvedAnchor> ResolveAnchors(OSMData data, Dictionary<int, LatLon> featureCoords, int res)
    {
        var resolved = new List<ResolvedAnchor>();

        foreach (var anchor in anchors)
        {
            if (string.IsNullOrEmpty(anchor.poiName)) continue;

            bool found = false;
            for (int i = 0; i < data.features.Count; i++)
            {
                var feat = data.features[i];
                if (feat.properties.name != anchor.poiName) continue;
                if (!featureCoords.TryGetValue(i, out LatLon ll)) continue;

                float nx = Mathf.InverseLerp(MinLon, MaxLon, ll.lon);
                float ny = Mathf.InverseLerp(MinLat, MaxLat, ll.lat);
                if (nx <= 0f || nx >= 1f || ny <= 0f || ny >= 1f)
                {
                    Debug.LogWarning($"[SyrosEngine] Anchor '{anchor.poiName}' at ({ll.lon}, {ll.lat}) is outside bounds — skipped.");
                    continue;
                }

                resolved.Add(new ResolvedAnchor
                {
                    row  = ny * (res - 1),
                    col  = nx * (res - 1),
                    height = anchor.height,
                    name = anchor.poiName
                });

                Debug.Log($"[SyrosEngine] Anchor '{anchor.poiName}' → grid row {ny * (res - 1):F0}, col {nx * (res - 1):F0}, height {anchor.height:F2}");
                found = true;
                break;
            }

            if (!found)
                Debug.LogWarning($"[SyrosEngine] Anchor '{anchor.poiName}' not found in data!");
        }

        Debug.Log($"[SyrosEngine] Resolved {resolved.Count}/{anchors.Count} anchors.");
        return resolved;
    }

    // ───────────────────────────────────────────────────────────────────
    //  ANCHOR HEIGHTMAP — Gaussian-weighted interpolation from anchors
    //
    //  For each pixel, compute a weighted average of all anchor heights
    //  where weight = exp(-dist² / 2σ²). A small background weight
    //  pulls uncovered areas toward baseHeight.
    //  O(res² × numAnchors) — typically < 200 ms for ~5 anchors.
    // ───────────────────────────────────────────────────────────────────
    float[,] ComputeAnchorHeightmap(int res, List<ResolvedAnchor> resolved, float sigma)
    {
        float[,] grid = new float[res, res];
        float sigma2x2 = 2f * sigma * sigma;
        float bgWeight = 0.001f;                 // pulls distant pixels toward baseHeight
        int progressStep = Mathf.Max(1, res / 30);

        for (int r = 0; r < res; r++)
        {
            if (r % progressStep == 0)
            {
                float p = 0.15f + 0.45f * ((float)r / res);
                if (EditorUtility.DisplayCancelableProgressBar("Syros Terrain",
                        $"Anchor interpolation… row {r}/{res}", p))
                {
                    Debug.LogWarning("[SyrosEngine] Cancelled by user.");
                    return grid;
                }
            }

            for (int c = 0; c < res; c++)
            {
                float sumW  = bgWeight;
                float sumWH = bgWeight * baseHeight;

                for (int a = 0; a < resolved.Count; a++)
                {
                    float dr = r - resolved[a].row;
                    float dc = c - resolved[a].col;
                    float w = Mathf.Exp(-(dr * dr + dc * dc) / sigma2x2);
                    sumW  += w;
                    sumWH += w * resolved[a].height;
                }

                grid[r, c] = sumWH / sumW;
            }
        }

        Debug.Log($"[SyrosEngine] Anchor heightmap computed ({resolved.Count} anchors, σ={sigma}).");
        return grid;
    }

    // ───────────────────────────────────────────────────────────────────
    //  DENSITY — Bin coordinates into a grid  (O(N), used for overlay)
    // ───────────────────────────────────────────────────────────────────
    float[,] BinCoordinates(string rawJson, int res)
    {
        float[,] grid = new float[res, res];
        MatchCollection matches = CoordRegex.Matches(rawJson);

        int inBounds = 0;
        foreach (Match m in matches)
        {
            float lon = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            float lat = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

            float nx = Mathf.InverseLerp(MinLon, MaxLon, lon);
            float ny = Mathf.InverseLerp(MinLat, MaxLat, lat);
            if (nx <= 0f || nx >= 1f || ny <= 0f || ny >= 1f) continue;

            int col = Mathf.Clamp((int)(nx * (res - 1)), 0, res - 1);
            int row = Mathf.Clamp((int)(ny * (res - 1)), 0, res - 1);
            grid[row, col] += 1f;
            inBounds++;
        }

        Debug.Log($"[SyrosEngine] Density: binned {inBounds}/{matches.Count} coords into {res}×{res} grid.");
        return grid;
    }

    // ───────────────────────────────────────────────────────────────────
    //  DENSITY — Separable Gaussian blur  (O(res² × kernelWidth))
    // ───────────────────────────────────────────────────────────────────
    static float[,] GaussianBlur(float[,] src, int res, int radius)
    {
        float sigma = radius / 2.5f;
        float[] kernel = new float[radius * 2 + 1];
        float kSum = 0f;
        for (int i = 0; i < kernel.Length; i++)
        {
            float x = i - radius;
            kernel[i] = Mathf.Exp(-(x * x) / (2f * sigma * sigma));
            kSum += kernel[i];
        }
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= kSum;

        int last = res - 1;

        // Horizontal pass
        float[,] tmp = new float[res, res];
        for (int r = 0; r < res; r++)
            for (int c = 0; c < res; c++)
            {
                float v = 0f;
                int kMin = Mathf.Max(-radius, -c);
                int kMax = Mathf.Min(radius, last - c);
                for (int k = kMin; k <= kMax; k++)
                    v += src[r, c + k] * kernel[k + radius];
                tmp[r, c] = v;
            }

        // Vertical pass
        float[,] dst = new float[res, res];
        for (int r = 0; r < res; r++)
        {
            int rMin = Mathf.Max(0, r - radius);
            int rMax = Mathf.Min(last, r + radius);
            for (int c = 0; c < res; c++)
            {
                float v = 0f;
                for (int sr = rMin; sr <= rMax; sr++)
                    v += tmp[sr, c] * kernel[(sr - r) + radius];
                dst[r, c] = v;
            }
        }

        return dst;
    }

    // ───────────────────────────────────────────────────────────────────
    //  DENSITY — Normalize density grid to [0, 1]
    // ───────────────────────────────────────────────────────────────────
    static void NormalizeDensity(float[,] grid, int res)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int r = 0; r < res; r++)
            for (int c = 0; c < res; c++)
            {
                float v = grid[r, c];
                if (v < min) min = v;
                if (v > max) max = v;
            }

        float range = max - min;
        if (range < 1e-6f) return;
        float invRange = 1f / range;

        for (int r = 0; r < res; r++)
            for (int c = 0; c < res; c++)
                grid[r, c] = (grid[r, c] - min) * invRange;
    }

    // ───────────────────────────────────────────────────────────────────
    //  DENSITY — Normalize to [baseH, 1] (fallback when < 2 anchors)
    // ───────────────────────────────────────────────────────────────────
    static void NormalizeHeights(float[,] grid, int res, float baseH)
    {
        float min = float.MaxValue, max = float.MinValue;
        for (int r = 0; r < res; r++)
            for (int c = 0; c < res; c++)
            {
                float v = grid[r, c];
                if (v < min) min = v;
                if (v > max) max = v;
            }

        float range = max - min;
        if (range < 1e-6f) range = 1f;
        float invRange = (1f - baseH) / range;

        for (int r = 0; r < res; r++)
            for (int c = 0; c < res; c++)
                grid[r, c] = baseH + (grid[r, c] - min) * invRange;
    }

    // ───────────────────────────────────────────────────────────────────
    //  BLEND — Add density overlay onto the anchor heightmap
    // ───────────────────────────────────────────────────────────────────
    static void BlendDensity(float[,] heights, float[,] density, int res, float blend)
    {
        for (int r = 0; r < res; r++)
            for (int c = 0; c < res; c++)
                heights[r, c] = Mathf.Clamp01(heights[r, c] + density[r, c] * blend);
    }

    // ───────────────────────────────────────────────────────────────────
    //  COORD MAP — feature-index → first coordinate (single regex pass)
    // ───────────────────────────────────────────────────────────────────
    struct LatLon { public float lon, lat; }

    static Dictionary<int, LatLon> BuildFeatureCoordMap(string rawJson, int featureCount)
    {
        var map = new Dictionary<int, LatLon>(featureCount);
        var scanner = new Regex(
            @"""type""\s*:\s*""Feature""|\[\s*(24\.\d+)\s*,\s*(37\.\d+)\s*\]",
            RegexOptions.Compiled);

        int featureIdx = -1;
        bool needCoord = false;

        foreach (Match m in scanner.Matches(rawJson))
        {
            if (m.Value.Contains("Feature"))
            {
                featureIdx++;
                needCoord = true;
            }
            else if (needCoord && m.Groups[1].Success)
            {
                float lon = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                float lat = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                map[featureIdx] = new LatLon { lon = lon, lat = lat };
                needCoord = false;
            }
        }

        Debug.Log($"[SyrosEngine] Coord map: {map.Count}/{featureCount} features mapped.");
        return map;
    }

    // ───────────────────────────────────────────────────────────────────
    //  POI MARKERS — Spawn named features on the terrain surface
    // ───────────────────────────────────────────────────────────────────
    void SpawnPOIMarkers(OSMData data, Dictionary<int, LatLon> featureCoords, Terrain terrain)
    {
        if (markerPrefab == null)
        {
            Debug.LogWarning("[SyrosEngine] markerPrefab not assigned — skipping POIs.");
            return;
        }

        int spawned = 0, noCoord = 0, oob = 0;

        for (int i = 0; i < data.features.Count; i++)
        {
            var feat = data.features[i];
            if (string.IsNullOrEmpty(feat.properties.name)) continue;

            if (!featureCoords.TryGetValue(i, out LatLon ll))
            { noCoord++; continue; }

            float nx = Mathf.InverseLerp(MinLon, MaxLon, ll.lon);
            float ny = Mathf.InverseLerp(MinLat, MaxLat, ll.lat);
            if (nx <= 0 || nx >= 1 || ny <= 0 || ny >= 1) { oob++; continue; }

            Vector3 pos = new Vector3(nx * terrainScale, 0f, ny * terrainScale) + transform.position;
            pos.y = terrain.SampleHeight(pos) + markerYOffset;

            GameObject marker = (GameObject)PrefabUtility.InstantiatePrefab(markerPrefab);
            marker.transform.position = pos;
            marker.transform.localScale = Vector3.one * markerScale;
            marker.transform.SetParent(transform);
            marker.name = feat.properties.name;
            spawned++;
        }

        Debug.Log($"[SyrosEngine] POIs: {spawned} spawned, {noCoord} no coords, {oob} outside bounds.");
    }

    // ───────────────────────────────────────────────────────────────────
    //  UTILITIES
    // ───────────────────────────────────────────────────────────────────
    void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }

    void ApplyURPShader(Terrain terrain)
    {
        Shader urp = Shader.Find("Universal Render Pipeline/Terrain/Lit");
        if (urp != null && (terrain.materialTemplate == null || terrain.materialTemplate.shader != urp))
            terrain.materialTemplate = new Material(urp);
    }
}

// ── CUSTOM INSPECTOR ──────────────────────────────────────────────────────
[CustomEditor(typeof(SyrosTerrainEngine))]
public class SyrosTerrainEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        SyrosTerrainEngine engine = (SyrosTerrainEngine)target;

        EditorGUILayout.Space();
        GUI.backgroundColor = new Color(0.3f, 0.9f, 0.4f);
        if (GUILayout.Button("GENERATE TERRAIN FROM ANCHORS", GUILayout.Height(40)))
            engine.GenerateFromOSM();
        GUI.backgroundColor = Color.white;

        EditorGUILayout.HelpBox(
            "Anchor-driven terrain for Ermoupoli + Ano Syros.\n\n" +
            "• Add named POIs as anchors — height 0 = sea level, 1 = peak.\n" +
            "• Anchor Spread controls how far each peak/valley extends.\n" +
            "• Density Blend adds subtle OSM building-density texture.\n" +
            "• Cancel any time via the progress bar.",
            MessageType.Info);
    }
}
