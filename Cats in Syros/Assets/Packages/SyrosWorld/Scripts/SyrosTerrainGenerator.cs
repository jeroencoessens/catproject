using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SyrosWorld
{
    /// <summary>
    /// Generates a Unity <see cref="Terrain"/> from a heightmap texture,
    /// using the <see cref="SyrosGeoConverter"/>'s inverse warp to properly
    /// sample the heightmap into warped world-space.
    ///
    /// Optionally blends in elevation data from the GeoJSON to correct areas
    /// where the heightmap is inaccurate (especially dense urban areas like
    /// Ermoupoli whose small-scale hills are smoothed out by satellite DEM).
    ///
    /// <b>Height scale chain (no hidden multipliers):</b>
    /// <code>
    /// normalisedHeight ∈ [0, 1]   (from heightmap pixel or elevation/terrainMaxHeight)
    ///         ↓  TerrainData.SetHeights(…)
    /// Unity Y = normalisedHeight × terrainMaxHeight
    /// </code>
    /// <c>terrainMaxHeight</c> is the sole scale factor.  Set it ≥ the highest
    /// real-world elevation for 1:1 metre accuracy.
    ///
    /// <b>Three blend modes</b> (selected via transient flags on the config):
    /// <list type="bullet">
    ///   <item><b>Elevation Only (debug)</b> — heightmap is entirely ignored;
    ///         terrain built purely from JSON elevation grid.</item>
    ///   <item><b>Density-Based Blend</b> — where elevation data is dense
    ///         (high confidence) it overrides the heightmap; where sparse /
    ///         absent the heightmap fills in.</item>
    ///   <item><b>Uniform Blend</b> — a fixed blend weight mixes the two
    ///         everywhere that has any elevation data.</item>
    /// </list>
    /// </summary>
    public static class SyrosTerrainGenerator
    {
        // =================================================================
        //  PUBLIC API
        // =================================================================

        /// <summary>
        /// Generate terrain from a heightmap only (no elevation blending).
        /// Convenience overload that passes <c>null</c> for elevation data.
        /// </summary>
        public static void GenerateTerrain(
            Terrain terrain,
            Texture2D heightmap,
            SyrosGeoConverter converter,
            SyrosWorldConfig config)
        {
            GenerateTerrain(terrain, heightmap, converter, config, null);
        }

        /// <summary>
        /// Generate terrain from a heightmap, optionally blended with
        /// sparse elevation points from the GeoJSON data.
        /// </summary>
        public static void GenerateTerrain(
            Terrain terrain,
            Texture2D heightmap,
            SyrosGeoConverter converter,
            SyrosWorldConfig config,
            List<ElevationPoint> elevationPoints)
        {
            // ── Validate inputs ─────────────────────────────────────────
            if (terrain == null)
            {
                Debug.LogError("[SyrosTerrainGenerator] Terrain is null.");
                return;
            }
            if (heightmap == null)
            {
                Debug.LogError("[SyrosTerrainGenerator] Heightmap texture is null.");
                return;
            }
            if (!converter.IsInitialized)
            {
                Debug.LogError("[SyrosTerrainGenerator] GeoConverter not initialised.");
                return;
            }

            int res = config.terrainResolution;

            // ── Prepare TerrainData ─────────────────────────────────────
            TerrainData terrainData = terrain.terrainData;
            if (terrainData == null)
            {
                terrainData = new TerrainData();
                terrain.terrainData = terrainData;
                var col = terrain.GetComponent<TerrainCollider>();
                if (col != null) col.terrainData = terrainData;
            }

            terrainData.heightmapResolution = res;

            // ★ This is the SINGLE place that sets the terrain's physical size.
            //   terrainMaxHeight is the only vertical scale factor — there are
            //   no other hidden multipliers anywhere in the pipeline.
            terrainData.size = new Vector3(
                config.targetWorldWidth,
                config.terrainMaxHeight,
                config.targetWorldLength);

            Debug.Log($"[SyrosTerrainGenerator] TerrainData.size = " +
                      $"({config.targetWorldWidth}, {config.terrainMaxHeight}, " +
                      $"{config.targetWorldLength})  ← terrainMaxHeight is the " +
                      $"sole vertical scale factor.");

            // ── Build elevation grid from JSON data (if applicable) ─────
            float[,] elevGrid   = null;  // normalised elevation values [0,1]
            float[,] elevWeight = null;  // per-cell confidence [0,1]

            bool hasElevData  = elevationPoints != null && elevationPoints.Count > 0
                                && config.elevationBlendWeight > 0f;
            bool elevOnly     = elevationPoints != null && elevationPoints.Count > 0
                                && config.elevationOnlyDebug;
            bool densityBlend = elevationPoints != null && elevationPoints.Count > 0
                                && config.useDensityBasedBlending
                                && !config.elevationOnlyDebug;

            if (hasElevData || elevOnly || densityBlend)
            {
                Debug.Log($"[SyrosTerrainGenerator] Rasterising {elevationPoints.Count} " +
                          "elevation points into IDW grid…");
                BuildElevationGrid(elevationPoints, converter, config,
                                   out elevGrid, out elevWeight);
            }

            // ── Fill the height array ───────────────────────────────────
            //   For every pixel in the output heightmap we:
            //     1. Compute the normalised world position.
            //     2. Inverse-warp it to a geo position.
            //     3. Sample the source heightmap at that geo position.
            //     4. Optionally blend with the elevation grid.
            float[,] heights = new float[res, res];

            // Cache sea override parameters for the inner loop
            bool  seaOverride   = config.seaLevelOverride;
            float seaThreshold  = config.seaBlackThreshold;
            float seaTransition = config.seaTransitionWidth;

            for (int y = 0; y < res; y++)
            {
                float worldNormY = (float)y / (res - 1);

                for (int x = 0; x < res; x++)
                {
                    float worldNormX = (float)x / (res - 1);

                    // Inverse warp: world → geo  (accounts for adaptive warping)
                    Vector2 geoNorm = converter.WorldNormToGeoNorm(worldNormX, worldNormY);

                    // Sample the heightmap in geo space → [0,1]
                    float hMap = converter.SampleHeightmap(heightmap, geoNorm.x, geoNorm.y);

                    // ── Sea-level override check ────────────────────────
                    // If the raw heightmap pixel is at or below the black
                    // threshold, this is sea — skip all blending and force
                    // height toward 0 with a smooth transition.
                    if (seaOverride && hMap <= seaThreshold)
                    {
                        heights[y, x] = 0f;
                    }
                    else if (seaOverride && seaTransition > 0f && hMap < seaThreshold + seaTransition)
                    {
                        // Transition band: lerp blended height toward 0 at the coast
                        float t = (hMap - seaThreshold) / seaTransition; // 0 at shore, 1 at full land
                        float blended = ComputeBlendedHeight(
                            hMap, worldNormX, worldNormY,
                            elevOnly, densityBlend, hasElevData,
                            elevGrid, elevWeight, config);
                        heights[y, x] = blended * t;
                    }
                    else
                    {
                        // ── Normal blend modes ──────────────────────────
                        heights[y, x] = ComputeBlendedHeight(
                            hMap, worldNormX, worldNormY,
                            elevOnly, densityBlend, hasElevData,
                            elevGrid, elevWeight, config);
                    }
                }

                // Progress bar (editor only)
                #if UNITY_EDITOR
                if (y % 100 == 0)
                {
                    float pct = (float)y / res;
                    UnityEditor.EditorUtility.DisplayProgressBar(
                        "Generating Terrain",
                        $"Row {y}/{res} ({pct * 100f:F0}%)", pct);
                }
                #endif
            }

            #if UNITY_EDITOR
            UnityEditor.EditorUtility.ClearProgressBar();
            #endif

            // ── Apply to terrain ────────────────────────────────────────
            terrainData.SetHeights(0, 0, heights);

            // Assign a render-pipeline-appropriate material (prevents pink terrain)
            AssignTerrainMaterial(terrain);

            // Position terrain at the world origin
            terrain.transform.position = Vector3.zero;

            // ── Summary log ─────────────────────────────────────────────
            string modeLabel = elevOnly          ? " (ELEVATION DATA ONLY — debug)" :
                               densityBlend      ? " (density-based blend)" :
                               hasElevData       ? $" (uniform blend, weight={config.elevationBlendWeight:F2})" :
                                                   "";
            Debug.Log($"[SyrosTerrainGenerator] Terrain generated: {res}×{res}, " +
                      $"size {config.targetWorldWidth}×{config.terrainMaxHeight}×" +
                      $"{config.targetWorldLength}{modeLabel}");
        }

        // =================================================================
        //  BLEND-MODE HELPER
        // =================================================================

        /// <summary>
        /// Compute the final blended height for a single pixel, applying the
        /// active blend mode (elevation-only / density / uniform / pure heightmap).
        /// Extracted so both the normal path and the sea-transition path can share it.
        /// </summary>
        static float ComputeBlendedHeight(
            float hMap,
            float worldNormX, float worldNormY,
            bool elevOnly, bool densityBlend, bool hasElevData,
            float[,] elevGrid, float[,] elevWeight,
            SyrosWorldConfig config)
        {
            if (elevOnly && elevGrid != null)
            {
                return SampleGrid(elevGrid, worldNormX, worldNormY);
            }
            if (densityBlend && elevGrid != null)
            {
                float hElev = SampleGrid(elevGrid,   worldNormX, worldNormY);
                float w     = SampleGrid(elevWeight, worldNormX, worldNormY);
                return Mathf.Lerp(hMap, hElev, w);
            }
            if (hasElevData && elevGrid != null)
            {
                float hElev = SampleGrid(elevGrid,   worldNormX, worldNormY);
                float w     = SampleGrid(elevWeight, worldNormX, worldNormY);
                float blend = w * config.elevationBlendWeight;
                return Mathf.Lerp(hMap, hElev, blend);
            }
            return hMap;
        }

        // =================================================================
        //  ELEVATION GRID CONSTRUCTION
        // =================================================================

        /// <summary>
        /// Rasterise sparse elevation points into a regular grid using
        /// inverse-distance-weighted (IDW) accumulation, then optionally
        /// dilate the confidence mask and apply Gaussian smoothing.
        ///
        /// <b>Normalisation:</b>
        /// <c>normHeight = Clamp01(elevation / terrainMaxHeight)</c>
        /// so that Unity Y = normHeight × terrainMaxHeight ≈ real metres.
        ///
        /// <b>Complexity:</b> O(N × R²) where N = number of points, R = search radius.
        /// </summary>
        static void BuildElevationGrid(
            List<ElevationPoint> points,
            SyrosGeoConverter converter,
            SyrosWorldConfig config,
            out float[,] grid,
            out float[,] weight)
        {
            int gRes = config.elevationGridResolution;
            grid   = new float[gRes, gRes];
            weight = new float[gRes, gRes];

            // Temporary accumulators for IDW scatter pass
            float[,] wSum = new float[gRes, gRes];   // sum of weights
            float[,] hSum = new float[gRes, gRes];   // sum of weighted heights

            int   radius      = Mathf.Max(config.elevationSearchRadius, 1);
            float maxElevData = Mathf.Max(config.elevationDataMaxHeight, 1f);

            // ★ We normalise relative to terrainMaxHeight (NOT elevationDataMaxHeight)
            //   so that: Unity Y = normValue × terrainMaxHeight ≈ real metres.
            float normDivisor = Mathf.Max(config.terrainMaxHeight, 1f);

            // Warn if data can exceed the terrain height cap
            if (maxElevData > config.terrainMaxHeight)
            {
                Debug.LogWarning(
                    $"[SyrosTerrainGenerator] elevationDataMaxHeight ({maxElevData:F0} m) > " +
                    $"terrainMaxHeight ({config.terrainMaxHeight:F0}).  Peaks above " +
                    $"{config.terrainMaxHeight:F0} m will be clamped.  Set terrainMaxHeight ≥ " +
                    $"{maxElevData:F0} for 1:1 metre accuracy.");
            }

            // ── Scatter each elevation point into surrounding grid cells ─
            foreach (var pt in points)
            {
                // Forward warp: geo → normalised world position
                Vector3 worldPos = converter.GeoToWorld(pt.lon, pt.lat);
                float nx = worldPos.x / config.targetWorldWidth;
                float nz = worldPos.z / config.targetWorldLength;

                // Map to grid cell
                float gx = nx * (gRes - 1);
                float gy = nz * (gRes - 1);
                int cx = Mathf.RoundToInt(gx);
                int cy = Mathf.RoundToInt(gy);

                // Normalise elevation to [0,1]
                float hNorm = Mathf.Clamp01(pt.elevation / normDivisor);

                // Accumulate into nearby cells using IDW: w = 1/(1+d²)
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int iy = cy + dy;
                    if (iy < 0 || iy >= gRes) continue;

                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int ix = cx + dx;
                        if (ix < 0 || ix >= gRes) continue;

                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        if (dist > radius) continue;

                        float w = 1f / (1f + dist * dist);
                        wSum[iy, ix] += w;
                        hSum[iy, ix] += w * hNorm;
                    }
                }
            }

            // ── Normalise accumulators → grid height + confidence weight ─
            for (int y = 0; y < gRes; y++)
            {
                for (int x = 0; x < gRes; x++)
                {
                    if (wSum[y, x] > 1e-6f)
                    {
                        grid[y, x]   = hSum[y, x] / wSum[y, x];
                        // Confidence saturates quickly: a few nearby points → ~1.0.
                        weight[y, x] = Mathf.Clamp01(wSum[y, x] / 3f);
                    }
                    // else: grid=0, weight=0  (no data)
                }
            }

            // ── Dilate the confidence mask ──────────────────────────────
            // Expands the "trust zone" beyond the data points so the
            // heightmap doesn't abruptly take over at the data boundary.
            int falloff = Mathf.Max(config.densityFalloffRadius, 0);
            if (falloff > 0)
                weight = DilateWeight(weight, falloff);

            // ── Gaussian smooth for natural terrain ─────────────────────
            float sigma = config.elevationSmoothingSigma;
            if (sigma > 0.1f)
            {
                grid   = GaussianSmooth2D(grid,   sigma);
                weight = GaussianSmooth2D(weight, sigma);

                // Re-clamp weight after smoothing
                for (int y = 0; y < gRes; y++)
                    for (int x = 0; x < gRes; x++)
                        weight[y, x] = Mathf.Clamp01(weight[y, x]);
            }

            // ── Stats log ───────────────────────────────────────────────
            int filledCells = 0;
            for (int y = 0; y < gRes; y++)
                for (int x = 0; x < gRes; x++)
                    if (weight[y, x] > 0.01f) filledCells++;

            float coverage = 100f * filledCells / (gRes * gRes);
            Debug.Log($"[SyrosTerrainGenerator] Elevation grid: {gRes}×{gRes}, " +
                      $"{filledCells} cells with data ({coverage:F1}% coverage), " +
                      $"sigma={sigma:F1}, normDivisor={normDivisor:F0}");
        }

        // =================================================================
        //  GRID SAMPLING
        // =================================================================

        /// <summary>
        /// Bilinear-sample a 2D grid at normalised coordinates [0,1].
        /// </summary>
        static float SampleGrid(float[,] grid, float normX, float normY)
        {
            int h = grid.GetLength(0);
            int w = grid.GetLength(1);

            float px = normX * (w - 1);
            float py = normY * (h - 1);

            int x0 = Mathf.Clamp(Mathf.FloorToInt(px), 0, w - 2);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(py), 0, h - 2);
            float fx = px - x0;
            float fy = py - y0;

            float v00 = grid[y0,     x0];
            float v10 = grid[y0,     x0 + 1];
            float v01 = grid[y0 + 1, x0];
            float v11 = grid[y0 + 1, x0 + 1];

            return Mathf.Lerp(
                Mathf.Lerp(v00, v10, fx),
                Mathf.Lerp(v01, v11, fx),
                fy);
        }

        // =================================================================
        //  GAUSSIAN SMOOTH (separable 2-pass)
        // =================================================================

        /// <summary>
        /// Separable Gaussian blur on a 2D float array.
        /// Kernel radius = ceil(sigma × 3).
        /// </summary>
        static float[,] GaussianSmooth2D(float[,] src, float sigma)
        {
            int h = src.GetLength(0);
            int w = src.GetLength(1);
            int radius = Mathf.CeilToInt(sigma * 3f);

            // Build 1-D kernel
            float[] kernel = new float[radius * 2 + 1];
            float kSum = 0f;
            for (int i = -radius; i <= radius; i++)
            {
                float v = Mathf.Exp(-(i * i) / (2f * sigma * sigma));
                kernel[i + radius] = v;
                kSum += v;
            }
            for (int i = 0; i < kernel.Length; i++) kernel[i] /= kSum;

            // Horizontal pass
            float[,] tmp = new float[h, w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sx = Mathf.Clamp(x + k, 0, w - 1);
                        sum += src[y, sx] * kernel[k + radius];
                    }
                    tmp[y, x] = sum;
                }

            // Vertical pass
            float[,] dst = new float[h, w];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sy = Mathf.Clamp(y + k, 0, h - 1);
                        sum += tmp[sy, x] * kernel[k + radius];
                    }
                    dst[y, x] = sum;
                }

            return dst;
        }

        // =================================================================
        //  WEIGHT DILATION
        // =================================================================

        /// <summary>
        /// Expand the confidence mask using a max-filter with linear falloff.
        /// Ensures areas near data-rich cells keep a high weight even if they
        /// have no direct data points, creating a smooth transition zone to
        /// the heightmap.
        /// </summary>
        static float[,] DilateWeight(float[,] src, int radius)
        {
            int h = src.GetLength(0);
            int w = src.GetLength(1);
            float[,] dst = new float[h, w];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float best = src[y, x];

                    for (int dy = -radius; dy <= radius && best < 0.999f; dy++)
                    {
                        int iy = y + dy;
                        if (iy < 0 || iy >= h) continue;

                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int ix = x + dx;
                            if (ix < 0 || ix >= w) continue;

                            float dist = Mathf.Sqrt(dx * dx + dy * dy);
                            if (dist > radius) continue;

                            // Linear falloff: 1 at centre, 0 at the radius edge
                            float candidate = src[iy, ix] * (1f - dist / radius);
                            if (candidate > best) best = candidate;
                        }
                    }

                    dst[y, x] = Mathf.Clamp01(best);
                }
            }

            return dst;
        }

        // =================================================================
        //  FACTORY
        // =================================================================

        /// <summary>
        /// Create a new <see cref="Terrain"/> GameObject complete with
        /// <see cref="TerrainCollider"/>, ready for generation.
        /// </summary>
        public static Terrain CreateTerrainObject(Transform parent = null)
        {
            var go = new GameObject("SyrosTerrain");
            if (parent != null) go.transform.SetParent(parent);

            var terrainData = new TerrainData();
            var terrain  = go.AddComponent<Terrain>();
            var collider = go.AddComponent<TerrainCollider>();

            terrain.terrainData  = terrainData;
            collider.terrainData = terrainData;

            return terrain;
        }

        // =================================================================
        //  MATERIAL
        // =================================================================

        /// <summary>
        /// Assign a render-pipeline-appropriate material to the terrain.
        /// Prevents the pink-terrain issue common with URP.
        /// </summary>
        static void AssignTerrainMaterial(Terrain terrain)
        {
            // Keep the current material if it's valid and non-error
            if (terrain.materialTemplate != null &&
                terrain.materialTemplate.shader != null &&
                !terrain.materialTemplate.shader.name.Contains("Error"))
            {
                return;
            }

            var mat = SyrosMaterialHelper.GetTerrainMaterial();
            if (mat != null)
            {
                terrain.materialTemplate = mat;
                Debug.Log($"[SyrosTerrainGenerator] Assigned terrain material: {mat.shader.name}");
            }
            else
            {
                Debug.LogWarning("[SyrosTerrainGenerator] Could not find a terrain material. " +
                                 "Manually assign one via the Terrain component → Material.");
            }
        }
    }
}
