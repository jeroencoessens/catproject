// =============================================================================
// ChunkedEdgeSpawner.cs
//
// Self-contained chunk-streaming foliage spawner for building-terrain edges.
// Detects object footprints via per-chunk orthographic mask capture, finds
// edges with a Sobel filter in the compute shader, and renders foliage with
// GPU instancing (DrawMeshInstancedIndirect).
//
// ─── HOW IT WORKS ────────────────────────────────────────────────────────
//   1. Terrain is divided into a grid of chunks (chunkSize × chunkSize).
//   2. Each frame, chunks within loadRadius of the camera are streamed in.
//   3. When a new chunk loads:
//        a) A small orthographic camera renders objects on objectLayers
//           from above into a shared work RenderTexture (footprint mask).
//        b) Optional Gaussian blur softens the mask (smooths noisy geometry).
//        c) A compute shader runs Sobel edge detection on the mask.
//           High gradient = object boundary → foliage instance placed.
//        d) Results are stored in a GPU buffer slot for that chunk.
//   4. Every frame, a frustum + distance cull pass filters visible instances.
//   5. DrawMeshInstancedIndirect renders only the survivors.
//   6. When chunks leave range, their data is saved to CPU cache so
//      revisiting the area skips re-computation.
//
// ─── USAGE ───────────────────────────────────────────────────────────────
//   - Add this component to any GameObject.
//   - Assign the Terrain (auto-detected if null).
//   - Assign the EdgeScatter compute shader.
//   - Configure objectLayers (EXCLUDE terrain layer!).
//   - Tune density, edgeThreshold, chunkSize, loadRadius.
//   - One spawner per foliage type. Add multiple for grass + rocks.
//   - Hit Play — chunks stream automatically.
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class ChunkedEdgeSpawner : MonoBehaviour
{
    // ── References ─────────────────────────────────────────────────────
    [Header("References")]
    [Tooltip("Terrain to spawn foliage on. Auto-detected if null.")]
    public Terrain terrain;

    [Tooltip("EdgeScatter.compute — drag the compute shader asset here.")]
    public ComputeShader scatterCompute;

    // ── Object Detection ───────────────────────────────────────────────
    [Header("Object Detection")]
    [Tooltip("Layers containing objects that produce footprints (buildings, walls, etc.).\n" +
             "IMPORTANT: Exclude the Terrain layer or the entire mask will be white.")]
    public LayerMask objectLayers = ~0;

    // ── Chunk Streaming ────────────────────────────────────────────────
    [Header("Chunk Streaming")]
    [Tooltip("World-space size of each chunk (metres). 30–80m typical.")]
    [Range(10f, 200f)]
    public float chunkSize = 50f;

    [Tooltip("Distance from camera within which chunks are loaded.")]
    public float loadRadius = 200f;

    [Tooltip("Max simultaneously active chunks. Memory = maxActiveChunks × maxInstancesPerChunk × 32 bytes × 2.")]
    [Range(16, 512)]
    public int maxActiveChunks = 128;

    [Tooltip("Max chunks to load per frame. Higher = faster streaming, bigger spikes.")]
    [Range(1, 16)]
    public int chunkLoadBudget = 4;

    // ── Mask Capture ───────────────────────────────────────────────────
    [Header("Mask Capture")]
    [Tooltip("Resolution of the per-chunk footprint mask (pixels per side).\n" +
             "64 = coarse (~0.78m/px at 50m chunks), 128 = balanced, 256 = fine.")]
    [Range(32, 512)]
    public int maskResolution = 128;

    [Tooltip("Blur passes on the captured mask before edge detection.\n" +
             "Smooths noisy geometry. 0 = sharp, 1–2 = typical, 3+ = very soft.")]
    [Range(0, 6)]
    public int blurPasses = 1;

    [Tooltip("Blur spread per pass in texels.")]
    [Range(0.5f, 4f)]
    public float blurSpread = 1.5f;

    // ── Scatter ────────────────────────────────────────────────────────
    [Header("Scatter")]
    [Tooltip("Grid density per chunk for scatter sampling.\n" +
             "128 = 16k potential points per chunk. Higher = denser but slower scatter.")]
    [Range(32, 512)]
    public int scatterGrid = 128;

    [Tooltip("Spawn probability per grid point at full edge strength.")]
    [Range(0.01f, 2f)]
    public float density = 0.5f;

    [Tooltip("Sobel gradient threshold. Only spawn where gradient exceeds this.\n" +
             "Lower = wider edge band. Higher = sharper, tighter edge.")]
    [Range(0.01f, 1f)]
    public float edgeThreshold = 0.15f;

    [Tooltip("Max instances stored per chunk. If exceeded, extras are simply dropped.")]
    [Range(256, 16384)]
    public int maxInstancesPerChunk = 4096;

    // ── Foliage Appearance ─────────────────────────────────────────────
    [Header("Foliage Appearance")]
    [Tooltip("Foliage texture (alpha cutout). White quad if null.")]
    public Texture2D foliageTexture;

    public Color baseColor = new Color(0.35f, 0.55f, 0.20f, 1f);
    public Color tipColor  = new Color(0.60f, 0.80f, 0.30f, 1f);

    [Range(0f, 1f)]
    public float alphaCutoff = 0.4f;

    [Range(0f, 0.5f)]
    public float colorVariation = 0.15f;

    // ── Foliage Size ───────────────────────────────────────────────────
    [Header("Foliage Size")]
    [Tooltip("Master scale multiplier. Works in real-time without re-scattering.")]
    [Range(0.01f, 5f)]
    public float uniformScale = 1f;

    public float baseWidth  = 0.12f;
    public float baseHeight = 0.25f;

    [Range(0f, 0.8f)]
    public float sizeVariation = 0.3f;

    public float heightOffset = 0f;

    // ── Wind ───────────────────────────────────────────────────────────
    [Header("Wind")]
    public float windSpeed    = 1.5f;
    public float windStrength = 0.15f;

    // ── Culling & Rendering ────────────────────────────────────────────
    [Header("Culling & Rendering")]
    [Tooltip("Max render distance. Foliage beyond this is culled entirely.")]
    public float maxRenderDistance = 200f;

    [Tooltip("Fraction of maxRenderDistance where fade begins.")]
    [Range(0.3f, 0.95f)]
    public float fadeStartFraction = 0.7f;

    [Tooltip("Instances below this fade are culled (not drawn).")]
    [Range(0.01f, 0.3f)]
    public float fadeThreshold = 0.05f;

    public ShadowCastingMode shadowMode = ShadowCastingMode.Off;
    public int renderLayer = 0;

    // ── Debug ──────────────────────────────────────────────────────────
    [Header("Debug")]
    public bool showDebug = false;

    // ═══════════════════════════════════════════════════════════════════
    //  INTERNAL STATE
    // ═══════════════════════════════════════════════════════════════════

    // Instance struct — must match compute shader layout (32 bytes)
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct FoliageInstance
    {
        public Vector3 position;
        public float   rotation;
        public Vector2 scale;
        public float   colorVar;
        public float   fade;
    }

    // Chunk tracking
    struct ChunkInfo
    {
        public int slotIndex;
        public int instanceCount;
    }

    Dictionary<Vector2Int, ChunkInfo> _activeChunks;
    Dictionary<Vector2Int, FoliageInstance[]> _cpuCache;
    Stack<int> _freeSlots;

    // GPU resources
    ComputeBuffer _foliageBuffer;
    ComputeBuffer _counterBuffer;
    ComputeBuffer _visibleBuffer;
    ComputeBuffer _cullCounterBuffer;
    ComputeBuffer _argsBuffer;

    // Mask capture
    RenderTexture _workMaskRT;
    RenderTexture _blurTempRT;
    Material      _blurMat;
    Camera        _maskCam;
    GameObject    _maskCamGO;

    // Rendering
    Material _renderMat;
    Mesh     _crossMesh;
    Bounds   _worldBounds;

    // Terrain cache
    Vector3 _terrainOrigin;
    Vector3 _terrainSize;

    // Kernel IDs
    int  _kClear, _kScatter, _kClearCull, _kCull;
    bool _kernelsCached;

    // Cull helpers
    readonly Plane[]   _planes   = new Plane[6];
    readonly Vector4[] _planeVec = new Vector4[6];
    readonly uint[]    _argsReset = new uint[5];

    // Stats
    int _scatteredCount;
    int _lastVisibleCount;

    // ── Public Read-Only ───────────────────────────────────────────────
    public int ScatteredCount => _scatteredCount;
    public int VisibleCount   => _lastVisibleCount;
    public int ActiveChunkCount => _activeChunks?.Count ?? 0;

    // ═══════════════════════════════════════════════════════════════════
    //  LIFECYCLE
    // ═══════════════════════════════════════════════════════════════════

    void OnEnable()
    {
        if (terrain == null) terrain = FindFirstObjectByType<Terrain>();

        _activeChunks = new Dictionary<Vector2Int, ChunkInfo>();
        _cpuCache     = new Dictionary<Vector2Int, FoliageInstance[]>();
        _freeSlots    = new Stack<int>();

        for (int i = maxActiveChunks - 1; i >= 0; i--)
            _freeSlots.Push(i);

        CacheKernels();
        EnsureResources();

        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        CleanupAll();
    }

    void Update()
    {
        if (terrain == null || terrain.terrainData == null) return;
        if (scatterCompute == null) return;

        _terrainOrigin = terrain.transform.position;
        _terrainSize   = terrain.terrainData.size;

        UpdateStreaming();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  STREAMING — load / unload chunks as camera moves
    // ═══════════════════════════════════════════════════════════════════

    void UpdateStreaming()
    {
        Camera cam = Camera.main;
        #if UNITY_EDITOR
        if (!Application.isPlaying && SceneView.lastActiveSceneView != null)
            cam = SceneView.lastActiveSceneView.camera;
        #endif
        if (cam == null) return;

        EnsureResources();

        Vector3 camPos = cam.transform.position;

        // Chunk grid bounds around camera
        int cMinX = Mathf.FloorToInt((camPos.x - loadRadius - _terrainOrigin.x) / chunkSize);
        int cMaxX = Mathf.FloorToInt((camPos.x + loadRadius - _terrainOrigin.x) / chunkSize);
        int cMinZ = Mathf.FloorToInt((camPos.z - loadRadius - _terrainOrigin.z) / chunkSize);
        int cMaxZ = Mathf.FloorToInt((camPos.z + loadRadius - _terrainOrigin.z) / chunkSize);

        int gridMaxX = Mathf.CeilToInt(_terrainSize.x / chunkSize) - 1;
        int gridMaxZ = Mathf.CeilToInt(_terrainSize.z / chunkSize) - 1;
        cMinX = Mathf.Max(cMinX, 0); cMaxX = Mathf.Min(cMaxX, gridMaxX);
        cMinZ = Mathf.Max(cMinZ, 0); cMaxZ = Mathf.Min(cMaxZ, gridMaxZ);

        // Determine needed set
        HashSet<Vector2Int> needed = new HashSet<Vector2Int>();
        float chunkHalf = chunkSize * 0.5f;
        for (int cx = cMinX; cx <= cMaxX; cx++)
        {
            for (int cz = cMinZ; cz <= cMaxZ; cz++)
            {
                Vector2 center = new Vector2(
                    _terrainOrigin.x + cx * chunkSize + chunkHalf,
                    _terrainOrigin.z + cz * chunkSize + chunkHalf
                );
                if (Vector2.Distance(new Vector2(camPos.x, camPos.z), center) <= loadRadius)
                    needed.Add(new Vector2Int(cx, cz));
            }
        }

        // Unload chunks outside range
        List<Vector2Int> toUnload = new List<Vector2Int>();
        foreach (var kv in _activeChunks)
        {
            if (!needed.Contains(kv.Key))
                toUnload.Add(kv.Key);
        }
        foreach (var coord in toUnload)
            UnloadChunk(coord);

        // Load new chunks within budget
        int loaded = 0;
        foreach (var coord in needed)
        {
            if (loaded >= chunkLoadBudget) break;
            if (_activeChunks.ContainsKey(coord)) continue;
            if (_freeSlots.Count == 0) continue;

            LoadChunk(coord);
            loaded++;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CHUNK LOAD / UNLOAD
    // ═══════════════════════════════════════════════════════════════════

    void LoadChunk(Vector2Int coord)
    {
        if (_freeSlots.Count == 0) return;
        int slot = _freeSlots.Pop();
        int count;

        // Try CPU cache first
        if (_cpuCache.TryGetValue(coord, out FoliageInstance[] cached))
        {
            count = cached.Length;
            if (count > 0)
                _foliageBuffer.SetData(cached, 0, slot * maxInstancesPerChunk, count);
            _cpuCache.Remove(coord);
        }
        else
        {
            // Capture mask → scatter
            CaptureMaskForChunk(coord);
            count = DispatchScatter(coord, slot);
        }

        _activeChunks[coord] = new ChunkInfo { slotIndex = slot, instanceCount = count };
        RecalcScatteredCount();
    }

    void UnloadChunk(Vector2Int coord)
    {
        if (!_activeChunks.TryGetValue(coord, out ChunkInfo info)) return;

        // Save to CPU cache
        if (info.instanceCount > 0 && _foliageBuffer != null)
        {
            FoliageInstance[] data = new FoliageInstance[info.instanceCount];
            _foliageBuffer.GetData(data, 0, info.slotIndex * maxInstancesPerChunk, info.instanceCount);
            _cpuCache[coord] = data;
        }

        _freeSlots.Push(info.slotIndex);
        _activeChunks.Remove(coord);
        RecalcScatteredCount();
    }

    void RecalcScatteredCount()
    {
        _scatteredCount = 0;
        foreach (var kv in _activeChunks)
            _scatteredCount += kv.Value.instanceCount;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MASK CAPTURE — per-chunk orthographic render
    // ═══════════════════════════════════════════════════════════════════

    void CaptureMaskForChunk(Vector2Int coord)
    {
        EnsureMaskCamera();

        // Chunk world bounds
        float chunkMinX = _terrainOrigin.x + coord.x * chunkSize;
        float chunkMinZ = _terrainOrigin.z + coord.y * chunkSize;
        float chunkCenterX = chunkMinX + chunkSize * 0.5f;
        float chunkCenterZ = chunkMinZ + chunkSize * 0.5f;

        // Position camera above chunk center looking down
        float camY = _terrainOrigin.y + _terrainSize.y + 100f;
        _maskCamGO.transform.position = new Vector3(chunkCenterX, camY, chunkCenterZ);
        _maskCamGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        _maskCam.orthographic     = true;
        _maskCam.orthographicSize = chunkSize * 0.5f;
        _maskCam.aspect           = 1f; // square chunks
        _maskCam.nearClipPlane    = 1f;
        _maskCam.farClipPlane     = _terrainSize.y + 200f;
        _maskCam.clearFlags       = CameraClearFlags.SolidColor;
        _maskCam.backgroundColor  = Color.black;
        _maskCam.cullingMask      = objectLayers;
        _maskCam.targetTexture    = _workMaskRT;

        // Render with replacement shader — all objects become white
        Shader whiteShader = Shader.Find("Hidden/EdgeSpawner/FootprintCapture");
        if (whiteShader != null)
            _maskCam.SetReplacementShader(whiteShader, "");

        _maskCam.Render();
        _maskCam.targetTexture = null;

        // Optional blur
        ApplyBlur();
    }

    void EnsureMaskCamera()
    {
        if (_maskCamGO != null) return;

        _maskCamGO = new GameObject("_EdgeSpawnerMaskCam");
        _maskCamGO.hideFlags = HideFlags.HideAndDontSave;
        _maskCam = _maskCamGO.AddComponent<Camera>();
        _maskCam.enabled = false; // manual render only
    }

    void ApplyBlur()
    {
        if (_blurMat == null || blurPasses <= 0 || _workMaskRT == null) return;

        for (int i = 0; i < blurPasses; i++)
        {
            _blurMat.SetVector("_BlurDir", new Vector4(blurSpread / _workMaskRT.width, 0, 0, 0));
            Graphics.Blit(_workMaskRT, _blurTempRT, _blurMat);

            _blurMat.SetVector("_BlurDir", new Vector4(0, blurSpread / _workMaskRT.height, 0, 0));
            Graphics.Blit(_blurTempRT, _workMaskRT, _blurMat);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SCATTER — compute dispatch per chunk
    // ═══════════════════════════════════════════════════════════════════

    int DispatchScatter(Vector2Int coord, int slot)
    {
        if (!_kernelsCached) CacheKernels();
        if (_foliageBuffer == null || _counterBuffer == null) return 0;

        float chunkMinX = _terrainOrigin.x + coord.x * chunkSize;
        float chunkMinZ = _terrainOrigin.z + coord.y * chunkSize;

        // Clear counter
        _counterBuffer.SetData(new uint[] { 0 });

        // Set scatter params
        scatterCompute.SetBuffer(_kScatter, "_FoliageBuffer", _foliageBuffer);
        scatterCompute.SetBuffer(_kScatter, "_CounterBuffer", _counterBuffer);
        scatterCompute.SetTexture(_kScatter, "_ChunkMask", _workMaskRT);
        scatterCompute.SetTexture(_kScatter, "_HeightMap", terrain.terrainData.heightmapTexture);

        scatterCompute.SetVector("_TerrainOrigin", new Vector4(_terrainOrigin.x, _terrainOrigin.y, _terrainOrigin.z, 0));
        scatterCompute.SetVector("_TerrainSize", new Vector4(_terrainSize.x, _terrainSize.y, _terrainSize.z, 0));
        scatterCompute.SetFloats("_ChunkWorldMin", chunkMinX, chunkMinZ);
        scatterCompute.SetFloats("_ChunkWorldSize", chunkSize, chunkSize);
        scatterCompute.SetInt("_GridWidth", scatterGrid);
        scatterCompute.SetInt("_GridHeight", scatterGrid);
        scatterCompute.SetFloat("_Density", density);
        scatterCompute.SetFloat("_EdgeThreshold", edgeThreshold);
        scatterCompute.SetFloat("_BaseWidth", baseWidth);
        scatterCompute.SetFloat("_BaseHeight", baseHeight);
        scatterCompute.SetFloat("_SizeVariation", sizeVariation);
        scatterCompute.SetFloat("_HeightOffset", heightOffset);
        scatterCompute.SetFloat("_Seed", coord.x * 7919f + coord.y * 7727f);
        scatterCompute.SetInt("_BufferOffset", slot * maxInstancesPerChunk);
        scatterCompute.SetInt("_MaxPerChunk", maxInstancesPerChunk);

        // Dispatch
        int groupsX = Mathf.CeilToInt(scatterGrid / 8f);
        int groupsY = Mathf.CeilToInt(scatterGrid / 8f);
        scatterCompute.Dispatch(_kScatter, groupsX, groupsY, 1);

        // Read back count
        uint[] countArr = new uint[1];
        _counterBuffer.GetData(countArr);
        int count = Mathf.Min((int)countArr[0], maxInstancesPerChunk);

        return count;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CULL + DRAW — URP camera callback (every frame, per camera)
    // ═══════════════════════════════════════════════════════════════════

    void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        if (!isActiveAndEnabled) return;
        if (_activeChunks == null || _activeChunks.Count == 0) return;
        if (cam.cameraType == CameraType.Preview || cam.cameraType == CameraType.Reflection) return;
        if (_renderMat == null || scatterCompute == null) return;
        if (_foliageBuffer == null || _visibleBuffer == null) return;

        CullAndDraw(cam);
    }

    void CullAndDraw(Camera cam)
    {
        if (!_kernelsCached) CacheKernels();

        // Update material properties
        SetMaterialProperties();

        // Extract frustum planes
        GeometryUtility.CalculateFrustumPlanes(cam, _planes);
        for (int i = 0; i < 6; i++)
            _planeVec[i] = new Vector4(_planes[i].normal.x, _planes[i].normal.y, _planes[i].normal.z, _planes[i].distance);

        // Clear cull counter
        scatterCompute.SetBuffer(_kClearCull, "_CullCounterBuffer", _cullCounterBuffer);
        scatterCompute.Dispatch(_kClearCull, 1, 1, 1);

        // Set shared cull params
        scatterCompute.SetVectorArray("_FrustumPlanes", _planeVec);
        scatterCompute.SetVector("_CameraPos", cam.transform.position);
        scatterCompute.SetFloat("_MaxDist", maxRenderDistance);
        scatterCompute.SetFloat("_FadeStart", fadeStartFraction);
        scatterCompute.SetFloat("_FadeThreshold", fadeThreshold);

        int totalBufferSize = maxActiveChunks * maxInstancesPerChunk;
        scatterCompute.SetInt("_MaxVisible", totalBufferSize);
        scatterCompute.SetBuffer(_kCull, "_FoliageBufferRead", _foliageBuffer);
        scatterCompute.SetBuffer(_kCull, "_VisibleBuffer", _visibleBuffer);
        scatterCompute.SetBuffer(_kCull, "_CullCounterBuffer", _cullCounterBuffer);

        // Dispatch cull per active chunk
        foreach (var kv in _activeChunks)
        {
            if (kv.Value.instanceCount <= 0) continue;

            int offset = kv.Value.slotIndex * maxInstancesPerChunk;
            scatterCompute.SetInt("_CullOffset", offset);
            scatterCompute.SetInt("_TotalInstances", kv.Value.instanceCount);

            int groups = Mathf.CeilToInt(kv.Value.instanceCount / 64f);
            scatterCompute.Dispatch(_kCull, groups, 1, 1);
        }

        // Read visible count
        uint[] visCount = new uint[1];
        _cullCounterBuffer.GetData(visCount);
        _lastVisibleCount = (int)visCount[0];

        if (_lastVisibleCount <= 0) return;

        // Set draw args
        _argsReset[0] = (uint)_crossMesh.GetIndexCount(0);
        _argsReset[1] = (uint)_lastVisibleCount;
        _argsReset[2] = (uint)_crossMesh.GetIndexStart(0);
        _argsReset[3] = (uint)_crossMesh.GetBaseVertex(0);
        _argsReset[4] = 0;
        _argsBuffer.SetData(_argsReset);

        // Set visible buffer on material
        _renderMat.SetBuffer("_VisibleBuffer", _visibleBuffer);

        // Draw
        _worldBounds = new Bounds(
            _terrainOrigin + _terrainSize * 0.5f,
            _terrainSize + Vector3.one * 50f
        );

        Graphics.DrawMeshInstancedIndirect(
            _crossMesh, 0, _renderMat, _worldBounds, _argsBuffer,
            0, null, shadowMode, true, renderLayer
        );
    }

    void SetMaterialProperties()
    {
        if (_renderMat == null) return;

        _renderMat.SetColor("_BaseColor", baseColor);
        _renderMat.SetColor("_TipColor", tipColor);
        _renderMat.SetFloat("_Cutoff", alphaCutoff);
        _renderMat.SetFloat("_ColorVariation", colorVariation);
        _renderMat.SetFloat("_UniformScale", uniformScale);
        _renderMat.SetFloat("_WindSpeed", windSpeed);
        _renderMat.SetFloat("_WindStrength", windStrength);

        if (foliageTexture != null)
            _renderMat.SetTexture("_MainTex", foliageTexture);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  RESOURCE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════

    void CacheKernels()
    {
        if (scatterCompute == null) return;
        _kClear     = scatterCompute.FindKernel("Clear");
        _kScatter   = scatterCompute.FindKernel("ScatterChunk");
        _kClearCull = scatterCompute.FindKernel("ClearCull");
        _kCull      = scatterCompute.FindKernel("FrustumCull");
        _kernelsCached = true;
    }

    void EnsureResources()
    {
        int totalSlots = maxActiveChunks * maxInstancesPerChunk;

        // GPU buffers
        if (_foliageBuffer == null || _foliageBuffer.count != totalSlots)
        {
            ReleaseBuffer(ref _foliageBuffer);
            ReleaseBuffer(ref _visibleBuffer);
            ReleaseBuffer(ref _counterBuffer);
            ReleaseBuffer(ref _cullCounterBuffer);
            ReleaseBuffer(ref _argsBuffer);

            _foliageBuffer     = new ComputeBuffer(totalSlots, 32); // sizeof(FoliageInstance)
            _visibleBuffer     = new ComputeBuffer(totalSlots, 32);
            _counterBuffer     = new ComputeBuffer(1, sizeof(uint));
            _cullCounterBuffer = new ComputeBuffer(1, sizeof(uint));
            _argsBuffer        = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);

            // Rebuild slot stack
            _freeSlots.Clear();
            for (int i = maxActiveChunks - 1; i >= 0; i--)
                _freeSlots.Push(i);

            // Clear active chunks (buffer layout changed)
            if (_activeChunks != null) _activeChunks.Clear();
            _scatteredCount = 0;
        }

        // Work mask RT (shared, reused per chunk)
        if (_workMaskRT == null || _workMaskRT.width != maskResolution)
        {
            ReleaseRT(ref _workMaskRT);
            ReleaseRT(ref _blurTempRT);

            _workMaskRT = new RenderTexture(maskResolution, maskResolution, 16, RenderTextureFormat.R8);
            _workMaskRT.name = "EdgeSpawner_WorkMask";
            _workMaskRT.wrapMode   = TextureWrapMode.Clamp;
            _workMaskRT.filterMode = FilterMode.Bilinear;
            _workMaskRT.Create();

            _blurTempRT = new RenderTexture(maskResolution, maskResolution, 0, RenderTextureFormat.R8);
            _blurTempRT.name = "EdgeSpawner_BlurTemp";
            _blurTempRT.wrapMode   = TextureWrapMode.Clamp;
            _blurTempRT.filterMode = FilterMode.Bilinear;
            _blurTempRT.Create();
        }

        // Blur material
        if (_blurMat == null)
        {
            Shader blurShader = Shader.Find("Hidden/EdgeSpawner/MaskBlur");
            if (blurShader != null)
            {
                _blurMat = new Material(blurShader);
                _blurMat.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        // Render material
        if (_renderMat == null)
        {
            Shader renderShader = Shader.Find("Hidden/EdgeSpawner/FoliageRender");
            if (renderShader != null)
            {
                _renderMat = new Material(renderShader);
                _renderMat.hideFlags = HideFlags.HideAndDontSave;
            }
            else
            {
                Debug.LogError("[EdgeSpawner] FoliageRender shader not found.");
            }
        }

        // Cross mesh
        BuildCrossMesh();
    }

    void CleanupAll()
    {
        // Camera
        if (_maskCamGO != null)
        {
            if (Application.isPlaying) Destroy(_maskCamGO);
            else DestroyImmediate(_maskCamGO);
        }
        _maskCam   = null;
        _maskCamGO = null;

        // Buffers
        ReleaseBuffer(ref _foliageBuffer);
        ReleaseBuffer(ref _visibleBuffer);
        ReleaseBuffer(ref _counterBuffer);
        ReleaseBuffer(ref _cullCounterBuffer);
        ReleaseBuffer(ref _argsBuffer);

        // RTs
        ReleaseRT(ref _workMaskRT);
        ReleaseRT(ref _blurTempRT);

        // Materials
        if (_blurMat != null)
        {
            if (Application.isPlaying) Destroy(_blurMat);
            else DestroyImmediate(_blurMat);
            _blurMat = null;
        }
        if (_renderMat != null)
        {
            if (Application.isPlaying) Destroy(_renderMat);
            else DestroyImmediate(_renderMat);
            _renderMat = null;
        }

        // Mesh
        if (_crossMesh != null)
        {
            if (Application.isPlaying) Destroy(_crossMesh);
            else DestroyImmediate(_crossMesh);
            _crossMesh = null;
        }

        _activeChunks?.Clear();
        _cpuCache?.Clear();
        _freeSlots?.Clear();
        _scatteredCount   = 0;
        _lastVisibleCount = 0;
        _kernelsCached    = false;
    }

    static void ReleaseBuffer(ref ComputeBuffer buf)
    {
        if (buf != null) { buf.Release(); buf = null; }
    }

    static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt != null)
        {
            rt.Release();
            if (Application.isPlaying) Destroy(rt);
            else DestroyImmediate(rt);
            rt = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CROSS MESH — 3-quad star pattern (0°/60°/120°)
    // ═══════════════════════════════════════════════════════════════════

    void BuildCrossMesh()
    {
        if (_crossMesh != null) return;

        _crossMesh = new Mesh { name = "EdgeSpawner_CrossQuad" };
        _crossMesh.hideFlags = HideFlags.HideAndDontSave;

        int quadCount = 3;
        Vector3[] verts = new Vector3[quadCount * 4];
        Vector2[] uvs   = new Vector2[quadCount * 4];
        int[]     tris  = new int[quadCount * 6]; // single-sided; GPU Cull Off handles back

        float[] angles = { 0f, 60f, 120f };
        int vi = 0, ti = 0;

        for (int q = 0; q < quadCount; q++)
        {
            float rad = angles[q] * Mathf.Deg2Rad;
            float s = Mathf.Sin(rad);
            float c = Mathf.Cos(rad);

            // Quad corners: bottom-left, bottom-right, top-left, top-right
            // Local X rotated around Y by angle, Y is up
            Vector3 right = new Vector3(c, 0, s) * 0.5f;

            verts[vi + 0] = -right + Vector3.down * 0.5f;
            verts[vi + 1] =  right + Vector3.down * 0.5f;
            verts[vi + 2] = -right + Vector3.up   * 0.5f;
            verts[vi + 3] =  right + Vector3.up   * 0.5f;

            uvs[vi + 0] = new Vector2(0, 0);
            uvs[vi + 1] = new Vector2(1, 0);
            uvs[vi + 2] = new Vector2(0, 1);
            uvs[vi + 3] = new Vector2(1, 1);

            tris[ti + 0] = vi + 0;
            tris[ti + 1] = vi + 2;
            tris[ti + 2] = vi + 1;
            tris[ti + 3] = vi + 1;
            tris[ti + 4] = vi + 2;
            tris[ti + 5] = vi + 3;

            vi += 4;
            ti += 6;
        }

        _crossMesh.vertices  = verts;
        _crossMesh.uv        = uvs;
        _crossMesh.triangles = tris;
        _crossMesh.bounds    = new Bounds(Vector3.zero, Vector3.one);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PUBLIC API
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Force clear and reload all chunks.</summary>
    public void ResetAllChunks()
    {
        if (_activeChunks != null)
        {
            foreach (var kv in _activeChunks)
                _freeSlots.Push(kv.Value.slotIndex);
            _activeChunks.Clear();
        }
        _cpuCache?.Clear();
        _scatteredCount   = 0;
        _lastVisibleCount = 0;
    }

    /// <summary>Clear CPU cache (forces re-capture when chunks reload).</summary>
    public void ClearCache()
    {
        _cpuCache?.Clear();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GIZMOS
    // ═══════════════════════════════════════════════════════════════════

    #if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (terrain == null) return;

        Vector3 origin = terrain.transform.position;
        Vector3 size   = terrain.terrainData != null ? terrain.terrainData.size : Vector3.one * 100f;

        // Terrain bounds
        Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.15f);
        Gizmos.DrawWireCube(origin + size * 0.5f, size);

        // Active chunks
        if (_activeChunks == null) return;
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.25f);
        foreach (var kv in _activeChunks)
        {
            Vector3 chunkMin = new Vector3(
                origin.x + kv.Key.x * chunkSize,
                origin.y,
                origin.z + kv.Key.y * chunkSize
            );
            Vector3 chunkCenter = chunkMin + new Vector3(chunkSize * 0.5f, size.y * 0.5f, chunkSize * 0.5f);
            Gizmos.DrawWireCube(chunkCenter, new Vector3(chunkSize, size.y, chunkSize));
        }
    }
    #endif
}

// =============================================================================
//  CUSTOM EDITOR
// =============================================================================
#if UNITY_EDITOR
[CustomEditor(typeof(ChunkedEdgeSpawner))]
public class ChunkedEdgeSpawnerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var spawner = (ChunkedEdgeSpawner)target;

        EditorGUILayout.Space(8);

        // ── Memory Info ──
        int totalSlots = spawner.maxActiveChunks * spawner.maxInstancesPerChunk;
        float bufferMB = totalSlots * 32f * 2f / (1024f * 1024f); // foliage + visible
        float maskKB = spawner.maskResolution * spawner.maskResolution * 2f / 1024f; // work + blur temp

        string memInfo = $"Buffer: {totalSlots:N0} slots ({bufferMB:F1} MB)\n" +
                         $"Work mask: {spawner.maskResolution}×{spawner.maskResolution} ({maskKB:F0} KB)\n" +
                         $"Active chunks: {spawner.ActiveChunkCount} / {spawner.maxActiveChunks}\n" +
                         $"Scattered: {spawner.ScatteredCount:N0}  |  Visible: {spawner.VisibleCount:N0}";

        MessageType msgType = bufferMB > 100f ? MessageType.Error
                            : bufferMB > 50f  ? MessageType.Warning
                            : MessageType.Info;

        EditorGUILayout.HelpBox(memInfo, msgType);

        // ── Buttons ──
        EditorGUILayout.Space(4);

        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("Reset All Chunks", GUILayout.Height(28)))
        {
            spawner.ResetAllChunks();
            EditorUtility.SetDirty(spawner);
        }
        GUI.backgroundColor = Color.white;

        if (GUILayout.Button("Clear CPU Cache"))
        {
            spawner.ClearCache();
        }

        // ── Debug mask preview ──
        if (spawner.showDebug)
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("System Info", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Chunk size: {spawner.chunkSize}m");
            EditorGUILayout.LabelField($"Mask precision: {spawner.chunkSize / (float)spawner.maskResolution:F2}m per pixel");
            EditorGUILayout.LabelField($"Scatter grid: {spawner.scatterGrid}×{spawner.scatterGrid} = {spawner.scatterGrid * spawner.scatterGrid:N0} points/chunk");

            if (spawner.terrain != null && spawner.terrain.terrainData != null)
            {
                Vector3 size = spawner.terrain.terrainData.size;
                int totalChunksX = Mathf.CeilToInt(size.x / spawner.chunkSize);
                int totalChunksZ = Mathf.CeilToInt(size.z / spawner.chunkSize);
                EditorGUILayout.LabelField($"Terrain chunks: {totalChunksX}×{totalChunksZ} = {totalChunksX * totalChunksZ}");
            }
        }
    }
}
#endif
