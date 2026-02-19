using UnityEngine;
using UnityEditor;

namespace SyrosWorld.Editor
{
    /// <summary>
    /// Custom Inspector for <see cref="SyrosWorldBuilder"/>.
    ///
    /// Sections (top to bottom):
    /// <list type="number">
    ///   <item>Default serialised fields (drawn by Unity)</item>
    ///   <item>Status readout — parsed counts, terrain size, modes</item>
    ///   <item>Elevation blending controls (when blending is enabled)</item>
    ///   <item>Validation warnings for missing references</item>
    ///   <item>Generation buttons — Generate World / Regen Terrain / Clear</item>
    ///   <item>POI tools — select all POIs in the scene</item>
    ///   <item>Alignment debug — heightmap vs OSM bounds comparison</item>
    /// </list>
    /// </summary>
    [CustomEditor(typeof(SyrosWorldBuilder))]
    public class SyrosWorldBuilderEditor : UnityEditor.Editor
    {
        bool _showAlignmentFoldout;

        // =============================================================
        //  MAIN INSPECTOR
        // =============================================================

        public override void OnInspectorGUI()
        {
            var builder = (SyrosWorldBuilder)target;

            DrawDefaultInspector();

            // ── Section 1: Status readout ────────────────────────────
            EditorGUILayout.Space(16);
            EditorGUILayout.LabelField("World Generation", EditorStyles.boldLabel);
            DrawStatusBox(builder);

            // ── Section 1b: Terrain scale controls ───────────────────
            if (builder.config != null)
                DrawTerrainScaleControls(builder);

            // ── Section 1c: Building placement controls ──────────────
            if (builder.config != null)
                DrawBuildingPlacementControls(builder);

            // ── Section 2: Elevation blending controls ───────────────
            if (builder.useElevationBlending && builder.config != null)
                DrawElevationBlendingControls(builder);

            EditorGUILayout.Space(8);

            // ── Section 3: Validation warnings ───────────────────────
            DrawValidationWarnings(builder);

            EditorGUILayout.Space(8);

            // ── Section 4: Generation buttons ────────────────────────
            DrawGenerationButtons(builder);

            EditorGUILayout.Space(8);

            // ── Section 5: Quick config helper ───────────────────────
            if (builder.config == null)
                DrawConfigCreationButton(builder);

            // ── Section 6: POI tools ─────────────────────────────────
            DrawPOITools();

            // ── Section 7: Alignment debug foldout ───────────────────
            DrawAlignmentDebug(builder);
        }

        // =============================================================
        //  STATUS BOX
        // =============================================================

        /// <summary>Show parsed data counts, terrain size, and active modes.</summary>
        void DrawStatusBox(SyrosWorldBuilder builder)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // Data counts
                var data = builder.MapData;
                if (data != null)
                {
                    EditorGUILayout.LabelField("Parsed Data:",
                        $"{data.buildings.Count} buildings, " +
                        $"{data.POIs.Count} POIs, " +
                        $"{data.streets.Count} streets");
                }
                else
                {
                    EditorGUILayout.LabelField("Status:", "Not yet generated");
                }

                // Terrain readout (resolution + XZ footprint)
                if (builder.GeneratedTerrain != null)
                {
                    var td = builder.GeneratedTerrain.terrainData;
                    EditorGUILayout.LabelField("Terrain:",
                        $"{td.heightmapResolution}×{td.heightmapResolution}, " +
                        $"size {td.size.x:F0}×{td.size.z:F0}, " +
                        $"maxH {td.size.y:F0}");

                    // Warn if the live terrain height doesn't match the config
                    if (builder.config != null &&
                        Mathf.Abs(td.size.y - builder.config.terrainMaxHeight) > 0.5f)
                    {
                        EditorGUILayout.LabelField(
                            $"  ⚠ Live terrain Y={td.size.y:F0} but config says {builder.config.terrainMaxHeight:F0}. Regenerate!",
                            EditorStyles.miniLabel);
                    }
                }
                else if (builder.config != null)
                {
                    EditorGUILayout.LabelField("Config maxH:",
                        $"{builder.config.terrainMaxHeight:F0} (not yet generated)");
                }

                // Active modes
                EditorGUILayout.LabelField("Mapping:",
                    builder.useAdaptiveWarping
                        ? "Adaptive (urban-preserving)"
                        : "Uniform (true proportions)");

                EditorGUILayout.LabelField("Elevation Blending:",
                    builder.useElevationBlending
                        ? $"ON (weight={builder.config?.elevationBlendWeight:F2})"
                        : "OFF (heightmap only)");
            }
        }

        // =============================================================
        //  TERRAIN SCALE CONTROLS
        // =============================================================

        /// <summary>
        /// Editable controls for the three values that define the terrain's
        /// physical size: width (X), height (Y), and length (Z).
        /// Includes real-world scale ratio readout.
        /// </summary>
        void DrawTerrainScaleControls(SyrosWorldBuilder builder)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Terrain Scale", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();

                builder.config.targetWorldWidth = EditorGUILayout.FloatField(
                    new GUIContent("World Width (X)",
                        "East–west extent in Unity units.\n" +
                        "Real Syros ≈ 10 000 m. Divide to get the horizontal scale ratio."),
                    builder.config.targetWorldWidth);

                builder.config.targetWorldLength = EditorGUILayout.FloatField(
                    new GUIContent("World Length (Z)",
                        "North–south extent in Unity units.\n" +
                        "Real Syros ≈ 16 500 m."),
                    builder.config.targetWorldLength);

                builder.config.terrainMaxHeight = EditorGUILayout.FloatField(
                    new GUIContent("Max Terrain Height (Y)",
                        "The Y component of TerrainData.size.\n" +
                        "A normalised height of 1.0 = exactly this many Unity units.\n" +
                        "Set ≥ 418 for true 1:1 metre accuracy (Syros peak = 418 m).\n\n" +
                        "★ This is the SOLE vertical scale factor — no hidden multipliers."),
                    builder.config.terrainMaxHeight);

                // ── Real-world scale ratios ──────────────────────────
                float realWidth  = 10000f;  // ~10 km east-west
                float realLength = 16500f;  // ~16.5 km north-south
                float realHeight = 418f;    // peak elevation

                float hScale = builder.config.targetWorldWidth  / realWidth;
                float vScale = builder.config.targetWorldLength / realLength;
                float yScale = builder.config.terrainMaxHeight  / realHeight;

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Scale Ratios", EditorStyles.miniLabel);
                EditorGUILayout.LabelField(
                    $"  Horizontal: 1:{1f / hScale:F1}  (X={hScale:F3})   " +
                    $"1:{1f / vScale:F1}  (Z={vScale:F3})");
                EditorGUILayout.LabelField(
                    $"  Vertical:   1:{1f / yScale:F2}  (Y={yScale:F3})");

                bool is1to1 = Mathf.Abs(yScale - 1f) < 0.02f;
                if (is1to1)
                    EditorGUILayout.LabelField("  ✓ Vertical is ~1:1 metre", EditorStyles.boldLabel);
                else if (builder.config.terrainMaxHeight < realHeight)
                    EditorGUILayout.LabelField(
                        $"  ⚠ Peaks above {builder.config.terrainMaxHeight:F0}m will be clamped",
                        EditorStyles.boldLabel);

                if (EditorGUI.EndChangeCheck())
                {
                    EditorUtility.SetDirty(builder.config);
                }
            }
        }

        // =============================================================
        //  BUILDING PLACEMENT CONTROLS
        // =============================================================

        /// <summary>
        /// Editable controls for building placement mode, prefab material
        /// handling, and foundation-mode offset.
        /// </summary>
        void DrawBuildingPlacementControls(SyrosWorldBuilder builder)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Building Placement", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();

                builder.config.buildingPlacementMode = (BuildingPlacementMode)EditorGUILayout.EnumPopup(
                    new GUIContent("Placement Mode",
                        "Level: flat, bottom on terrain.\n" +
                        "SlopeAligned: rotated to match terrain slope.\n" +
                        "Foundation: level, top face at terrain, mesh extends down."),
                    builder.config.buildingPlacementMode);

                // Mode-specific help
                switch (builder.config.buildingPlacementMode)
                {
                    case BuildingPlacementMode.SlopeAligned:
                        EditorGUILayout.HelpBox(
                            "Buildings will be rotated to match the terrain surface normal. " +
                            "Works best on gentle slopes; steep cliffs may look odd.",
                            MessageType.Info);
                        break;

                    case BuildingPlacementMode.Foundation:
                        EditorGUILayout.HelpBox(
                            "Top face sits at terrain level. Mesh extends downward as a " +
                            "foundation. Increase building height to add more depth below terrain.",
                            MessageType.Info);

                        builder.config.foundationTopOffset = EditorGUILayout.Slider(
                            new GUIContent("Top Offset",
                                "How far the top face protrudes above the terrain. " +
                                "0 = flush, positive = cap visible above."),
                            builder.config.foundationTopOffset, 0f, 5f);
                        break;
                }

                EditorGUILayout.Space(4);

                builder.config.usePrefabMaterials = EditorGUILayout.Toggle(
                    new GUIContent("Use Prefab Materials",
                        "ON: keep the prefab's own materials (textures, shaders, etc).\n" +
                        "OFF: override with flat colours from the config."),
                    builder.config.usePrefabMaterials);

                if (builder.config.usePrefabMaterials)
                {
                    bool hasDefault = builder.defaultBuildingPrefab != null;
                    bool hasPOI     = builder.poiBuildingPrefab != null;

                    if (hasDefault || hasPOI)
                    {
                        string info = "Prefab materials will be used for: ";
                        if (hasDefault) info += "buildings";
                        if (hasDefault && hasPOI) info += " + ";
                        if (hasPOI) info += "POIs";
                        info += ". Config colours are ignored for those.";
                        EditorGUILayout.HelpBox(info, MessageType.Info);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox(
                            "No prefabs assigned — config colours will be used as fallback.",
                            MessageType.None);
                    }
                }

                if (EditorGUI.EndChangeCheck())
                {
                    EditorUtility.SetDirty(builder.config);
                }
            }
        }

        // =============================================================
        //  ELEVATION BLENDING CONTROLS
        // =============================================================

        /// <summary>
        /// Inline sliders/toggles for the elevation blending pipeline,
        /// shown only when <c>useElevationBlending</c> is enabled.
        /// </summary>
        void DrawElevationBlendingControls(SyrosWorldBuilder builder)
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Elevation Blending Settings", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();

                // ── Mode toggles ─────────────────────────────────────
                EditorGUILayout.Space(2);
                builder.elevationOnlyDebug = EditorGUILayout.Toggle(
                    new GUIContent("Elevation Only (Debug)",
                        "Ignore the heightmap completely. Terrain is built purely from JSON " +
                        "elevation data. Useful for verifying POI elevations."),
                    builder.elevationOnlyDebug);

                using (new EditorGUI.DisabledScope(builder.elevationOnlyDebug))
                {
                    builder.useDensityBasedBlending = EditorGUILayout.Toggle(
                        new GUIContent("Density-Based Blend",
                            "Where elevation data is dense the heightmap is suppressed; " +
                            "where there are gaps the heightmap fills in. " +
                            "When OFF a uniform Blend Weight is used."),
                        builder.useDensityBasedBlending);
                }

                if (builder.elevationOnlyDebug)
                {
                    EditorGUILayout.HelpBox(
                        "DEBUG MODE: The heightmap is completely ignored. " +
                        "Terrain is generated from JSON elevation data only. " +
                        "POI Y positions should now match their expected elevations.",
                        MessageType.Warning);
                }

                EditorGUILayout.Space(4);

                // ── Contextual sub-controls ──────────────────────────
                if (!builder.elevationOnlyDebug && builder.useDensityBasedBlending)
                {
                    builder.config.densityFalloffRadius = EditorGUILayout.IntSlider(
                        new GUIContent("Density Falloff Radius",
                            "Buffer (grid cells) around data-rich areas where the heightmap " +
                            "stays suppressed. Larger = bigger buffer."),
                        builder.config.densityFalloffRadius, 0, 64);
                }

                if (!builder.elevationOnlyDebug && !builder.useDensityBasedBlending)
                {
                    builder.config.elevationBlendWeight = EditorGUILayout.Slider(
                        new GUIContent("Blend Weight",
                            "0 = heightmap only, 1 = JSON elevation only"),
                        builder.config.elevationBlendWeight, 0f, 1f);
                }

                // ── Grid / rasterisation settings ────────────────────
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Grid Settings", EditorStyles.miniLabel);

                builder.config.elevationSmoothingSigma = EditorGUILayout.Slider(
                    new GUIContent("Smoothing Sigma",
                        "Gaussian sigma to remove integer staircase artifacts"),
                    builder.config.elevationSmoothingSigma, 0f, 10f);

                builder.config.elevationGridResolution = EditorGUILayout.IntSlider(
                    new GUIContent("Grid Resolution",
                        "Resolution of the intermediate elevation raster grid"),
                    builder.config.elevationGridResolution, 64, 1024);

                builder.config.elevationSearchRadius = EditorGUILayout.IntSlider(
                    new GUIContent("Search Radius",
                        "IDW scatter radius in grid cells"),
                    builder.config.elevationSearchRadius, 1, 32);

                builder.config.elevationDataMaxHeight = EditorGUILayout.FloatField(
                    new GUIContent("Max Elevation (m)",
                        "Maximum real-world elevation present in the JSON data"),
                    builder.config.elevationDataMaxHeight);

                // ── 1:1 scale warning ────────────────────────────────
                if (builder.config.terrainMaxHeight < builder.config.elevationDataMaxHeight)
                {
                    EditorGUILayout.HelpBox(
                        $"Terrain Max Height ({builder.config.terrainMaxHeight:F0}) < " +
                        $"Max Elevation ({builder.config.elevationDataMaxHeight:F0}m). " +
                        $"Peaks above {builder.config.terrainMaxHeight:F0}m will be clamped. " +
                        $"Set Terrain Max Height ≥ {builder.config.elevationDataMaxHeight:F0} " +
                        "for 1:1 metre accuracy.",
                        MessageType.Warning);
                }

                // ── Sea-level override ───────────────────────────────
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Sea-Level Override", EditorStyles.miniLabel);

                builder.config.seaLevelOverride = EditorGUILayout.Toggle(
                    new GUIContent("Sea Override",
                        "Force pure-black heightmap pixels (sea) to height 0, " +
                        "overriding any elevation blending. Prevents JSON data " +
                        "from raising ocean areas."),
                    builder.config.seaLevelOverride);

                if (builder.config.seaLevelOverride)
                {
                    builder.config.seaBlackThreshold = EditorGUILayout.Slider(
                        new GUIContent("Black Threshold",
                            "Heightmap values ≤ this are treated as sea. " +
                            "0 = only pure black; raise slightly to catch near-black coast pixels."),
                        builder.config.seaBlackThreshold, 0f, 0.05f);

                    builder.config.seaTransitionWidth = EditorGUILayout.Slider(
                        new GUIContent("Transition Width",
                            "Height range above the threshold where terrain fades to 0. " +
                            "Prevents hard cliffs at the coastline."),
                        builder.config.seaTransitionWidth, 0f, 0.1f);
                }

                if (EditorGUI.EndChangeCheck())
                {
                    EditorUtility.SetDirty(builder.config);
                    EditorUtility.SetDirty(builder);
                }
            }
        }

        // =============================================================
        //  VALIDATION WARNINGS
        // =============================================================

        void DrawValidationWarnings(SyrosWorldBuilder builder)
        {
            if (builder.config == null)
            {
                EditorGUILayout.HelpBox(
                    "No config assigned. Create one via Assets > Create > Syros > World Config.",
                    MessageType.Warning);
            }

            if (builder.osmJsonData == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign the syros_unity_with_elevation.json TextAsset.",
                    MessageType.Warning);
            }

            if (builder.generateTerrain && builder.heightmapTexture == null)
            {
                EditorGUILayout.HelpBox(
                    "Assign the heightmap texture (Read/Write must be enabled in import settings).",
                    MessageType.Info);
            }

            // Non-power-of-2 info for heightmap texture
            if (builder.heightmapTexture != null)
            {
                int w = builder.heightmapTexture.width;
                int h = builder.heightmapTexture.height;
                if (!IsPowerOfTwo(w) || !IsPowerOfTwo(h))
                {
                    EditorGUILayout.HelpBox(
                        $"Heightmap is {w}×{h} (non-power-of-2). This is fine — " +
                        "the system samples it with bilinear interpolation. " +
                        "Make sure the texture import has 'Non-Power of 2' set to 'None' " +
                        "so Unity doesn't rescale it.",
                        MessageType.Info);
                }
            }
        }

        // =============================================================
        //  GENERATION BUTTONS
        // =============================================================

        void DrawGenerationButtons(SyrosWorldBuilder builder)
        {
            // ── Primary: Generate World ──────────────────────────────
            GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
            if (GUILayout.Button("Generate World", GUILayout.Height(36)))
            {
                Undo.RegisterFullObjectHierarchyUndo(builder.gameObject, "Generate Syros World");
                builder.GenerateWorld();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(4);

            // ── Secondary: Regen Terrain / Clear ─────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Regenerate Terrain Only", GUILayout.Height(28)))
                {
                    Undo.RegisterFullObjectHierarchyUndo(builder.gameObject, "Regenerate Terrain");
                    builder.RegenerateTerrain();
                }

                GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
                if (GUILayout.Button("Clear All", GUILayout.Height(28), GUILayout.Width(100)))
                {
                    if (EditorUtility.DisplayDialog("Clear Generated World",
                        "Remove all generated terrain, buildings, and streets?", "Clear", "Cancel"))
                    {
                        Undo.RegisterFullObjectHierarchyUndo(builder.gameObject, "Clear Syros World");
                        builder.ClearGenerated();
                    }
                }
                GUI.backgroundColor = Color.white;
            }
        }

        // =============================================================
        //  CONFIG CREATION HELPER
        // =============================================================

        void DrawConfigCreationButton(SyrosWorldBuilder builder)
        {
            if (GUILayout.Button("Create Default Config Asset"))
            {
                var config = ScriptableObject.CreateInstance<SyrosWorldConfig>();
                string path = EditorUtility.SaveFilePanelInProject(
                    "Save Syros Config", "SyrosWorldConfig", "asset",
                    "Choose where to save the config");
                if (!string.IsNullOrEmpty(path))
                {
                    AssetDatabase.CreateAsset(config, path);
                    AssetDatabase.SaveAssets();
                    builder.config = config;
                    EditorUtility.SetDirty(builder);
                }
            }
        }

        // =============================================================
        //  POI TOOLS
        // =============================================================

        void DrawPOITools()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("POI Tools", EditorStyles.boldLabel);

            if (GUILayout.Button("Select All POIs in Scene"))
            {
                var pois = FindObjectsByType<SyrosPOIMarker>(FindObjectsSortMode.None);
                var gos = new GameObject[pois.Length];
                for (int i = 0; i < pois.Length; i++)
                    gos[i] = pois[i].gameObject;
                Selection.objects = gos;
                Debug.Log($"Selected {pois.Length} POIs");
            }
        }

        // =============================================================
        //  ALIGNMENT DEBUG FOLDOUT
        // =============================================================

        /// <summary>
        /// Foldout that compares heightmap versus OSM geo-bounds and
        /// provides buttons to log landmark / corner height samples.
        /// </summary>
        void DrawAlignmentDebug(SyrosWorldBuilder builder)
        {
            EditorGUILayout.Space(12);
            _showAlignmentFoldout = EditorGUILayout.Foldout(_showAlignmentFoldout,
                "Heightmap \u2194 OSM Alignment Debug", true, EditorStyles.foldoutHeader);

            if (!_showAlignmentFoldout) return;

            EditorGUILayout.HelpBox(
                "The heightmap and OSM data both use geo-coordinate bounds to map onto " +
                "the same world space. If they don't cover the exact same area, adjust the " +
                "Heightmap Geo Bounds in the Config.\n\n" +
                "Use 'Log Corner Check' to see where known landmarks end up — " +
                "if a coastal building appears inland, the bounds need tweaking.",
                MessageType.None);

            // ── Bounds comparison ────────────────────────────────────
            if (builder.config != null)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("OSM Bounds", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"  Lon: {builder.config.minLon:F6} \u2192 {builder.config.maxLon:F6}");
                    EditorGUILayout.LabelField($"  Lat: {builder.config.minLat:F6} \u2192 {builder.config.maxLat:F6}");

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Heightmap Bounds", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"  Lon: {builder.config.heightmapMinLon:F6} \u2192 {builder.config.heightmapMaxLon:F6}");
                    EditorGUILayout.LabelField($"  Lat: {builder.config.heightmapMinLat:F6} \u2192 {builder.config.heightmapMaxLat:F6}");

                    bool match =
                        System.Math.Abs(builder.config.minLon - builder.config.heightmapMinLon) < 0.0001 &&
                        System.Math.Abs(builder.config.maxLon - builder.config.heightmapMaxLon) < 0.0001 &&
                        System.Math.Abs(builder.config.minLat - builder.config.heightmapMinLat) < 0.0001 &&
                        System.Math.Abs(builder.config.maxLat - builder.config.heightmapMaxLat) < 0.0001;

                    EditorGUILayout.LabelField(
                        match ? "\u2713 Bounds match (OSM = Heightmap)"
                              : "\u26A0 Bounds differ \u2014 heightmap is offset from OSM",
                        EditorStyles.boldLabel);
                }
            }

            // ── Debug buttons ────────────────────────────────────────
            if (builder.MapData != null && builder.Converter != null && builder.Converter.IsInitialized)
            {
                if (GUILayout.Button("Log Corner Check (Ermoupoli Port)"))
                {
                    double testLon = 24.9410;
                    double testLat = 37.4430;
                    var worldPos = builder.Converter.GeoToWorld(testLon, testLat, builder.GeneratedTerrain);
                    Debug.Log($"[Alignment] Ermoupoli Port ({testLon}, {testLat}) \u2192 " +
                              $"World ({worldPos.x:F1}, {worldPos.y:F1}, {worldPos.z:F1}). " +
                              $"Should be near the coast at low elevation.");
                }

                if (GUILayout.Button("Log Heightmap Sample at Corners"))
                    LogCornerHeights(builder);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Generate the world first, then use these buttons to verify alignment.",
                    MessageType.Info);
            }
        }

        // =============================================================
        //  HELPERS
        // =============================================================

        /// <summary>Log normalised heightmap values at the four corners + centre.</summary>
        static void LogCornerHeights(SyrosWorldBuilder builder)
        {
            if (builder.heightmapTexture == null || builder.Converter == null) return;
            var tex  = builder.heightmapTexture;
            var conv = builder.Converter;

            string[] labels = { "SW (bottom-left)", "SE (bottom-right)", "NW (top-left)", "NE (top-right)", "Centre" };
            float[]  xs     = { 0f, 1f, 0f, 1f, 0.5f };
            float[]  ys     = { 0f, 0f, 1f, 1f, 0.5f };

            for (int i = 0; i < labels.Length; i++)
            {
                float h   = conv.SampleHeightmap(tex, xs[i], ys[i]);
                var   geo = conv.WorldNormToGeo(xs[i], ys[i]);
                Debug.Log($"[Alignment] {labels[i]}: geo ({geo.x:F5}, {geo.y:F5}), " +
                          $"heightmap value = {h:F3} " +
                          $"(should be ~0 if sea, >0 if land)");
            }
        }

        /// <summary>True if <paramref name="x"/> is a power of two.</summary>
        static bool IsPowerOfTwo(int x) => x > 0 && (x & (x - 1)) == 0;
    }
}
