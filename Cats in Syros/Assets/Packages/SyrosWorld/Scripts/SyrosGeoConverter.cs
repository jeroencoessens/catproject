using System;
using UnityEngine;

namespace SyrosWorld
{
    /// <summary>
    /// Converts geographic coordinates (lon/lat) ↔ Unity world positions
    /// using optional density-based adaptive warping.
    ///
    /// <b>Adaptive (urban-preserving) mode:</b> Builds a CDF (Cumulative
    /// Distribution Function) from building-density histograms along each axis.
    /// Urban areas keep near-original proportions; rural / empty areas compress.
    ///
    /// <b>Uniform mode:</b> Simple proportional mapping — true island shape,
    /// just scaled to fit <c>targetWorldWidth × targetWorldLength</c>.
    ///
    /// <b>Workflow:</b>
    /// <list type="number">
    ///   <item>Build density histograms from building centroids.</item>
    ///   <item>Smooth histograms with a Gaussian kernel.</item>
    ///   <item>Apply a minimum floor so empty areas don't collapse to zero width.</item>
    ///   <item>Compute the CDF → the forward warp function.</item>
    ///   <item>Normalise output to [0, targetSize].</item>
    ///   <item>Build an inverse LUT (world → geo) for heightmap sampling.</item>
    /// </list>
    ///
    /// <b>Important:</b> The forward <see cref="GeoToWorld(double,double)"/>
    /// method returns Y = 0.  Terrain height is sampled separately via
    /// <see cref="Terrain.SampleHeight"/> or the overload that accepts a Terrain.
    /// </summary>
    public class SyrosGeoConverter
    {
        readonly SyrosWorldConfig _config;

        // Forward warp: CDF tables mapping normalised geo [0..1] → normalised world [0..1]
        float[] _cdfX;  // length = warpBinsX + 1
        float[] _cdfY;  // length = warpBinsY + 1

        // Inverse warp: lookup tables mapping normalised world [0..1] → normalised geo [0..1]
        float[] _invX;  // length = InverseLutSize
        float[] _invY;

        /// <summary>Resolution of the inverse lookup tables (higher = smoother inverse).</summary>
        const int InverseLutSize = 2048;

        /// <summary><c>true</c> after either <see cref="Initialize"/> or
        /// <see cref="InitializeUniform"/> has been called.</summary>
        public bool IsInitialized { get; private set; }

        public SyrosGeoConverter(SyrosWorldConfig config)
        {
            _config = config;
        }

        // =================================================================
        //  INITIALISATION — must be called before any conversion
        // =================================================================

        /// <summary>
        /// Build adaptive warp tables from parsed OSM building data.
        /// Dense areas (many buildings) receive more world-space, sparse areas compress.
        /// </summary>
        public void Initialize(SyrosMapData mapData)
        {
            int bx = _config.warpBinsX;
            int by = _config.warpBinsY;

            // 1. Build density histograms from building centroids
            float[] histX = new float[bx];
            float[] histY = new float[by];

            foreach (var bld in mapData.buildings)
            {
                var c = bld.Centroid;
                float nx = Mathf.Clamp01((float)((c.x - _config.minLon) / _config.LonSpan));
                float ny = Mathf.Clamp01((float)((c.y - _config.minLat) / _config.LatSpan));

                int ix = Mathf.Clamp(Mathf.FloorToInt(nx * bx), 0, bx - 1);
                int iy = Mathf.Clamp(Mathf.FloorToInt(ny * by), 0, by - 1);

                histX[ix] += 1f;
                histY[iy] += 1f;
            }

            // 2. Density exponent — amplify contrast between dense and sparse bins
            float exp = _config.densityExponent;
            for (int i = 0; i < bx; i++) histX[i] = Mathf.Pow(histX[i] + 0.001f, exp);
            for (int i = 0; i < by; i++) histY[i] = Mathf.Pow(histY[i] + 0.001f, exp);

            // 3. Gaussian smoothing to soften warp transitions
            if (_config.smoothingSigma > 0.01f)
            {
                histX = GaussianSmooth(histX, _config.smoothingSigma);
                histY = GaussianSmooth(histY, _config.smoothingSigma);
            }

            // 4. Minimum density floor — prevent zero-width dead zones
            float floorX = Max(histX) * _config.minDensityFloor;
            float floorY = Max(histY) * _config.minDensityFloor;
            for (int i = 0; i < bx; i++) histX[i] = Mathf.Max(histX[i], floorX);
            for (int i = 0; i < by; i++) histY[i] = Mathf.Max(histY[i], floorY);

            // 5. Build CDF (normalised to [0, 1])
            _cdfX = BuildCDF(histX);
            _cdfY = BuildCDF(histY);

            // 6. Build inverse LUT via binary search
            _invX = BuildInverseLUT(_cdfX, InverseLutSize);
            _invY = BuildInverseLUT(_cdfY, InverseLutSize);

            IsInitialized = true;

            Debug.Log($"[SyrosGeoConverter] Adaptive warp tables built: " +
                      $"{bx}×{by} bins, inverse LUT {InverseLutSize}");
        }

        /// <summary>
        /// Initialise with identity (no-warp) mapping.
        /// Useful for testing or for a true-proportion view of the island.
        /// </summary>
        public void InitializeUniform()
        {
            int bx = _config.warpBinsX;
            int by = _config.warpBinsY;

            _cdfX = new float[bx + 1];
            _cdfY = new float[by + 1];
            for (int i = 0; i <= bx; i++) _cdfX[i] = (float)i / bx;
            for (int i = 0; i <= by; i++) _cdfY[i] = (float)i / by;

            _invX = new float[InverseLutSize];
            _invY = new float[InverseLutSize];
            for (int i = 0; i < InverseLutSize; i++)
            {
                _invX[i] = (float)i / (InverseLutSize - 1);
                _invY[i] = (float)i / (InverseLutSize - 1);
            }

            IsInitialized = true;
        }

        // =================================================================
        //  FORWARD: Geo → World
        // =================================================================

        /// <summary>
        /// Convert geo (lon, lat) to a Unity world position.
        /// <b>Y is always 0</b> — set it from terrain height separately.
        /// </summary>
        public Vector3 GeoToWorld(double lon, double lat)
        {
            // Normalise to [0, 1] within the OSM data extent
            float nx = (float)((lon - _config.minLon) / _config.LonSpan);
            float ny = (float)((lat - _config.minLat) / _config.LatSpan);

            // Apply forward warp (CDF) and scale to world units
            float wx = SampleCDF(_cdfX, nx) * _config.targetWorldWidth;
            float wz = SampleCDF(_cdfY, ny) * _config.targetWorldLength;

            return new Vector3(wx, 0f, wz);
        }

        /// <summary>
        /// Convert geo (lon, lat) to a Unity world position with terrain height.
        /// </summary>
        public Vector3 GeoToWorld(double lon, double lat, Terrain terrain)
        {
            Vector3 pos = GeoToWorld(lon, lat);
            if (terrain != null)
                pos.y = terrain.SampleHeight(pos);
            return pos;
        }

        /// <summary>Overload accepting <see cref="Vector2d"/>.</summary>
        public Vector3 GeoToWorld(Vector2d geo) => GeoToWorld(geo.x, geo.y);

        /// <summary>Overload accepting <see cref="Vector2d"/> with terrain.</summary>
        public Vector3 GeoToWorld(Vector2d geo, Terrain terrain) =>
            GeoToWorld(geo.x, geo.y, terrain);

        // =================================================================
        //  INVERSE: World → Geo  (used for heightmap sampling)
        // =================================================================

        /// <summary>
        /// Convert normalised world position [0,1] back to normalised geo
        /// position [0,1].  Used by the terrain generator to re-sample the
        /// heightmap in warped space.
        /// </summary>
        public Vector2 WorldNormToGeoNorm(float worldNormX, float worldNormY)
        {
            float geoNormX = SampleInverse(_invX, worldNormX);
            float geoNormY = SampleInverse(_invY, worldNormY);
            return new Vector2(geoNormX, geoNormY);
        }

        /// <summary>
        /// Convert normalised world position to absolute geo coordinates.
        /// </summary>
        public Vector2d WorldNormToGeo(float worldNormX, float worldNormY)
        {
            var gn = WorldNormToGeoNorm(worldNormX, worldNormY);
            return new Vector2d(
                _config.minLon + gn.x * _config.LonSpan,
                _config.minLat + gn.y * _config.LatSpan);
        }

        // =================================================================
        //  HEIGHTMAP SAMPLING
        // =================================================================

        /// <summary>
        /// Sample the heightmap texture at a geo-normalised position,
        /// accounting for potentially different heightmap vs OSM bounds.
        /// Returns a value in [0, 1] (black = 0, white = 1).
        ///
        /// <b>Scale chain:</b>
        /// <c>heightmapPixel [0,1] → terrainData.SetHeights → Unity Y = pixel × terrainMaxHeight</c>.
        /// There are no additional multipliers — <c>terrainMaxHeight</c> is the
        /// single scale factor that converts normalised height to metres.
        /// </summary>
        public float SampleHeightmap(Texture2D heightmap, float geoNormX, float geoNormY)
        {
            // Map OSM geo-norm to heightmap UV (handles different bounds)
            double osmLon = _config.minLon + geoNormX * _config.LonSpan;
            double osmLat = _config.minLat + geoNormY * _config.LatSpan;

            float u = Mathf.Clamp01((float)((osmLon - _config.heightmapMinLon) /
                              (_config.heightmapMaxLon - _config.heightmapMinLon)));
            float v = Mathf.Clamp01((float)((osmLat - _config.heightmapMinLat) /
                              (_config.heightmapMaxLat - _config.heightmapMinLat)));

            // Bilinear sample the heightmap texture
            int w = heightmap.width;
            int h = heightmap.height;
            float px = u * (w - 1);
            float py = v * (h - 1);

            int x0 = Mathf.FloorToInt(px);
            int y0 = Mathf.FloorToInt(py);
            int x1 = Mathf.Min(x0 + 1, w - 1);
            int y1 = Mathf.Min(y0 + 1, h - 1);
            float fx = px - x0;
            float fy = py - y0;

            float h00 = heightmap.GetPixel(x0, y0).grayscale;
            float h10 = heightmap.GetPixel(x1, y0).grayscale;
            float h01 = heightmap.GetPixel(x0, y1).grayscale;
            float h11 = heightmap.GetPixel(x1, y1).grayscale;

            return Mathf.Lerp(
                Mathf.Lerp(h00, h10, fx),
                Mathf.Lerp(h01, h11, fx),
                fy);
        }

        // =================================================================
        //  INTERNAL — CDF construction & sampling
        // =================================================================

        /// <summary>
        /// Build a normalised CDF from a histogram.
        /// Returns an array of length <c>hist.Length + 1</c> spanning [0, 1].
        /// </summary>
        static float[] BuildCDF(float[] hist)
        {
            int n = hist.Length;
            float[] cdf = new float[n + 1];
            cdf[0] = 0f;
            for (int i = 0; i < n; i++)
                cdf[i + 1] = cdf[i] + hist[i];

            float total = cdf[n];
            if (total > 0f)
                for (int i = 0; i <= n; i++)
                    cdf[i] /= total;

            return cdf;
        }

        /// <summary>
        /// Sample the CDF at normalised position <paramref name="t"/> ∈ [0,1].
        /// Returns the warped position in [0,1].
        /// </summary>
        static float SampleCDF(float[] cdf, float t)
        {
            int bins = cdf.Length - 1;
            t = Mathf.Clamp01(t);
            float pos = t * bins;
            int idx = Mathf.Clamp(Mathf.FloorToInt(pos), 0, bins - 1);
            float frac = pos - idx;
            return Mathf.Lerp(cdf[idx], cdf[idx + 1], frac);
        }

        // =================================================================
        //  INTERNAL — Inverse LUT construction & sampling
        // =================================================================

        /// <summary>
        /// Build an inverse lookup table from the CDF via binary search.
        /// Maps warped [0,1] back to original [0,1].
        /// </summary>
        static float[] BuildInverseLUT(float[] cdf, int lutSize)
        {
            float[] inv = new float[lutSize];
            int bins = cdf.Length - 1;

            for (int i = 0; i < lutSize; i++)
            {
                float target = (float)i / (lutSize - 1);

                // Binary search: find the bin whose CDF bracket contains target
                int lo = 0, hi = bins;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (cdf[mid + 1] < target) lo = mid + 1;
                    else hi = mid;
                }

                float binStart = cdf[lo];
                float binEnd   = cdf[Mathf.Min(lo + 1, bins)];
                float frac     = (binEnd - binStart > 1e-8f)
                    ? Mathf.Clamp01((target - binStart) / (binEnd - binStart))
                    : 0f;

                inv[i] = (lo + frac) / bins;
            }

            return inv;
        }

        /// <summary>
        /// Sample the inverse LUT at normalised position <paramref name="t"/> ∈ [0,1].
        /// </summary>
        static float SampleInverse(float[] inv, float t)
        {
            t = Mathf.Clamp01(t);
            float pos = t * (inv.Length - 1);
            int idx = Mathf.Clamp(Mathf.FloorToInt(pos), 0, inv.Length - 2);
            float frac = pos - idx;
            return Mathf.Lerp(inv[idx], inv[idx + 1], frac);
        }

        // =================================================================
        //  INTERNAL — Utilities
        // =================================================================

        /// <summary>1-D Gaussian smoothing with clamped edges.</summary>
        static float[] GaussianSmooth(float[] data, float sigma)
        {
            int n = data.Length;
            float[] result = new float[n];
            int radius = Mathf.CeilToInt(sigma * 3f);

            for (int i = 0; i < n; i++)
            {
                float sum = 0f, wsum = 0f;
                for (int j = -radius; j <= radius; j++)
                {
                    int idx = Mathf.Clamp(i + j, 0, n - 1);
                    float w = Mathf.Exp(-(j * j) / (2f * sigma * sigma));
                    sum  += data[idx] * w;
                    wsum += w;
                }
                result[i] = sum / wsum;
            }

            return result;
        }

        /// <summary>Maximum value in an array.</summary>
        static float Max(float[] arr)
        {
            float m = float.MinValue;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] > m) m = arr[i];
            return m;
        }
    }
}
