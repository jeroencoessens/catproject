using System.Collections.Generic;
using UnityEngine;

namespace SyrosWorld
{
    /// <summary>
    /// Main orchestrator for the Syros World generation pipeline.
    ///
    /// Attach to an empty GameObject and assign the required references
    /// in the Inspector.  Use the custom editor button "Generate World"
    /// (or call <see cref="GenerateWorld"/> from script) to build everything.
    ///
    /// <b>Pipeline steps:</b>
    /// <list type="number">
    ///   <item>Parse GeoJSON → <see cref="SyrosMapData"/>.</item>
    ///   <item>Build coordinate warp tables → <see cref="SyrosGeoConverter"/>.</item>
    ///   <item>Generate terrain (heightmap ± elevation blending).</item>
    ///   <item>Place buildings and streets on the terrain.</item>
    /// </list>
    /// </summary>
    [ExecuteInEditMode]
    public class SyrosWorldBuilder : MonoBehaviour
    {
        // =================================================================
        //  DATA INPUTS
        // =================================================================

        [Header("Data Sources")]

        [Tooltip("The syros_unity_with_elevation.json GeoJSON file.")]
        public TextAsset osmJsonData;

        [Tooltip("Grayscale heightmap image (Read/Write must be enabled in import settings).")]
        public Texture2D heightmapTexture;

        // =================================================================
        //  CONFIGURATION
        // =================================================================

        [Header("Configuration")]

        [Tooltip("ScriptableObject holding all generation parameters.\n" +
                 "Create via: Assets → Create → Syros → World Config.")]
        public SyrosWorldConfig config;

        // =================================================================
        //  PREFABS
        // =================================================================

        [Header("Building Prefabs")]

        [Tooltip("Default building blockout prefab (scaled to match footprint). " +
                 "Leave null for auto-generated cubes.")]
        public GameObject defaultBuildingPrefab;

        [Tooltip("POI building prefab (named / important buildings). " +
                 "Falls back to defaultBuildingPrefab if null.")]
        public GameObject poiBuildingPrefab;

        // =================================================================
        //  GENERATION OPTIONS
        // =================================================================

        [Header("Generation Options")]
        public bool generateTerrain   = true;
        public bool generateBuildings = true;
        public bool generateStreets   = true;

        [Tooltip("ON = density-based warping (compresses empty hills, preserves Ermoupoli).\n" +
                 "OFF = uniform proportional scaling (true island shape, just smaller).")]
        public bool useAdaptiveWarping = true;

        [Tooltip("ON = blend JSON elevation data into the terrain heightmap.\n" +
                 "OFF = use heightmap only.")]
        public bool useElevationBlending = true;

        [Tooltip("DEBUG: Ignore the heightmap completely and build terrain from " +
                 "JSON elevation data only.  Useful to verify elevation values.")]
        public bool elevationOnlyDebug = false;

        [Tooltip("ON = smart blend: use elevation data where dense, heightmap where sparse.\n" +
                 "OFF = uniform blend controlled by Blend Weight.")]
        public bool useDensityBasedBlending = true;

        // =================================================================
        //  RUNTIME REFERENCES (read-only in Inspector)
        // =================================================================

        [Header("Generated (read-only)")]
        [SerializeField] Terrain _generatedTerrain;
        [SerializeField] List<GameObject> _generatedObjects = new List<GameObject>();

        // Working state (not serialised)
        SyrosMapData    _mapData;
        SyrosGeoConverter _converter;

        // =================================================================
        //  PUBLIC ACCESSORS
        // =================================================================

        /// <summary>The geo converter instance (available after generation).</summary>
        public SyrosGeoConverter Converter => _converter;

        /// <summary>The parsed map data (available after generation).</summary>
        public SyrosMapData MapData => _mapData;

        /// <summary>The generated terrain (available after generation).</summary>
        public Terrain GeneratedTerrain => _generatedTerrain;

        // =================================================================
        //  GENERATE WORLD — Full pipeline
        // =================================================================

        /// <summary>
        /// Run the complete world-generation pipeline (parse → warp → terrain
        /// → buildings / streets).
        /// </summary>
        public void GenerateWorld()
        {
            if (!ValidateInputs()) return;

            var timer = System.Diagnostics.Stopwatch.StartNew();
            ClearGenerated();

            // ── Step 1: Parse ───────────────────────────────────────────
            Debug.Log("[SyrosWorldBuilder] Step 1/4: Parsing OSM data…");
            _mapData = SyrosOSMParser.Parse(osmJsonData);
            if (_mapData.buildings.Count == 0 && _mapData.streets.Count == 0)
            {
                Debug.LogError("[SyrosWorldBuilder] No features parsed — check JSON data.");
                return;
            }

            // ── Step 2: Coordinate converter ────────────────────────────
            Debug.Log("[SyrosWorldBuilder] Step 2/4: Building coordinate warp tables…");
            _converter = new SyrosGeoConverter(config);
            if (useAdaptiveWarping)
                _converter.Initialize(_mapData);
            else
                _converter.InitializeUniform();

            // ── Step 3: Terrain ─────────────────────────────────────────
            if (generateTerrain && heightmapTexture != null)
            {
                Debug.Log("[SyrosWorldBuilder] Step 3/4: Generating terrain…");
                if (_generatedTerrain == null)
                    _generatedTerrain = SyrosTerrainGenerator.CreateTerrainObject(transform);

                // Collect elevation data from JSON when any elevation mode is active
                List<ElevationPoint> elevPts = null;
                if (useElevationBlending || elevationOnlyDebug)
                {
                    elevPts = _mapData.GetElevationPoints();
                    Debug.Log($"[SyrosWorldBuilder]   Collected {elevPts.Count} " +
                              "elevation points for terrain blending.");
                }

                // Push per-run mode flags into config (transient, not serialised)
                config.elevationOnlyDebug        = elevationOnlyDebug;
                config.useDensityBasedBlending    = useDensityBasedBlending;

                SyrosTerrainGenerator.GenerateTerrain(
                    _generatedTerrain, heightmapTexture, _converter, config, elevPts);
            }
            else if (generateTerrain)
            {
                Debug.LogWarning("[SyrosWorldBuilder] Terrain enabled but no heightmap assigned.");
            }

            // ── Step 4: Buildings & streets ─────────────────────────────
            Debug.Log("[SyrosWorldBuilder] Step 4/4: Placing buildings and streets…");

            if (generateBuildings)
            {
                var buildings = SyrosBuildingPlacer.PlaceBuildings(
                    _mapData.buildings, _converter, config,
                    _generatedTerrain, defaultBuildingPrefab, poiBuildingPrefab,
                    transform);
                _generatedObjects.AddRange(buildings);
            }

            if (generateStreets)
            {
                var streets = SyrosStreetRenderer.RenderStreets(
                    _mapData.streets, _converter, config,
                    _generatedTerrain, transform);
                _generatedObjects.AddRange(streets);
            }

            // ── Done ────────────────────────────────────────────────────
            timer.Stop();
            Debug.Log($"[SyrosWorldBuilder] World generation complete in " +
                      $"{timer.Elapsed.TotalSeconds:F1}s — " +
                      $"{_generatedObjects.Count} objects created.");

            #if UNITY_EDITOR
            UnityEditor.EditorUtility.ClearProgressBar();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
            #endif
        }

        // =================================================================
        //  CLEAR — Remove all generated objects
        // =================================================================

        /// <summary>Destroy all previously generated objects and terrain.</summary>
        public void ClearGenerated()
        {
            foreach (var go in _generatedObjects)
                if (go != null) DestroyImmediate(go);
            _generatedObjects.Clear();

            if (_generatedTerrain != null)
            {
                DestroyImmediate(_generatedTerrain.gameObject);
                _generatedTerrain = null;
            }

            // Clean up any leftover children (e.g. from interrupted builds)
            while (transform.childCount > 0)
                DestroyImmediate(transform.GetChild(0).gameObject);

            Debug.Log("[SyrosWorldBuilder] Cleared all generated objects.");
        }

        // =================================================================
        //  REGENERATE TERRAIN ONLY — Faster iteration
        // =================================================================

        /// <summary>
        /// Re-run only the terrain generation step (skips buildings / streets).
        /// Parses and warps only if needed.
        /// </summary>
        public void RegenerateTerrain()
        {
            if (!ValidateInputs()) return;

            // Lazy-parse if not already done
            if (_mapData == null)
                _mapData = SyrosOSMParser.Parse(osmJsonData);

            // Lazy-init converter if not already done
            if (_converter == null || !_converter.IsInitialized)
            {
                _converter = new SyrosGeoConverter(config);
                if (useAdaptiveWarping)
                    _converter.Initialize(_mapData);
                else
                    _converter.InitializeUniform();
            }

            if (_generatedTerrain == null)
                _generatedTerrain = SyrosTerrainGenerator.CreateTerrainObject(transform);

            List<ElevationPoint> elevPts = null;
            if (useElevationBlending || elevationOnlyDebug)
                elevPts = _mapData.GetElevationPoints();

            config.elevationOnlyDebug     = elevationOnlyDebug;
            config.useDensityBasedBlending = useDensityBasedBlending;

            SyrosTerrainGenerator.GenerateTerrain(
                _generatedTerrain, heightmapTexture, _converter, config, elevPts);
        }

        // =================================================================
        //  VALIDATION
        // =================================================================

        /// <summary>Check that required references are assigned.</summary>
        bool ValidateInputs()
        {
            bool valid = true;

            if (config == null)
            {
                Debug.LogError("[SyrosWorldBuilder] Config not assigned. " +
                               "Create via Assets → Create → Syros → World Config.");
                valid = false;
            }

            if (osmJsonData == null)
            {
                Debug.LogError("[SyrosWorldBuilder] OSM JSON data not assigned.");
                valid = false;
            }

            if (generateTerrain && heightmapTexture == null)
                Debug.LogWarning("[SyrosWorldBuilder] Heightmap not assigned — " +
                                 "terrain will be flat.");

            return valid;
        }
    }
}
