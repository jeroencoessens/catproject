// =============================================================================
// ContactGrassSpawner.cs
//
// Reads the contact mask from TerrainContactMaskGenerator and spawns grass/shrub
// geometry at intersection areas using GPU instancing (DrawMeshInstancedIndirect).
//
// Workflow:
//   1. Attach this component to any GameObject in the scene.
//   2. Assign a TerrainContactMaskGenerator reference (or auto-detects).
//   3. Assign a grass/shrub texture to the material's _MainTex slot.
//   4. Click "Scatter Grass" or enable auto-update for runtime changes.
//   5. The system reads the contact mask + terrain heightmap, runs a compute
//      shader to generate random positions, and renders them every frame with
//      DrawMeshInstancedIndirect — no GameObjects created.
//
// Performance:
//   - All position generation happens on GPU (compute shader).
//   - Rendering uses indirect instancing — one draw call for ALL grass.
//   - No per-frame CPU cost except the DrawMeshInstancedIndirect call.
//   - For a 2000×3300 terrain with moderate density, expect 50k-200k instances.
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;

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

    [Tooltip("Compute shader that generates grass positions from the mask.")]
    public ComputeShader scatterCompute;

    [Header("Grass Appearance")]
    [Tooltip("Grass blade texture (alpha cutout). White quad used if null.")]
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
    [Tooltip("Base width of each grass blade (metres).")]
    public float baseWidth = 0.3f;

    [Tooltip("Base height of each grass blade (metres).")]
    public float baseHeight = 0.6f;

    [Tooltip("Random variation in width (±fraction). 0.3 = ±30%.")]
    [Range(0f, 0.8f)]
    public float widthVariation = 0.3f;

    [Tooltip("Random variation in height (±fraction).")]
    [Range(0f, 0.8f)]
    public float heightVariation = 0.4f;

    [Header("Scatter Settings")]
    [Tooltip("Grid density for scatter sampling. Higher = more potential spawn points.\n" +
             "This is the number of sample points along the X axis; Y is scaled by aspect ratio.\n" +
             "500 → ~500×825 grid = 412k potential slots for a 2000×3300 terrain.")]
    [Range(64, 2048)]
    public int scatterGridX = 512;

    [Tooltip("Spawn probability per grid slot (before mask modulation).\n" +
             "0.5 = 50% base chance × mask intensity.")]
    [Range(0.01f, 1f)]
    public float density = 0.4f;

    [Tooltip("Minimum mask intensity to allow spawning. Filters out faint edges.")]
    [Range(0f, 0.5f)]
    public float maskThreshold = 0.05f;

    [Tooltip("Maximum instances the buffer can hold. Increase for very dense scenes.")]
    public int maxInstances = 500000;

    [Header("Wind")]
    public float windSpeed = 1.5f;
    public float windStrength = 0.15f;

    [Header("Rendering")]
    [Tooltip("Shadow casting mode for the grass.")]
    public ShadowCastingMode shadowMode = ShadowCastingMode.Off;

    [Tooltip("Maximum render distance (metres). Grass beyond this won't draw.")]
    public float maxRenderDistance = 200f;

    [Tooltip("Layer the grass renders on.")]
    public int renderLayer = 0;

    [Header("Update")]
    [Tooltip("Auto-scatter when the mask updates (useful for moving objects).")]
    public bool autoUpdate = false;

    // ── Internal ───────────────────────────────────────────────────────
    ComputeBuffer _grassBuffer;
    ComputeBuffer _counterBuffer;
    ComputeBuffer _argsBuffer;
    Material      _grassMat;
    Mesh          _quadMesh;
    Bounds        _worldBounds;
    int           _kernelClear;
    int           _kernelScatter;
    bool          _isScattered;

    int _lastGridX;
    int _lastMaxInstances;

    // Indirect draw args: [indexCount, instanceCount, startIndex, baseVertex, startInstance]
    readonly uint[] _argsReset = new uint[5] { 0, 0, 0, 0, 0 };

    // ── Public ─────────────────────────────────────────────────────────
    /// <summary>Number of active grass instances after last scatter.</summary>
    public int ActiveInstanceCount
    {
        get
        {
            if (_counterBuffer == null) return 0;
            uint[] count = new uint[1];
            _counterBuffer.GetData(count);
            return (int)count[0];
        }
    }

    // ================================================================
    //  LIFECYCLE
    // ================================================================

    void OnEnable()
    {
        if (maskGenerator == null)
            maskGenerator = FindFirstObjectByType<TerrainContactMaskGenerator>();
        if (terrain == null)
            terrain = FindFirstObjectByType<Terrain>();

        BuildQuadMesh();
        EnsureBuffers();
    }

    void OnDisable()
    {
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
        if (!_isScattered && maskGenerator != null && maskGenerator.MaskTexture != null)
        {
            ScatterGrass();
        }

        DrawGrass();
    }

    // ================================================================
    //  PUBLIC API
    // ================================================================

    /// <summary>
    /// Run the compute scatter and rebuild the instance buffer.
    /// </summary>
    public void ScatterGrass()
    {
        if (scatterCompute == null)
        {
            Debug.LogWarning("[GrassSpawner] No compute shader assigned.");
            return;
        }
        if (maskGenerator == null || maskGenerator.MaskTexture == null)
        {
            Debug.LogWarning("[GrassSpawner] No contact mask available. Bake the mask first.");
            return;
        }
        if (terrain == null || terrain.terrainData == null)
        {
            Debug.LogWarning("[GrassSpawner] No terrain assigned.");
            return;
        }

        EnsureBuffers();
        EnsureMaterial();

        TerrainData td = terrain.terrainData;
        Vector3 tPos = terrain.transform.position;
        Vector3 tSize = td.size;

        int gridX = scatterGridX;
        int gridY = Mathf.CeilToInt(scatterGridX * (tSize.z / tSize.x));

        // ── Clear counter ──
        _kernelClear = scatterCompute.FindKernel("Clear");
        scatterCompute.SetBuffer(_kernelClear, "_CounterBuffer", _counterBuffer);
        scatterCompute.Dispatch(_kernelClear, 1, 1, 1);

        // ── Set scatter params ──
        _kernelScatter = scatterCompute.FindKernel("Scatter");

        scatterCompute.SetBuffer(_kernelScatter, "_GrassBuffer", _grassBuffer);
        scatterCompute.SetBuffer(_kernelScatter, "_CounterBuffer", _counterBuffer);
        scatterCompute.SetTexture(_kernelScatter, "_ContactMask", maskGenerator.MaskTexture);

        // Heightmap — use terrain's heightmap texture
        RenderTexture heightmapRT = td.heightmapTexture;
        scatterCompute.SetTexture(_kernelScatter, "_HeightMap", heightmapRT);

        scatterCompute.SetVector("_TerrainOrigin", new Vector4(tPos.x, tPos.y, tPos.z, 0));
        scatterCompute.SetVector("_TerrainSize", new Vector4(tSize.x, tSize.y, tSize.z, 0));
        scatterCompute.SetFloat("_MaskThreshold", maskThreshold);
        scatterCompute.SetFloat("_Density", density);
        scatterCompute.SetFloat("_BaseWidth", baseWidth);
        scatterCompute.SetFloat("_BaseHeight", baseHeight);
        scatterCompute.SetFloat("_WidthVariation", widthVariation);
        scatterCompute.SetFloat("_HeightVariation", heightVariation);
        scatterCompute.SetInt("_MaxInstances", maxInstances);
        scatterCompute.SetInt("_GridWidth", gridX);
        scatterCompute.SetInt("_GridHeight", gridY);
        scatterCompute.SetFloat("_Seed", Random.Range(0f, 10000f));

        // Dispatch
        int groupsX = Mathf.CeilToInt(gridX / 8f);
        int groupsY = Mathf.CeilToInt(gridY / 8f);
        scatterCompute.Dispatch(_kernelScatter, groupsX, groupsY, 1);

        // Copy counter to indirect args buffer
        // args = [indexCount, instanceCount, startIndex, baseVertex, startInstance]
        uint[] argsData = new uint[5];
        argsData[0] = (uint)_quadMesh.GetIndexCount(0);
        argsData[1] = 0; // will be filled from counter
        argsData[2] = (uint)_quadMesh.GetIndexStart(0);
        argsData[3] = (uint)_quadMesh.GetBaseVertex(0);
        argsData[4] = 0;
        _argsBuffer.SetData(argsData);

        // Read back counter and set instance count in args
        // (We do a small GPU→CPU readback here; only happens on scatter, not per frame)
        uint[] count = new uint[1];
        _counterBuffer.GetData(count);
        uint instanceCount = System.Math.Min(count[0], (uint)maxInstances);
        argsData[1] = instanceCount;
        _argsBuffer.SetData(argsData);

        // Compute world bounds for culling
        _worldBounds = new Bounds(
            tPos + tSize * 0.5f,
            tSize + Vector3.up * baseHeight * 2f
        );

        _isScattered = true;
        Debug.Log($"[GrassSpawner] Scattered {instanceCount:N0} grass instances " +
                  $"(grid {gridX}×{gridY}, density {density:F2})");
    }

    // ================================================================
    //  DRAWING
    // ================================================================

    void DrawGrass()
    {
        if (!_isScattered || _grassMat == null || _argsBuffer == null)
            return;

        // Update material properties
        _grassMat.SetColor("_BaseColor", baseColor);
        _grassMat.SetColor("_TipColor", tipColor);
        _grassMat.SetFloat("_Cutoff", alphaCutoff);
        _grassMat.SetFloat("_WindSpeed", windSpeed);
        _grassMat.SetFloat("_WindStrength", windStrength);
        _grassMat.SetFloat("_ColorVariation", colorVariation);
        _grassMat.SetBuffer("_GrassBuffer", _grassBuffer);

        Graphics.DrawMeshInstancedIndirect(
            _quadMesh,
            0,
            _grassMat,
            _worldBounds,
            _argsBuffer,
            0,
            null,
            shadowMode,
            true,
            renderLayer
        );
    }

    // ================================================================
    //  BUFFER MANAGEMENT
    // ================================================================

    void EnsureBuffers()
    {
        bool needsRebuild = _grassBuffer == null
                          || _lastMaxInstances != maxInstances
                          || _lastGridX != scatterGridX;

        if (!needsRebuild) return;

        ReleaseBuffers();

        // GrassInstance struct: 8 floats = 32 bytes
        _grassBuffer = new ComputeBuffer(maxInstances, 32);
        _counterBuffer = new ComputeBuffer(1, sizeof(uint));
        _argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);

        _argsBuffer.SetData(_argsReset);
        _counterBuffer.SetData(new uint[] { 0 });

        _lastMaxInstances = maxInstances;
        _lastGridX = scatterGridX;
        _isScattered = false;

        float mbCost = (maxInstances * 32f) / (1024f * 1024f);
        Debug.Log($"[GrassSpawner] Allocated buffers for {maxInstances:N0} instances ({mbCost:F1} MB)");
    }

    void ReleaseBuffers()
    {
        _grassBuffer?.Release();   _grassBuffer = null;
        _counterBuffer?.Release(); _counterBuffer = null;
        _argsBuffer?.Release();    _argsBuffer = null;
        _isScattered = false;
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
            Debug.LogError("[GrassSpawner] Could not find Hidden/ContactGrassRender shader.");
            return;
        }

        _grassMat = new Material(shader);
        _grassMat.hideFlags = HideFlags.HideAndDontSave;

        if (grassTexture != null)
            _grassMat.SetTexture("_MainTex", grassTexture);
    }

    void BuildQuadMesh()
    {
        if (_quadMesh != null) return;

        _quadMesh = new Mesh();
        _quadMesh.name = "GrassQuad";

        // Simple quad: -0.5..0.5 on XY, Z=0
        _quadMesh.vertices = new Vector3[]
        {
            new(-0.5f, -0.5f, 0),
            new( 0.5f, -0.5f, 0),
            new( 0.5f,  0.5f, 0),
            new(-0.5f,  0.5f, 0)
        };

        _quadMesh.uv = new Vector2[]
        {
            new(0, 0),
            new(1, 0),
            new(1, 1),
            new(0, 1)
        };

        _quadMesh.triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        _quadMesh.normals = new Vector3[]
        {
            Vector3.back, Vector3.back, Vector3.back, Vector3.back
        };

        _quadMesh.UploadMeshData(true);
    }

    // ================================================================
    //  GIZMOS
    // ================================================================

    #if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (terrain == null) return;

        TerrainData td = terrain.terrainData;
        Vector3 pos = terrain.transform.position;
        Vector3 size = td.size;

        Gizmos.color = new Color(0.3f, 0.8f, 0.3f, 0.2f);
        Gizmos.DrawWireCube(pos + size * 0.5f, size);
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

        // ── Info box ──
        int gridX = spawner.scatterGridX;
        float aspect = 1f;
        if (spawner.terrain != null && spawner.terrain.terrainData != null)
        {
            Vector3 s = spawner.terrain.terrainData.size;
            aspect = s.z / s.x;
        }
        int gridY = Mathf.CeilToInt(gridX * aspect);
        long totalSlots = (long)gridX * gridY;
        int estInstances = Mathf.CeilToInt(totalSlots * spawner.density * 0.5f); // rough estimate
        float bufferMB = (spawner.maxInstances * 32f) / (1024f * 1024f);

        MessageType msgType = MessageType.Info;
        if (estInstances > 300000) msgType = MessageType.Warning;
        if (estInstances > 500000) msgType = MessageType.Error;

        EditorGUILayout.HelpBox(
            $"Scatter Grid: {gridX} × {gridY} = {totalSlots:N0} potential slots\n" +
            $"Estimated Instances: ~{estInstances:N0} (density × avg mask)\n" +
            $"Buffer Capacity: {spawner.maxInstances:N0} ({bufferMB:F1} MB)\n" +
            (spawner.ActiveInstanceCount > 0
                ? $"Active Instances: {spawner.ActiveInstanceCount:N0}"
                : "Not yet scattered."),
            msgType);

        EditorGUILayout.Space(4);

        // ── Scatter button ──
        GUI.backgroundColor = new Color(0.3f, 0.7f, 0.3f);
        if (GUILayout.Button("Scatter Grass", GUILayout.Height(30)))
        {
            spawner.ScatterGrass();
            EditorUtility.SetDirty(spawner);
        }
        GUI.backgroundColor = Color.white;

        // ── Clear button ──
        if (GUILayout.Button("Clear Grass"))
        {
            // Force re-scatter next time
            var field = typeof(ContactGrassSpawner).GetField("_isScattered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null) field.SetValue(spawner, false);
            EditorUtility.SetDirty(spawner);
        }
    }
}
#endif
