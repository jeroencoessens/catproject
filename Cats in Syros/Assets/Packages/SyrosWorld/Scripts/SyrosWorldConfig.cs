using UnityEngine;

namespace SyrosWorld
{
    /// <summary>
    /// Controls how buildings are positioned and rotated relative to the terrain.
    /// </summary>
    public enum BuildingPlacementMode
    {
        /// <summary>
        /// Default: buildings sit flat (level rotation) on the terrain surface.
        /// The bottom face rests on the sampled terrain height.
        /// </summary>
        Level = 0,

        /// <summary>
        /// Buildings are rotated to match the terrain slope/normal at their centroid.
        /// Useful for buildings on steep hillsides where a level base looks wrong.
        /// </summary>
        SlopeAligned = 1,

        /// <summary>
        /// Foundation mode: buildings keep level rotation but are positioned so
        /// their TOP face sits at (or just above) the terrain surface.
        /// The mesh extends downward, acting as a foundation / basement.
        /// No matter the mesh height, only the cap is visible above terrain.
        /// </summary>
        Foundation = 2
    }

    /// <summary>
    /// Central configuration asset for the Syros world-generation pipeline.
    ///
    /// Create via: Assets → Create → Syros → World Config
    ///
    /// <b>Scale note (1:1 metre mapping):</b>
    /// The terrain Y axis is controlled by <see cref="terrainMaxHeight"/>.
    /// Heightmap pixels and elevation data are normalised to [0,1] then
    /// multiplied by this value to produce the final Unity Y coordinate.
    /// For a true 1:1 metre mapping set <c>terrainMaxHeight</c> to at least
    /// the highest real-world elevation in the data (418 m for Syros).
    ///
    /// <b>IMPORTANT:</b> Changing the default value in code does NOT update
    /// already-serialised <c>.asset</c> files. If the terrain height looks
    /// wrong, inspect the ScriptableObject asset in the Inspector and set the
    /// value there — that is the value Unity actually reads at runtime.
    /// </summary>
    [CreateAssetMenu(fileName = "SyrosWorldConfig", menuName = "Syros/World Config")]
    public class SyrosWorldConfig : ScriptableObject
    {
        // =====================================================================
        //  GEO BOUNDS
        // =====================================================================

        [Header("OSM Geo Bounds (auto-detected defaults)")]

        [Tooltip("Western-most longitude of the OSM data extent.")]
        public double minLon = 24.858819;

        [Tooltip("Eastern-most longitude of the OSM data extent.")]
        public double maxLon = 24.972383;

        [Tooltip("Southern-most latitude of the OSM data extent.")]
        public double minLat = 37.363064;

        [Tooltip("Northern-most latitude of the OSM data extent.")]
        public double maxLat = 37.512692;

        // =====================================================================
        //  HEIGHTMAP BOUNDS
        // =====================================================================

        [Header("Heightmap Geo Bounds")]
        [Tooltip("Override when the heightmap covers a different geographic area " +
                 "than the OSM data. Defaults match the OSM bounds above.")]
        public double heightmapMinLon = 24.858819;
        public double heightmapMaxLon = 24.972383;
        public double heightmapMinLat = 37.363064;
        public double heightmapMaxLat = 37.512692;

        // =====================================================================
        //  WORLD SCALE
        //  These three values define the physical size of the Unity Terrain.
        //  terrainData.size = (targetWorldWidth, terrainMaxHeight, targetWorldLength)
        // =====================================================================

        [Header("World Scale")]

        [Tooltip("Target world width in Unity units (east–west). " +
                 "Real Syros island ≈ 10 km; default 2 000 gives a 1:5 horizontal scale.")]
        public float targetWorldWidth = 2000f;

        [Tooltip("Target world length in Unity units (north–south). " +
                 "Real Syros island ≈ 16.5 km; default 3 300 gives a 1:5 horizontal scale.")]
        public float targetWorldLength = 3300f;

        [Tooltip("Maximum terrain elevation in Unity Y units. " +
                 "This is the Y component of TerrainData.size.  A normalised " +
                 "height of 1.0 corresponds to exactly this many Unity units.\n\n" +
                 "For 1:1 vertical metre accuracy set this >= the highest real-world " +
                 "elevation (418 m for Syros).  Any elevation above this value is clamped.\n\n" +
                 "★ This is the SINGLE SCALE FACTOR that converts normalised [0,1] " +
                 "heights to world Y.  There are NO other hidden multipliers.")]
        public float terrainMaxHeight = 450f;

        // =====================================================================
        //  ADAPTIVE WARPING
        //  Only active when the WorldBuilder's "Use Adaptive Warping" toggle is ON.
        //  Uses a CDF-based approach to compress empty hills and preserve dense
        //  urban areas (Ermoupoli) at near-original proportions.
        // =====================================================================

        [Header("Adaptive Warping")]

        [Tooltip("Number of histogram bins along the X (longitude) axis.")]
        public int warpBinsX = 128;

        [Tooltip("Number of histogram bins along the Y (latitude) axis.")]
        public int warpBinsY = 128;

        [Tooltip("Minimum density floor to prevent zero-width dead zones (0–1 relative).")]
        [Range(0.01f, 1f)]
        public float minDensityFloor = 0.05f;

        [Tooltip("Gaussian smoothing sigma (in bins) to soften warp transitions.")]
        [Range(0f, 20f)]
        public float smoothingSigma = 5f;

        [Tooltip("Density exponent: controls how aggressively urban areas expand. " +
                 "1 = linear CDF, >1 = more expansion around dense areas.")]
        [Range(0.5f, 3f)]
        public float densityExponent = 1.2f;

        // =====================================================================
        //  TERRAIN
        // =====================================================================

        [Header("Terrain")]

        [Tooltip("Terrain heightmap resolution (must be power-of-2 + 1, " +
                 "e.g. 257, 513, 1025).  Higher = more detail but slower.")]
        public int terrainResolution = 513;

        // =====================================================================
        //  ELEVATION DATA BLENDING
        //  The JSON GeoJSON carries per-feature elevation values (metres ASL).
        //  These are rasterised into an intermediate grid via inverse-distance
        //  weighting (IDW) and blended with the heightmap to correct inaccuracies,
        //  especially in dense urban areas like Ermoupoli.
        //
        //  Three blend modes are available (selected on the WorldBuilder):
        //    1. Elevation Only (debug)   – heightmap is entirely ignored.
        //    2. Density-Based Blend      – heightmap fills in where JSON data is sparse.
        //    3. Uniform Blend            – a fixed weight mixes the two everywhere.
        //
        //  Normalisation note:
        //    normValue = Clamp01(elevation / terrainMaxHeight)
        //    → so Unity Y = normValue × terrainMaxHeight ≈ real-world metres.
        // =====================================================================

        [Header("Elevation Data Blending")]

        [Tooltip("How much weight the JSON elevation data has versus the heightmap " +
                 "in Uniform Blend mode.\n0 = heightmap only, 1 = elevation data only.")]
        [Range(0f, 1f)]
        public float elevationBlendWeight = 0.5f;

        [Tooltip("Resolution of the intermediate elevation raster grid. " +
                 "Higher = more spatial detail from JSON data but slower to build.")]
        public int elevationGridResolution = 256;

        [Tooltip("Gaussian smoothing sigma (in grid cells) applied after IDW rasterisation. " +
                 "Prevents staircase artefacts from integer-valued elevations.")]
        [Range(0f, 10f)]
        public float elevationSmoothingSigma = 3f;

        [Tooltip("IDW scatter radius (in grid cells).  Each elevation point " +
                 "influences this many cells around it.  Larger = smoother but slower.")]
        public int elevationSearchRadius = 8;

        [Tooltip("Maximum real-world elevation present in the JSON data (metres). " +
                 "Used only for the warning log; actual normalisation uses terrainMaxHeight.")]
        public float elevationDataMaxHeight = 420f;

        [Tooltip("Density-Based Blend falloff radius (in grid cells). " +
                 "Within this distance of data-rich areas the heightmap is suppressed; " +
                 "beyond this the heightmap is fully used.  Larger = bigger 'trust zone' " +
                 "around elevation data.")]
        public int densityFalloffRadius = 16;

        // =====================================================================
        //  SEA-LEVEL OVERRIDE
        //  When enabled, heightmap pixels at or below the black threshold are
        //  treated as sea and forced to 0 height, overriding any elevation
        //  blending.  A narrow transition band smooths the coastline.
        // =====================================================================

        [Header("Sea-Level Override")]

        [Tooltip("When ON, pure-black heightmap pixels (sea) override all elevation " +
                 "blending and force height to 0. Prevents JSON elevation data from " +
                 "raising ocean areas.")]
        public bool seaLevelOverride = true;

        [Tooltip("Heightmap values at or below this threshold are considered sea. " +
                 "0.0 = only pure black; raise slightly to catch near-black coastal pixels.")]
        [Range(0f, 0.05f)]
        public float seaBlackThreshold = 0.001f;

        [Tooltip("Transition width above the threshold where height fades to zero. " +
                 "Prevents a hard cliff at the coastline. " +
                 "0 = hard edge, 0.02 = gentle fade.")]
        [Range(0f, 0.1f)]
        public float seaTransitionWidth = 0.01f;

        // ── Transient flags ─────────────────────────────────────────────
        // Set by SyrosWorldBuilder before each generation run.
        // Not serialised — they mirror the builder's per-run toggle state.
        [System.NonSerialized] public bool elevationOnlyDebug;
        [System.NonSerialized] public bool useDensityBasedBlending = true;

        // =====================================================================
        //  STREETS
        // =====================================================================

        [Header("Streets")]

        [Tooltip("Y offset above terrain surface to prevent z-fighting.")]
        public float streetYOffset = 0.3f;

        public float primaryStreetWidth   = 5f;
        public float secondaryStreetWidth = 3.5f;
        public float residentialStreetWidth = 2.5f;
        public float footwayWidth  = 1.2f;
        public float defaultStreetWidth = 2f;
        public float stepsWidth    = 1f;

        // =====================================================================
        //  BUILDINGS
        // =====================================================================

        [Header("Buildings")]

        [Tooltip("How buildings are positioned relative to the terrain surface.\n\n" +
                 "Level: flat rotation, sitting on terrain.\n" +
                 "SlopeAligned: rotated to match terrain normal.\n" +
                 "Foundation: level rotation but top face at terrain level, mesh extends downward.")]
        public BuildingPlacementMode buildingPlacementMode = BuildingPlacementMode.Level;

        [Tooltip("When ON and a prefab is assigned, the prefab's own materials are kept.\n" +
                 "When OFF (or no prefab), materials are generated from the colours below.")]
        public bool usePrefabMaterials = true;

        [Tooltip("Foundation mode: extra units the top face sits ABOVE the terrain.\n" +
                 "0 = flush with terrain, positive = cap protrudes above.")]
        [Range(0f, 5f)]
        public float foundationTopOffset = 0.1f;

        [Tooltip("Default height for unnamed buildings (Unity units).")]
        public float defaultBuildingHeight = 5f;

        [Tooltip("Height for named POI buildings (Unity units).")]
        public float poiBuildingHeight = 8f;

        [Tooltip("Extra Y offset added to every building above the terrain surface.")]
        public float buildingYOffset = 0f;

        // =====================================================================
        //  COLOURS & MATERIALS
        // =====================================================================

        [Header("Visual")]
        public Color defaultBuildingColor  = new Color(0.75f, 0.72f, 0.68f, 1f);
        public Color poiBuildingColor      = new Color(1f, 0.45f, 0.15f, 1f);
        public Color primaryStreetColor    = new Color(0.35f, 0.35f, 0.35f, 1f);
        public Color secondaryStreetColor  = new Color(0.45f, 0.45f, 0.42f, 1f);
        public Color footpathColor         = new Color(0.6f, 0.55f, 0.5f, 1f);

        // =====================================================================
        //  COMPUTED HELPERS
        // =====================================================================

        /// <summary>Longitude span of the OSM extent.</summary>
        public double LonSpan => maxLon - minLon;

        /// <summary>Latitude span of the OSM extent.</summary>
        public double LatSpan => maxLat - minLat;

        /// <summary>
        /// Look up the street width (Unity units) for a given OSM highway tag.
        /// Falls back to <see cref="defaultStreetWidth"/> for unrecognised types.
        /// </summary>
        public float GetStreetWidth(string highwayType)
        {
            if (string.IsNullOrEmpty(highwayType)) return defaultStreetWidth;
            switch (highwayType)
            {
                case "primary":
                case "primary_link":
                case "trunk":
                case "trunk_link":      return primaryStreetWidth;

                case "secondary":
                case "secondary_link":
                case "tertiary":
                case "tertiary_link":   return secondaryStreetWidth;

                case "residential":
                case "living_street":
                case "unclassified":
                case "service":         return residentialStreetWidth;

                case "footway":
                case "path":
                case "track":
                case "pedestrian":      return footwayWidth;

                case "steps":           return stepsWidth;

                default:                return defaultStreetWidth;
            }
        }

        /// <summary>
        /// Look up the street colour for a given OSM highway tag.
        /// </summary>
        public Color GetStreetColor(string highwayType)
        {
            if (string.IsNullOrEmpty(highwayType)) return primaryStreetColor;
            switch (highwayType)
            {
                case "primary":
                case "primary_link":
                case "trunk":
                case "trunk_link":
                case "secondary":
                case "secondary_link":
                case "tertiary":
                case "tertiary_link":   return primaryStreetColor;

                case "residential":
                case "living_street":
                case "unclassified":
                case "service":         return secondaryStreetColor;

                case "footway":
                case "path":
                case "track":
                case "pedestrian":
                case "steps":           return footpathColor;

                default:                return primaryStreetColor;
            }
        }
    }
}
