// =============================================================================
// ContactGrassSpawner.cs
//
// GPU-instanced grass spawner for building-terrain contact edges.
// Works with TerrainContactMaskGenerator (provides the contact mask) and
// ContactGrassScatter.compute (GPU scatter + frustum cull kernels).
//
// ─── MODES ───────────────────────────────────────────────────────────────
//   MANUAL (useStreaming=false):
//     Scatters the entire terrain in one compute dispatch.
//     Good for small-to-medium terrains. Triggered by button or scatterOnPlay.
//
//   STREAMING (useStreaming=true):
//     Divides terrain into a chunk grid. Only chunks near the camera are
//     scattered and kept in GPU memory. Chunks are loaded/unloaded each
//     frame with a per-frame budget to avoid spikes.
//
// ─── PIPELINE ────────────────────────────────────────────────────────────
//   1. SCATTER  (once / per chunk):
//        Compute shader reads contact mask + heightmap → _grassBuffer.
//   2. CULL     (every frame, per camera):
//        GPU frustum + distance cull → _visibleBuffer.
//   3. DRAW     (every frame, per camera):
//        DrawMeshInstancedIndirect with a 3-quad cross mesh.
//
// ─── CACHING ─────────────────────────────────────────────────────────────
//   Manual mode:   saves _grassBuffer to disk (Library/GrassCache/).
//   Streaming mode: keeps unloaded chunk data in a CPU-side dictionary.
//   Both are invalidated automatically when scatter parameters change.
//
// ─── DEBUG ───────────────────────────────────────────────────────────────
//   debugSkipCulling     — renders ALL instances (verify scatter correctness)
//   debugShowPositions   — gizmo spheres at sample positions
//   debugLogVisibleCount — per-frame visible count in console
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class ContactGrassSpawner : MonoBehaviour
{
    // ── References ─────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("The contact mask generator that provides the mask texture.")]
    public TerrainContactMaskGenerator maskGenerator;

    [Tooltip("The terrain to spawn grass on. Auto-detected if null.")]
    public Terrain terrain;

    [Tooltip("Compute shader (ContactGrassScatter.compute).")]
    public ComputeShader scatterCompute;

    [Header("Grass Appearance")]
    [Tooltip("Grass blade texture (alpha cutout). Works as white quads if null.")]
    public Texture2D grassTexture;

    [Tooltip("Base colour at the bottom of grass blades.")]
    public Color baseColor = new Color(0.35f, 0.55f, 0.2f, 1f);

    [Tooltip("Tip colour at the top of grass blades.")]
    public Color tipColor = new Color(0.6f, 0.8f, 0.3f, 1f);

    [Tooltip("Alpha cutoff threshold.")]
    [Range(0f, 1f)]
    public float alphaCutoff = 0.4f;

    [Tooltip("Per-instance colour variation amount.")]
    [Range(0f, 0.5f)]
    public float colorVariation = 0.15f;

    [Header("Grass Size")]
    [Tooltip("Master scale multiplier applied to all grass. Works in real-time without re-scattering.")]
    [Range(0.01f, 5f)]
    public float uniformScale = 1f;
    [Tooltip("Base width of each grass tuft (metres). Keep small for dense look.")]
    public float baseWidth = 0.12f;

    [Tooltip("Base height of each grass blade (metres).")]
    public float baseHeight = 0.25f;

    [Tooltip("Random variation in width (±fraction). 0.3 = ±30%.")]
    [Range(0f, 0.8f)]
    public float widthVariation = 0.3f;

    [Tooltip("Random variation in height (±fraction).")]
    [Range(0f, 0.8f)]
    public float heightVariation = 0.5f;

    [Header("Scatter Settings")]
    [Tooltip("Grid density for scatter sampling along X axis.\nY is scaled by terrain aspect ratio.\n" +
             "Higher = more potential spawn points. 1024+ recommended for dense grass.")]
    [Range(64, 8192)]
    public int scatterGridX = 1024;

    [Tooltip("Spawn probability per grid slot (before edge modulation).")]
    [Range(0.01f, 2f)]
    public float density = 0.6f;

    [Tooltip("Maximum instances the buffer can hold.")]
    public int maxInstances = 500000;

    [Tooltip("World-space Y offset for grass placement.")]
    public float heightOffset = 0f;

    [Header("Streaming")]
    [Tooltip("Enable chunk-based streaming.\n" +
             "Only terrain near the camera is scattered and rendered.\n" +
             "Critical for large maps.")]
    public bool useStreaming = true;

    [Tooltip("World-space size of each streaming chunk (metres).\n" +
             "Smaller = finer granularity but more overhead. 30\u201380m is typical.")]
    [Range(10f, 200f)]
    public float chunkSize = 50f;

    [Tooltip("Distance from camera within which chunks are loaded.\n" +
             "Should be >= maxRenderDistance. Chunks outside this are unloaded.")]
    public float streamingDistance = 250f;

    [Tooltip("Maximum chunks that can be active simultaneously.\n" +
             "Memory = maxActiveChunks \u00d7 maxInstancesPerChunk \u00d7 32 bytes \u00d7 2.")]
    [Range(16, 1024)]
    public int maxActiveChunks = 256;

    [Tooltip("Maximum grass instances per chunk slot.\n" +
             "If a chunk has more potential instances, excess are dropped.")]
    [Range(256, 16384)]
    public int maxInstancesPerChunk = 4096;

    [Tooltip("Max chunks to scatter per frame during streaming.\n" +
             "Higher = faster loading but larger frame spikes.")]
    [Range(1, 16)]
    public int chunkLoadBudget = 4;

    [Header("Clustering")]
    [Tooltip("World-space noise frequency. Smaller = larger clusters, bigger = tighter clusters.\n" +
             "Try 0.05\u20130.3 for building-scale clusters.")]
    [Range(0.01f, 1f)]
    public float clusterScale = 0.1f;

    [Tooltip("Blend between uniform (0) and fully clustered (1).\n" +
             "0.5 = moderate clustering. 1.0 = strong clumps with gaps.")]
    [Range(0f, 1f)]
    public float clusterStrength = 0.5f;

    [Tooltip("Density multiplier inside cluster peaks.\n" +
             "1.0 = normal. 2.0 = double density in cluster centres.\n" +
             "Higher values pack grass tighter in clumps.")]
    [Range(0.5f, 5f)]
    public float clusterDensityBoost = 1.5f;

    [Header("Edge Detection")]
    [Tooltip("Minimum mask value for spawning.\n" +
             "The blurred mask is ~1.0 inside buildings, ~0 far away.\n" +
             "0.05 = include faint outer edges.")]
    [Range(0f, 0.9f)]
    public float edgeMin = 0.05f;

    [Tooltip("Maximum mask value for spawning.\n" +
             "0.45 = exclude bright interior under buildings.\n" +
             "Increase to widen the spawning ring inward.")]
    [Range(0.1f, 1f)]
    public float edgeMax = 0.45f;

    [Tooltip("Gradient boost for edge detection.\n" +
             "0 = use band only. Higher values favor steep mask transitions (true building edges).\n" +
             "2-5 is a good range.")]
    [Range(0f, 10f)]
    public float gradientBoost = 3f;

    [Header("Wind")]
    public float windSpeed = 1.5f;
    public float windStrength = 0.15f;

    [Header("Culling & Distance")]
    [Tooltip("Maximum render distance (metres). Grass beyond this is culled entirely.")]
    public float maxRenderDistance = 200f;

    [Tooltip("Fraction of maxRenderDistance where fade-out begins.\n0.7 = fade starts at 70% of max distance.")]
    [Range(0.3f, 0.95f)]
    public float fadeStartFraction = 0.7f;

    [Tooltip("Instances with fade below this are culled entirely (not drawn).\n" +
             "Prevents nearly-invisible grass from consuming draw calls.\n" +
             "0.02 = cull when 98% faded. 0.1 = more aggressive culling.")]
    [Range(0.01f, 0.3f)]
    public float fadeThreshold = 0.05f;

    [Header("Rendering")]
    [Tooltip("Shadow casting mode for the grass.")]
    public ShadowCastingMode shadowMode = ShadowCastingMode.Off;

    [Tooltip("Layer the grass renders on.")]
    public int renderLayer = 0;

    [Header("Debug")]
    [Tooltip("Skip frustum+distance culling — render ALL scattered instances.\nUseful to verify scatter positions are correct.")]
    public bool debugSkipCulling = false;

    [Tooltip("Draw green gizmo spheres at a sample of scatter positions.\nRequires GPU readback — ONLY for debugging.")]
    public bool debugShowPositions = false;

    [Tooltip("Max gizmo spheres to draw. Larger = more GPU readback cost.")]
    [Range(10, 1000)]
    public int debugMaxGizmos = 100;

    [Tooltip("Log visible instance count to console each frame. Very spammy.")]
    public bool debugLogVisibleCount = false;

    [Header("Update")]
    [Tooltip("Automatically scatter grass when entering Play mode or when the mask becomes available.\n" +
             "In streaming mode the system auto-loads chunks regardless of this flag.")]
    public bool scatterOnPlay = true;

    [Header("Caching")]
    [Tooltip("Cache scatter results to disk (Library/GrassCache/).\n" +
             "Subsequent Play sessions load instantly without re-running the compute shader.\n" +
             "Cache is automatically invalidated when scatter parameters change.")]
    public bool enableCache = true;

    // ── Internal ───────────────────────────────────────────────────────
    ComputeBuffer _grassBuffer;
    ComputeBuffer _counterBuffer;
    ComputeBuffer _visibleBuffer;
    ComputeBuffer _cullCounterBuffer;
    ComputeBuffer _argsBuffer;

    Material      _grassMat;
    Mesh          _quadMesh;
    Bounds        _worldBounds;
    bool          _isScattered;  // true when at least some data is in the buffer
    int           _scatteredCount; // total instances across all active chunks
    int           _lastVisibleCount;

    int _kernelClear;
    int _kernelScatter;
    int _kernelClearCull;
    int _kernelFrustumCull;

    int _lastMaxBuffer; // tracks buffer size for rebuild detection

    readonly Plane[]   _unityPlanes = new Plane[6];
    readonly Vector4[] _planeVec4   = new Vector4[6];
    readonly uint[]    _argsReset   = new uint[5] { 0, 0, 0, 0, 0 };

    // ── Debug ──────────────────────────────────────────────────────────
    Vector3[] _debugPositions;  // cached readback for gizmo drawing

    // ── Chunk Streaming State ──────────────────────────────────────────
    // Each active chunk occupies a contiguous slot in _grassBuffer.
    // Slot index * maxInstancesPerChunk = buffer offset for that chunk.
    struct ChunkInfo
    {
        public int   slotIndex;      // pool slot 0..maxActiveChunks-1
        public int   instanceCount;  // instances in this chunk
    }

    Dictionary<Vector2Int, ChunkInfo> _activeChunks;
    Stack<int>   _freeSlots;
    int          _chunksX, _chunksZ; // total chunk grid dimensions
    Vector3      _terrainOrigin;
    Vector3      _terrainSize;

    // ── CPU Chunk Cache (streaming) ────────────────────────────────────
    // When a streaming chunk is unloaded from GPU, its instance data is
    // kept here so revisiting the same area skips the compute dispatch.
    Dictionary<Vector2Int, (int count, float[] data)> _chunkCpuCache;

    // ── Public Read-Only Properties ────────────────────────────────────
    public int TotalScatteredCount => _scatteredCount;
    public int LastVisibleCount => _lastVisibleCount;
    public int ActiveChunkCount => _activeChunks != null ? _activeChunks.Count : 0;

    // ================================================================
    //  LIFECYCLE
    // ================================================================

    void OnEnable()
    {
        if (maskGenerator == null)
            maskGenerator = FindFirstObjectByType<TerrainContactMaskGenerator>();
        if (terrain == null)
            terrain = FindFirstObjectByType<Terrain>();

        _activeChunks = new Dictionary<Vector2Int, ChunkInfo>();
        _freeSlots = new Stack<int>();
        _chunkCpuCache = new Dictionary<Vector2Int, (int, float[])>();

        BuildQuadMesh();
        EnsureBuffers();
        CacheKernels();

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

        _activeChunks?.Clear();
        _freeSlots?.Clear();
        _chunkCpuCache?.Clear();
        _scatteredCount = 0;
        _isScattered = false;

        ReleaseBuffers();
        if (_grassMat != null)
        {
            if (Application.isPlaying) Destroy(_grassMat);
            else DestroyImmediate(_grassMat);
            _grassMat = null;
        }
    }

    void Update()
    {
        if (terrain == null || terrain.terrainData == null) return;
        if (maskGenerator == null || maskGenerator.MaskTexture == null) return;
        if (scatterCompute == null) return;

        // Cache terrain info
        _terrainOrigin = terrain.transform.position;
        _terrainSize = terrain.terrainData.size;
        _chunksX = Mathf.CeilToInt(_terrainSize.x / chunkSize);
        _chunksZ = Mathf.CeilToInt(_terrainSize.z / chunkSize);

        if (useStreaming)
        {
            UpdateStreaming();
        }
        else if (!_isScattered && scatterOnPlay)
        {
            // Try loading from disk cache first
            if (enableCache && TryLoadCacheFromDisk())
            {
                // Loaded from cache — no compute needed
            }
            else
            {
                ScatterGrass();
            }
        }
    }

    // ================================================================
    //  URP CAMERA CALLBACK — this is where cull + draw happens
    // ================================================================

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (!isActiveAndEnabled) return;
        if (!_isScattered && (!useStreaming || _activeChunks == null || _activeChunks.Count == 0))
            return;

        // Skip preview cameras, reflection probes, etc.
        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection)
            return;

        EnsureMaterial();
        if (_grassMat == null || scatterCompute == null)
            return;

        if (debugSkipCulling)
            DrawAllUnchecked(cam);
        else
            CullAndDraw(cam);
    }

    // ================================================================
    //  STREAMING — load/unload chunks as camera moves
    // ================================================================

    void UpdateStreaming()
    {
        EnsureBuffers();
        EnsureMaterial();
        CacheKernels();

        // Find camera (prefer scene camera in editor, main camera at runtime)
        Camera cam = null;
        #if UNITY_EDITOR
        if (!Application.isPlaying && SceneView.lastActiveSceneView != null)
            cam = SceneView.lastActiveSceneView.camera;
        #endif
        if (cam == null) cam = Camera.main;
        if (cam == null) return;

        Vector3 camPos = cam.transform.position;

        // Current camera chunk
        int camCX = Mathf.FloorToInt((camPos.x - _terrainOrigin.x) / chunkSize);
        int camCZ = Mathf.FloorToInt((camPos.z - _terrainOrigin.z) / chunkSize);

        // Build set of desired chunks within streaming distance
        int radius = Mathf.CeilToInt(streamingDistance / chunkSize);
        var desiredChunks = new HashSet<Vector2Int>();
        for (int cx = camCX - radius; cx <= camCX + radius; cx++)
        {
            for (int cz = camCZ - radius; cz <= camCZ + radius; cz++)
            {
                if (cx < 0 || cx >= _chunksX || cz < 0 || cz >= _chunksZ) continue;

                // Check actual distance from camera to chunk centre
                float chunkCenterX = _terrainOrigin.x + (cx + 0.5f) * chunkSize;
                float chunkCenterZ = _terrainOrigin.z + (cz + 0.5f) * chunkSize;
                float dx = camPos.x - chunkCenterX;
                float dz = camPos.z - chunkCenterZ;
                float dist2D = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist2D <= streamingDistance + chunkSize * 0.707f)
                    desiredChunks.Add(new Vector2Int(cx, cz));
            }
        }

        // ── Unload chunks outside range (save to CPU cache first) ──
        var toRemove = new List<Vector2Int>();
        foreach (var kvp in _activeChunks)
        {
            if (!desiredChunks.Contains(kvp.Key))
                toRemove.Add(kvp.Key);
        }
        foreach (var coord in toRemove)
        {
            var chunk = _activeChunks[coord];
            // Save to CPU cache before freeing the GPU slot
            if (enableCache && chunk.instanceCount > 0)
                SaveChunkToCpuCache(coord, chunk.slotIndex, chunk.instanceCount);

            _freeSlots.Push(chunk.slotIndex);
            _scatteredCount -= chunk.instanceCount;
            _activeChunks.Remove(coord);
        }

        // ── Load new chunks (budgeted) — check CPU cache first ──
        int loaded = 0;
        foreach (var coord in desiredChunks)
        {
            if (_activeChunks.ContainsKey(coord)) continue;
            if (_freeSlots.Count == 0 && _activeChunks.Count >= maxActiveChunks) break;
            if (loaded >= chunkLoadBudget) break;

            int slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _activeChunks.Count;
            int count;

            // Try CPU cache first (avoids compute scatter for revisited areas)
            if (enableCache && TryLoadChunkFromCpuCache(coord, slot, out count))
            {
                // Loaded from cache — no compute needed
            }
            else
            {
                count = ScatterSingleChunk(coord, slot);
            }

            _activeChunks[coord] = new ChunkInfo { slotIndex = slot, instanceCount = count };
            _scatteredCount += count;
            loaded++;
        }

        _isScattered = _activeChunks.Count > 0;

        // Update world bounds
        _worldBounds = new Bounds(
            _terrainOrigin + _terrainSize * 0.5f,
            _terrainSize + Vector3.up * baseHeight * 4f
        );
    }

    /// <summary>
    /// Set up compute params shared across all chunk scatters (textures, terrain info, etc.)
    /// </summary>
    void PrepareGlobalScatterParams()
    {
        TerrainData td = terrain.terrainData;

        scatterCompute.SetBuffer(_kernelScatter, "_GrassBuffer", _grassBuffer);
        scatterCompute.SetBuffer(_kernelScatter, "_CounterBuffer", _counterBuffer);
        scatterCompute.SetTexture(_kernelScatter, "_ContactMask", maskGenerator.MaskTexture);
        scatterCompute.SetTexture(_kernelScatter, "_HeightMap", td.heightmapTexture);

        scatterCompute.SetVector("_TerrainOrigin", new Vector4(_terrainOrigin.x, _terrainOrigin.y, _terrainOrigin.z, 0));
        scatterCompute.SetVector("_TerrainSize", new Vector4(_terrainSize.x, _terrainSize.y, _terrainSize.z, 0));
        scatterCompute.SetFloat("_Density", density);
        scatterCompute.SetFloat("_BaseWidth", baseWidth);
        scatterCompute.SetFloat("_BaseHeight", baseHeight);
        scatterCompute.SetFloat("_WidthVariation", widthVariation);
        scatterCompute.SetFloat("_HeightVariation", heightVariation);
        scatterCompute.SetFloat("_HeightOffset", heightOffset);
        scatterCompute.SetFloat("_EdgeMin", edgeMin);
        scatterCompute.SetFloat("_EdgeMax", edgeMax);
        scatterCompute.SetFloat("_GradientBoost", gradientBoost);
        scatterCompute.SetInt("_MaskWidth", maskGenerator.MaskTexture.width);
        scatterCompute.SetInt("_MaskHeight", maskGenerator.MaskTexture.height);
        scatterCompute.SetFloat("_ClusterScale", clusterScale);
        scatterCompute.SetFloat("_ClusterStrength", clusterStrength);
        scatterCompute.SetFloat("_ClusterDensityBoost", clusterDensityBoost);
    }

    /// <summary>
    /// Scatter a single chunk into a specific buffer slot. Returns instance count.
    /// </summary>
    int ScatterSingleChunk(Vector2Int coords, int slot)
    {
        // Set global params (idempotent — compute params persist)
        PrepareGlobalScatterParams();

        // Clear counter
        scatterCompute.SetBuffer(_kernelClear, "_CounterBuffer", _counterBuffer);
        scatterCompute.Dispatch(_kernelClear, 1, 1, 1);

        // Per-chunk UV range
        float uvMinX = (coords.x * chunkSize) / _terrainSize.x;
        float uvMinZ = (coords.y * chunkSize) / _terrainSize.z;
        float uvSizeX = chunkSize / _terrainSize.x;
        float uvSizeZ = chunkSize / _terrainSize.z;
        // Clamp to [0, 1] for edge chunks
        uvSizeX = Mathf.Min(uvSizeX, 1f - uvMinX);
        uvSizeZ = Mathf.Min(uvSizeZ, 1f - uvMinZ);

        scatterCompute.SetVector("_ChunkUVMin", new Vector4(uvMinX, uvMinZ, 0, 0));
        scatterCompute.SetVector("_ChunkUVSize", new Vector4(uvSizeX, uvSizeZ, 0, 0));
        scatterCompute.SetInt("_ChunkBufferOffset", slot * maxInstancesPerChunk);
        scatterCompute.SetInt("_ChunkMaxInstances", maxInstancesPerChunk);

        // Per-chunk grid resolution: based on actual chunk world size, not chunkSize
        float cellSize = _terrainSize.x / scatterGridX;
        float actualWorldX = uvSizeX * _terrainSize.x;
        float actualWorldZ = uvSizeZ * _terrainSize.z;
        int chunkGridX = Mathf.Max(1, Mathf.CeilToInt(actualWorldX / cellSize));
        int chunkGridZ = Mathf.Max(1, Mathf.CeilToInt(actualWorldZ / cellSize));
        scatterCompute.SetInt("_GridWidth", chunkGridX);
        scatterCompute.SetInt("_GridHeight", chunkGridZ);

        // Deterministic seed per chunk (same chunk always produces same layout)
        scatterCompute.SetFloat("_Seed", coords.x * 73.1f + coords.y * 137.9f);

        int groupsX = Mathf.CeilToInt(chunkGridX / 8f);
        int groupsZ = Mathf.CeilToInt(chunkGridZ / 8f);
        scatterCompute.Dispatch(_kernelScatter, groupsX, groupsZ, 1);

        // Read back count
        uint[] count = new uint[1];
        _counterBuffer.GetData(count);
        return (int)System.Math.Min(count[0], (uint)maxInstancesPerChunk);
    }

    /// <summary>
    /// Force clear and reload all chunks. Called by the editor button.
    /// </summary>
    public void ResetStreaming()
    {
        if (_activeChunks != null)
        {
            foreach (var kvp in _activeChunks)
                _freeSlots.Push(kvp.Value.slotIndex);
            _activeChunks.Clear();
        }
        _chunkCpuCache?.Clear();
        _scatteredCount = 0;
        _isScattered = false;
        _debugPositions = null;
    }

    // ================================================================
    //  PUBLIC API — manual scatter-all (non-streaming mode)
    // ================================================================

    public void ScatterGrass()
    {
        if (scatterCompute == null)
        {
            Debug.LogWarning("[GrassSpawner] No compute shader assigned. " +
                "Drag ContactGrassScatter.compute into the Scatter Compute slot.");
            return;
        }
        if (maskGenerator == null || maskGenerator.MaskTexture == null)
        {
            Debug.LogWarning("[GrassSpawner] No contact mask available. " +
                "Bake the mask first in TerrainContactMaskGenerator.");
            return;
        }
        if (terrain == null || terrain.terrainData == null)
        {
            Debug.LogWarning("[GrassSpawner] No terrain assigned.");
            return;
        }

        EnsureBuffers();
        EnsureMaterial();
        CacheKernels();

        // Refresh terrain state (may be called from editor outside the Update loop)
        _terrainOrigin = terrain.transform.position;
        _terrainSize   = terrain.terrainData.size;

        int gridX = scatterGridX;
        int gridY = Mathf.CeilToInt(scatterGridX * (_terrainSize.z / _terrainSize.x));

        // ── Clear scatter counter ──
        scatterCompute.SetBuffer(_kernelClear, "_CounterBuffer", _counterBuffer);
        scatterCompute.Dispatch(_kernelClear, 1, 1, 1);

        // ── Set scatter params (shared params + full-terrain overrides) ──
        PrepareGlobalScatterParams();
        scatterCompute.SetInt("_GridWidth", gridX);
        scatterCompute.SetInt("_GridHeight", gridY);
        scatterCompute.SetFloat("_Seed", Random.Range(0f, 10000f));

        // Full-terrain mode: UV covers entire terrain, no buffer offset
        scatterCompute.SetVector("_ChunkUVMin", new Vector4(0f, 0f, 0, 0));
        scatterCompute.SetVector("_ChunkUVSize", new Vector4(1f, 1f, 0, 0));
        scatterCompute.SetInt("_ChunkBufferOffset", 0);
        scatterCompute.SetInt("_ChunkMaxInstances", maxInstances);

        int groupsX = Mathf.CeilToInt(gridX / 8f);
        int groupsY = Mathf.CeilToInt(gridY / 8f);
        scatterCompute.Dispatch(_kernelScatter, groupsX, groupsY, 1);

        // Read back total count
        uint[] count = new uint[1];
        _counterBuffer.GetData(count);
        _scatteredCount = (int)System.Math.Min(count[0], (uint)maxInstances);

        // World bounds for Unity's coarse frustum check
        _worldBounds = new Bounds(
            _terrainOrigin + _terrainSize * 0.5f,
            _terrainSize + Vector3.up * baseHeight * 2f
        );

        _isScattered = true;
        _debugPositions = null; // invalidate debug cache

        Debug.Log($"[GrassSpawner] Scattered {_scatteredCount:N0} grass instances " +
                  $"(grid {gridX}×{gridY}, density {density:F2}). " +
                  $"Edge band: [{edgeMin:F2} – {edgeMax:F2}], gradient boost: {gradientBoost:F1}");

        // Save to disk cache for instant reload on next Play
        if (enableCache && _scatteredCount > 0)
            SaveCacheToDisk();

        if (_scatteredCount == 0)
        {
            Debug.LogWarning("[GrassSpawner] Zero instances scattered! Possible causes:\n" +
                "  - Contact mask is all black (no objects detected)\n" +
                "  - edgeMin/edgeMax band too narrow or missing the gradient range\n" +
                "  - density too low\n" +
                "  - Object layers not included in mask generator\n" +
                "  Check the mask preview in TerrainContactMaskGenerator inspector.");
        }
    }

    // ================================================================
    //  CULL + DRAW  (called per camera from URP callback)
    // ================================================================

    void CullAndDraw(Camera cam, float overrideMaxDist = -1f, float overrideFadeThreshold = -1f)
    {
        float effectiveMaxDist = overrideMaxDist > 0f ? overrideMaxDist : maxRenderDistance;
        float effectiveFadeThr = overrideFadeThreshold >= 0f ? overrideFadeThreshold : fadeThreshold;

        // ── 1. Clear cull counter ──
        scatterCompute.SetBuffer(_kernelClearCull, "_CullCounterBuffer", _cullCounterBuffer);
        scatterCompute.Dispatch(_kernelClearCull, 1, 1, 1);

        // ── 2. Extract frustum planes ──
        GeometryUtility.CalculateFrustumPlanes(cam, _unityPlanes);
        for (int i = 0; i < 6; i++)
        {
            Vector3 n = _unityPlanes[i].normal;
            _planeVec4[i] = new Vector4(n.x, n.y, n.z, _unityPlanes[i].distance);
        }

        // ── 3. Set shared cull params ──
        scatterCompute.SetBuffer(_kernelFrustumCull, "_GrassBufferRead", _grassBuffer);
        scatterCompute.SetBuffer(_kernelFrustumCull, "_VisibleBuffer", _visibleBuffer);
        scatterCompute.SetBuffer(_kernelFrustumCull, "_CullCounterBuffer", _cullCounterBuffer);
        scatterCompute.SetVectorArray("_FrustumPlanes", _planeVec4);
        scatterCompute.SetVector("_CameraPos", cam.transform.position);
        scatterCompute.SetFloat("_MaxDist", effectiveMaxDist);
        scatterCompute.SetFloat("_FadeStart", fadeStartFraction);
        scatterCompute.SetFloat("_FadeThreshold", effectiveFadeThr);

        int totalVisibleCapacity = useStreaming
            ? maxActiveChunks * maxInstancesPerChunk
            : maxInstances;
        scatterCompute.SetInt("_MaxInstances", totalVisibleCapacity);

        // ── 4. Dispatch cull — per-chunk in streaming, single in legacy ──
        if (useStreaming && _activeChunks != null)
        {
            foreach (var kvp in _activeChunks)
            {
                var chunk = kvp.Value;
                if (chunk.instanceCount <= 0) continue;
                scatterCompute.SetInt("_CullOffset", chunk.slotIndex * maxInstancesPerChunk);
                scatterCompute.SetInt("_TotalInstances", chunk.instanceCount);
                int groups = Mathf.CeilToInt(chunk.instanceCount / 64f);
                scatterCompute.Dispatch(_kernelFrustumCull, groups, 1, 1);
            }
        }
        else
        {
            scatterCompute.SetInt("_CullOffset", 0);
            scatterCompute.SetInt("_TotalInstances", _scatteredCount);
            int cullGroups = Mathf.CeilToInt(_scatteredCount / 64f);
            scatterCompute.Dispatch(_kernelFrustumCull, cullGroups, 1, 1);
        }

        // ── 5. Read back visible count ──
        uint[] cullCount = new uint[1];
        _cullCounterBuffer.GetData(cullCount);
        uint visibleCount = System.Math.Min(cullCount[0], (uint)totalVisibleCapacity);
        _lastVisibleCount = (int)visibleCount;

        if (debugLogVisibleCount)
            Debug.Log($"[GrassSpawner] Visible: {visibleCount} / {_scatteredCount}" +
                      (useStreaming ? $" ({_activeChunks.Count} chunks)" : "") +
                      $" ({cam.name})");

        if (visibleCount == 0)
            return;

        // ── 6. Set indirect args ──
        uint[] argsData = new uint[5];
        argsData[0] = (uint)_quadMesh.GetIndexCount(0);
        argsData[1] = visibleCount;
        argsData[2] = (uint)_quadMesh.GetIndexStart(0);
        argsData[3] = (uint)_quadMesh.GetBaseVertex(0);
        argsData[4] = 0;
        _argsBuffer.SetData(argsData);

        // ── 7. Draw ──
        SetMaterialProperties();
        _grassMat.SetBuffer("_VisibleBuffer", _visibleBuffer);

        Graphics.DrawMeshInstancedIndirect(
            _quadMesh, 0, _grassMat, _worldBounds, _argsBuffer,
            0, null, shadowMode, true, renderLayer
        );
    }

    /// <summary>
    /// Debug mode: render ALL scattered instances by disabling distance/fade culling.
    /// </summary>
    void DrawAllUnchecked(Camera cam)
    {
        CullAndDraw(cam, overrideMaxDist: 999999f, overrideFadeThreshold: 0f);

        if (debugLogVisibleCount)
            Debug.Log($"[GrassSpawner] Debug: skip-culling active ({cam.name})");
    }

    void SetMaterialProperties()
    {
        _grassMat.SetColor("_BaseColor", baseColor);
        _grassMat.SetColor("_TipColor", tipColor);
        _grassMat.SetFloat("_Cutoff", alphaCutoff);
        _grassMat.SetFloat("_WindSpeed", windSpeed);
        _grassMat.SetFloat("_WindStrength", windStrength);
        _grassMat.SetFloat("_ColorVariation", colorVariation);
        _grassMat.SetFloat("_UniformScale", uniformScale);

        if (grassTexture != null)
            _grassMat.SetTexture("_MainTex", grassTexture);
    }

    // ================================================================
    //  KERNEL CACHING
    // ================================================================

    void CacheKernels()
    {
        if (scatterCompute == null) return;
        _kernelClear       = scatterCompute.FindKernel("Clear");
        _kernelScatter     = scatterCompute.FindKernel("Scatter");
        _kernelClearCull   = scatterCompute.FindKernel("ClearCull");
        _kernelFrustumCull = scatterCompute.FindKernel("FrustumCull");
    }

    // ================================================================
    //  BUFFER MANAGEMENT
    // ================================================================

    void EnsureBuffers()
    {
        // Buffer capacity depends on mode
        int requiredCapacity = useStreaming
            ? maxActiveChunks * maxInstancesPerChunk
            : maxInstances;

        bool needsRebuild = _grassBuffer == null || _lastMaxBuffer != requiredCapacity;

        if (!needsRebuild) return;

        ReleaseBuffers();

        int stride = 32; // GrassInstance: 8 floats × 4 bytes

        _grassBuffer       = new ComputeBuffer(requiredCapacity, stride);
        _counterBuffer     = new ComputeBuffer(1, sizeof(uint));
        _visibleBuffer     = new ComputeBuffer(requiredCapacity, stride);
        _cullCounterBuffer = new ComputeBuffer(1, sizeof(uint));
        _argsBuffer        = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);

        _argsBuffer.SetData(_argsReset);
        _counterBuffer.SetData(new uint[] { 0 });
        _cullCounterBuffer.SetData(new uint[] { 0 });

        _lastMaxBuffer = requiredCapacity;
        _isScattered = false;

        // Rebuild free slot pool for streaming
        if (useStreaming && _freeSlots != null)
        {
            _freeSlots.Clear();
            _activeChunks?.Clear();
            _scatteredCount = 0;
            for (int i = maxActiveChunks - 1; i >= 0; i--)
                _freeSlots.Push(i);
        }

        float mbCost = (requiredCapacity * stride * 2f) / (1024f * 1024f);
        Debug.Log($"[GrassSpawner] Allocated buffers: {requiredCapacity:N0} capacity ({mbCost:F1} MB)" +
                  (useStreaming ? $" [{maxActiveChunks} chunks × {maxInstancesPerChunk}/chunk]" : ""));
    }

    void ReleaseBuffers()
    {
        _grassBuffer?.Release();       _grassBuffer = null;
        _counterBuffer?.Release();     _counterBuffer = null;
        _visibleBuffer?.Release();     _visibleBuffer = null;
        _cullCounterBuffer?.Release(); _cullCounterBuffer = null;
        _argsBuffer?.Release();        _argsBuffer = null;
        _isScattered = false;
        _scatteredCount = 0;
    }

    // ================================================================
    //  CACHING — persist scatter results to avoid re-computing
    // ================================================================

    /// <summary>
    /// Deterministic hash of all scatter-relevant parameters.
    /// If any of these change, cached data is invalidated automatically.
    /// </summary>
    int ComputeParamHash()
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + scatterGridX;
            h = h * 31 + density.GetHashCode();
            h = h * 31 + baseWidth.GetHashCode();
            h = h * 31 + baseHeight.GetHashCode();
            h = h * 31 + widthVariation.GetHashCode();
            h = h * 31 + heightVariation.GetHashCode();
            h = h * 31 + heightOffset.GetHashCode();
            h = h * 31 + edgeMin.GetHashCode();
            h = h * 31 + edgeMax.GetHashCode();
            h = h * 31 + gradientBoost.GetHashCode();
            h = h * 31 + clusterScale.GetHashCode();
            h = h * 31 + clusterStrength.GetHashCode();
            h = h * 31 + clusterDensityBoost.GetHashCode();
            h = h * 31 + maxInstances;
            h = h * 31 + (useStreaming ? 1 : 0);
            if (useStreaming)
            {
                h = h * 31 + chunkSize.GetHashCode();
                h = h * 31 + maxInstancesPerChunk;
                h = h * 31 + maxActiveChunks;
            }
            if (terrain != null && terrain.terrainData != null)
            {
                var s = terrain.terrainData.size;
                h = h * 31 + s.x.GetHashCode();
                h = h * 31 + s.y.GetHashCode();
                h = h * 31 + s.z.GetHashCode();
            }
            return h;
        }
    }

    string GetCacheDir()
    {
        return System.IO.Path.Combine(Application.dataPath, "..", "Library", "GrassCache");
    }

    string GetNonStreamingCachePath()
    {
        return System.IO.Path.Combine(GetCacheDir(), "grass_scatter.bin");
    }

    /// <summary>
    /// Save the full non-streaming scatter buffer to disk.
    /// </summary>
    void SaveCacheToDisk()
    {
        if (_scatteredCount <= 0 || _grassBuffer == null) return;

        try
        {
            string dir = GetCacheDir();
            System.IO.Directory.CreateDirectory(dir);
            string path = GetNonStreamingCachePath();

            // Read only the scattered instances (not the entire buffer)
            float[] raw = new float[_scatteredCount * 8];
            _grassBuffer.GetData(raw, 0, 0, _scatteredCount * 8);

            // Write header + data
            int hash = ComputeParamHash();
            byte[] dataBytes = new byte[_scatteredCount * 32];
            System.Buffer.BlockCopy(raw, 0, dataBytes, 0, dataBytes.Length);

            using (var fs = System.IO.File.Create(path))
            using (var bw = new System.IO.BinaryWriter(fs))
            {
                bw.Write(hash);
                bw.Write(_scatteredCount);
                bw.Write(dataBytes);
            }

            Debug.Log($"[GrassSpawner] Cached {_scatteredCount:N0} instances to disk ({dataBytes.Length / 1024}KB).");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[GrassSpawner] Failed to save cache: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to restore non-streaming scatter data from disk cache.
    /// Returns true if successful (scatter can be skipped).
    /// </summary>
    bool TryLoadCacheFromDisk()
    {
        string path = GetNonStreamingCachePath();
        if (!System.IO.File.Exists(path)) return false;

        try
        {
            using (var fs = System.IO.File.OpenRead(path))
            using (var br = new System.IO.BinaryReader(fs))
            {
                int storedHash = br.ReadInt32();
                int currentHash = ComputeParamHash();
                if (storedHash != currentHash)
                {
                    Debug.Log("[GrassSpawner] Cache invalidated (parameters changed). Will re-scatter.");
                    return false;
                }

                int count = br.ReadInt32();
                if (count <= 0 || count > maxInstances) return false;

                byte[] dataBytes = br.ReadBytes(count * 32);
                if (dataBytes.Length != count * 32) return false;

                float[] raw = new float[count * 8];
                System.Buffer.BlockCopy(dataBytes, 0, raw, 0, dataBytes.Length);

                EnsureBuffers();
                EnsureMaterial();
                CacheKernels();

                // Upload only the instance data to the start of the GPU buffer
                _grassBuffer.SetData(raw, 0, 0, raw.Length);

                _scatteredCount = count;
                _isScattered = true;

                // World bounds
                Vector3 tPos = terrain.transform.position;
                Vector3 tSize = terrain.terrainData.size;
                _worldBounds = new Bounds(tPos + tSize * 0.5f, tSize + Vector3.up * baseHeight * 2f);

                Debug.Log($"[GrassSpawner] Loaded {count:N0} cached instances from disk.");
                return true;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[GrassSpawner] Failed to load cache: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Save a streaming chunk's GPU data to the CPU-side cache dictionary.
    /// Called when a chunk is about to be unloaded from GPU.
    /// </summary>
    void SaveChunkToCpuCache(Vector2Int coords, int slot, int count)
    {
        if (count <= 0 || _grassBuffer == null) return;
        if (_chunkCpuCache == null)
            _chunkCpuCache = new Dictionary<Vector2Int, (int, float[])>();

        try
        {
            // Partial GPU readback: only read this chunk's slot, not the entire buffer
            int floatOffset = slot * maxInstancesPerChunk * 8;
            int floatCount  = count * 8;
            float[] chunkData = new float[floatCount];
            _grassBuffer.GetData(chunkData, 0, floatOffset, floatCount);

            _chunkCpuCache[coords] = (count, chunkData);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[GrassSpawner] Failed to cache chunk {coords}: {ex.Message}");
        }
    }

    /// <summary>
    /// Try to restore a chunk from CPU cache into a GPU buffer slot.
    /// Returns true if the chunk was found in cache.
    /// </summary>
    bool TryLoadChunkFromCpuCache(Vector2Int coords, int slot, out int count)
    {
        count = 0;
        if (_chunkCpuCache == null || !_chunkCpuCache.ContainsKey(coords))
            return false;

        var cached = _chunkCpuCache[coords];
        count = cached.count;
        float[] data = cached.data;
        if (data == null || data.Length != count * 8)
            return false;

        try
        {
            // Partial GPU upload: write directly into the target slot
            int floatOffset = slot * maxInstancesPerChunk * 8;
            _grassBuffer.SetData(data, 0, floatOffset, data.Length);

            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[GrassSpawner] Failed to restore chunk {coords} from cache: {ex.Message}");
            count = 0;
            return false;
        }
    }

    /// <summary>
    /// Delete all cached data (disk + CPU).
    /// </summary>
    public void ClearAllCaches()
    {
        _chunkCpuCache?.Clear();

        try
        {
            string dir = GetCacheDir();
            if (System.IO.Directory.Exists(dir))
                System.IO.Directory.Delete(dir, true);
            Debug.Log("[GrassSpawner] All caches cleared.");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[GrassSpawner] Failed to clear disk cache: {ex.Message}");
        }
    }

    // ================================================================
    //  MATERIAL & MESH
    // ================================================================

    void EnsureMaterial()
    {
        if (_grassMat != null) return;

        Shader shader = Shader.Find("Hidden/ContactGrassRender");
        if (shader == null)
        {
            Debug.LogError("[GrassSpawner] Shader 'Hidden/ContactGrassRender' not found. " +
                "Make sure ContactGrassRender.shader exists in your project.");
            return;
        }

        _grassMat = new Material(shader);
        _grassMat.hideFlags = HideFlags.HideAndDontSave;

        if (grassTexture != null)
            _grassMat.SetTexture("_MainTex", grassTexture);
    }

    /// <summary>
    /// Builds a 3-quad cross mesh (star pattern at 0°/60°/120° around Y).
    /// Each quad is -0.5..0.5 on XY. The cross looks volumetric from any angle.
    /// Total: 12 vertices, 6 triangles (12 double-sided = 12 tris).
    /// </summary>
    void BuildQuadMesh()
    {
        if (_quadMesh != null) return;

        _quadMesh = new Mesh();
        _quadMesh.name = "GrassCrossQuad";

        // 3 quads at 0°, 60°, 120° around Y axis
        float[] angles = { 0f, 60f, 120f };
        var verts = new Vector3[12];
        var uvs   = new Vector2[12];
        var norms = new Vector3[12];
        var tris  = new int[18]; // 6 tris × 3 indices

        for (int q = 0; q < 3; q++)
        {
            float rad = angles[q] * Mathf.Deg2Rad;
            float s = Mathf.Sin(rad);
            float c = Mathf.Cos(rad);

            // Quad corners in local space (XY plane, rotated around Y)
            // Bottom-left, bottom-right, top-right, top-left
            int vi = q * 4;
            verts[vi + 0] = new Vector3(-0.5f * c, -0.5f, -0.5f * s);
            verts[vi + 1] = new Vector3( 0.5f * c, -0.5f,  0.5f * s);
            verts[vi + 2] = new Vector3( 0.5f * c,  0.5f,  0.5f * s);
            verts[vi + 3] = new Vector3(-0.5f * c,  0.5f, -0.5f * s);

            uvs[vi + 0] = new Vector2(0, 0);
            uvs[vi + 1] = new Vector2(1, 0);
            uvs[vi + 2] = new Vector2(1, 1);
            uvs[vi + 3] = new Vector2(0, 1);

            // Normal perpendicular to this quad
            Vector3 n = new Vector3(-s, 0, c);
            norms[vi + 0] = n;
            norms[vi + 1] = n;
            norms[vi + 2] = n;
            norms[vi + 3] = n;

            // Two triangles per quad (double-sided via Cull Off in shader)
            int ti = q * 6;
            tris[ti + 0] = vi + 0;
            tris[ti + 1] = vi + 2;
            tris[ti + 2] = vi + 1;
            tris[ti + 3] = vi + 0;
            tris[ti + 4] = vi + 3;
            tris[ti + 5] = vi + 2;
        }

        _quadMesh.vertices  = verts;
        _quadMesh.uv        = uvs;
        _quadMesh.normals   = norms;
        _quadMesh.triangles = tris;
        _quadMesh.UploadMeshData(true);
    }

    // ================================================================
    //  DEBUG GIZMOS
    // ================================================================

    #if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        Vector3 pos = terrain.transform.position;
        Vector3 size = td.size;

        // Terrain bounds
        Gizmos.color = new Color(0.3f, 0.8f, 0.3f, 0.15f);
        Gizmos.DrawWireCube(pos + size * 0.5f, size);

        // Render distance sphere from scene camera
        if (SceneView.lastActiveSceneView != null)
        {
            Vector3 camPos = SceneView.lastActiveSceneView.camera.transform.position;
            Gizmos.color = new Color(1f, 1f, 0f, 0.06f);
            Gizmos.DrawWireSphere(camPos, maxRenderDistance);
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.06f);
            Gizmos.DrawWireSphere(camPos, maxRenderDistance * fadeStartFraction);

            // Streaming distance sphere
            if (useStreaming)
            {
                Gizmos.color = new Color(0f, 0.8f, 1f, 0.06f);
                Gizmos.DrawWireSphere(camPos, streamingDistance);
            }
        }

        // Visualise active chunk bounds
        if (useStreaming && _activeChunks != null && _activeChunks.Count > 0)
        {
            Gizmos.color = new Color(0f, 1f, 0.5f, 0.08f);
            foreach (var kvp in _activeChunks)
            {
                var c = kvp.Key;
                Vector3 chunkMin = new Vector3(
                    _terrainOrigin.x + c.x * chunkSize,
                    _terrainOrigin.y,
                    _terrainOrigin.z + c.y * chunkSize
                );
                Vector3 chunkCenter = chunkMin + new Vector3(chunkSize * 0.5f, _terrainSize.y * 0.5f, chunkSize * 0.5f);
                Vector3 chunkExtent = new Vector3(chunkSize, _terrainSize.y, chunkSize);
                Gizmos.DrawWireCube(chunkCenter, chunkExtent);
            }
        }

        // Debug scatter positions (reads from first active chunk or start of buffer)
        if (debugShowPositions && _grassBuffer != null && _scatteredCount > 0)
        {
            if (_debugPositions == null)
            {
                int readCount = Mathf.Min(debugMaxGizmos, _scatteredCount);

                // For streaming, find the first active chunk's buffer offset
                int readOffset = 0;
                int readAvailable = _scatteredCount;
                if (useStreaming && _activeChunks != null)
                {
                    foreach (var kvp in _activeChunks)
                    {
                        if (kvp.Value.instanceCount > 0)
                        {
                            readOffset = kvp.Value.slotIndex * maxInstancesPerChunk;
                            readAvailable = kvp.Value.instanceCount;
                            break;
                        }
                    }
                }
                readCount = Mathf.Min(readCount, readAvailable);
                if (readCount <= 0) return;

                float[] rawData = new float[readCount * 8];
                _grassBuffer.GetData(rawData, 0, readOffset * 8, readCount * 8);

                _debugPositions = new Vector3[readCount];
                for (int i = 0; i < readCount; i++)
                {
                    _debugPositions[i] = new Vector3(
                        rawData[i * 8 + 0],
                        rawData[i * 8 + 1],
                        rawData[i * 8 + 2]
                    );
                }
            }

            Gizmos.color = new Color(0f, 1f, 0f, 0.7f);
            foreach (var p in _debugPositions)
            {
                Gizmos.DrawWireSphere(p, 0.3f);
            }
        }
    }
    #endif
}

// =============================================================================
//  CUSTOM EDITOR
// =============================================================================
#if UNITY_EDITOR
[CustomEditor(typeof(ContactGrassSpawner))]
public class ContactGrassSpawnerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var spawner = (ContactGrassSpawner)target;

        EditorGUILayout.Space(8);

        // ── Diagnostic checks ──
        bool hasMask = spawner.maskGenerator != null && spawner.maskGenerator.MaskTexture != null;
        bool hasCompute = spawner.scatterCompute != null;
        bool hasTerrain = spawner.terrain != null;

        if (!hasCompute)
            EditorGUILayout.HelpBox(
                "Compute Shader not assigned!\n" +
                "Drag 'ContactGrassScatter' compute asset into the Scatter Compute slot.",
                MessageType.Error);

        if (!hasMask)
            EditorGUILayout.HelpBox(
                "No contact mask available.\n" +
                "Make sure TerrainContactMaskGenerator exists and has baked a mask.",
                MessageType.Warning);

        if (!hasTerrain)
            EditorGUILayout.HelpBox("No terrain found.", MessageType.Warning);

        // ── Info box ──
        string statusLine;
        float bufferMB;
        MessageType msgType = MessageType.Info;

        if (spawner.useStreaming)
        {
            // Streaming info
            bufferMB = (spawner.maxActiveChunks * spawner.maxInstancesPerChunk * 32f * 2f) / (1024f * 1024f);
            int activeChunks = spawner.ActiveChunkCount;

            if (activeChunks > 0)
            {
                statusLine = $"Active Chunks: {activeChunks} / {spawner.maxActiveChunks}\n" +
                             $"Instances: {spawner.TotalScatteredCount:N0}  |  Visible: {spawner.LastVisibleCount:N0}" +
                             (spawner.debugSkipCulling ? " (culling OFF)" : "");
            }
            else
                statusLine = "Streaming active \u2014 move camera near buildings to load chunks.";

            EditorGUILayout.HelpBox(
                $"STREAMING MODE  |  Chunk: {spawner.chunkSize}m  |  Range: {spawner.streamingDistance}m\n" +
                $"Buffer: {bufferMB:F1} MB  [{spawner.maxActiveChunks} chunks \u00d7 {spawner.maxInstancesPerChunk}/chunk]\n" +
                $"Render: {spawner.maxRenderDistance}m (fade {spawner.maxRenderDistance * spawner.fadeStartFraction:F0}m, cull <{spawner.fadeThreshold:P0})\n" +
                $"Scale: \u00d7{spawner.uniformScale:F2}  |  Cluster: {spawner.clusterStrength:F2} @ {spawner.clusterScale:F3}\n" +
                statusLine,
                msgType);
        }
        else
        {
            // Legacy scatter-all info
            int gridX = spawner.scatterGridX;
            float aspect = 1f;
            if (spawner.terrain != null && spawner.terrain.terrainData != null)
            {
                Vector3 s = spawner.terrain.terrainData.size;
                aspect = s.z / s.x;
            }
            int gridY = Mathf.CeilToInt(gridX * aspect);
            long totalSlots = (long)gridX * gridY;
            int estInstances = Mathf.CeilToInt(totalSlots * spawner.density * 0.5f);
            bufferMB = (spawner.maxInstances * 32f * 2f) / (1024f * 1024f);

            if (estInstances > 300000) msgType = MessageType.Warning;
            if (estInstances > 500000) msgType = MessageType.Error;

            if (spawner.TotalScatteredCount > 0)
            {
                statusLine = $"Scattered: {spawner.TotalScatteredCount:N0} total\n" +
                             $"Last Visible: {spawner.LastVisibleCount:N0}" +
                             (spawner.debugSkipCulling ? " (culling OFF)" : "");
            }
            else
                statusLine = "Not yet scattered \u2014 click 'Scatter Grass' below.";

            EditorGUILayout.HelpBox(
                $"MANUAL MODE  |  Grid: {gridX} \u00d7 {gridY} = {totalSlots:N0} slots\n" +
                $"Est. Instances: ~{estInstances:N0}\n" +
                $"Buffer: {bufferMB:F1} MB  |  Distance: {spawner.maxRenderDistance}m (fade {spawner.maxRenderDistance * spawner.fadeStartFraction:F0}m)\n" +
                $"Scale: \u00d7{spawner.uniformScale:F2}  |  Cluster: {spawner.clusterStrength:F2} @ {spawner.clusterScale:F3}\n" +
                statusLine,
                msgType);
        }

        EditorGUILayout.Space(4);

        GUI.enabled = hasCompute && hasMask && hasTerrain;

        if (spawner.useStreaming)
        {
            // ── Reset Streaming button ──
            GUI.backgroundColor = new Color(0.3f, 0.7f, 0.3f);
            if (GUILayout.Button("Reset Streaming (Reload All Chunks)", GUILayout.Height(30)))
            {
                spawner.ResetStreaming();
                EditorUtility.SetDirty(spawner);
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = Color.white;
        }
        else
        {
            // ── Scatter button (manual mode) ──
            GUI.backgroundColor = new Color(0.3f, 0.7f, 0.3f);
            if (GUILayout.Button("Scatter Grass", GUILayout.Height(30)))
            {
                spawner.ScatterGrass();
                EditorUtility.SetDirty(spawner);
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = Color.white;
        }
        GUI.enabled = true;

        // ── Clear button ──
        if (spawner.TotalScatteredCount > 0 || spawner.ActiveChunkCount > 0)
        {
            if (GUILayout.Button("Clear All Grass"))
            {
                if (spawner.useStreaming)
                {
                    spawner.ResetStreaming();
                }
                else
                {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    typeof(ContactGrassSpawner).GetField("_isScattered", flags)?.SetValue(spawner, false);
                    typeof(ContactGrassSpawner).GetField("_scatteredCount", flags)?.SetValue(spawner, 0);
                    typeof(ContactGrassSpawner).GetField("_debugPositions", flags)?.SetValue(spawner, null);
                }
                EditorUtility.SetDirty(spawner);
                SceneView.RepaintAll();
            }
        }

        // ── Cache section ──
        if (spawner.enableCache)
        {
            EditorGUILayout.Space(4);

            // Show cache status
            string cachePath = System.IO.Path.Combine(
                Application.dataPath, "..", "Library", "GrassCache", "grass_scatter.bin");
            bool cacheExists = System.IO.File.Exists(cachePath);
            string cacheInfo = cacheExists
                ? $"Disk cache: {new System.IO.FileInfo(cachePath).Length / 1024}KB"
                : "Disk cache: none";

            EditorGUILayout.HelpBox(
                $"CACHE  |  {cacheInfo}\n" +
                $"scatterOnPlay={spawner.scatterOnPlay}  |  " +
                (spawner.scatterOnPlay
                    ? "Will auto-load from cache or scatter on Play."
                    : "Disabled \u2014 enable 'Scatter On Play' for auto-start."),
                MessageType.None);

            GUI.backgroundColor = new Color(0.9f, 0.5f, 0.3f);
            if (GUILayout.Button("Clear All Caches"))
            {
                spawner.ClearAllCaches();
                EditorUtility.SetDirty(spawner);
            }
            GUI.backgroundColor = Color.white;
        }

        // ── Repaint for live updates ──
        if (spawner.TotalScatteredCount > 0 || spawner.ActiveChunkCount > 0)
            Repaint();
    }
}
#endif
