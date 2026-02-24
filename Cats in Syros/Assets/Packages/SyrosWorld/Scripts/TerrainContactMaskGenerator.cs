// =============================================================================
// TerrainContactMaskGenerator.cs
//
// Generates a world-space contact mask for the terrain shader's Layer 5 (dirt).
// White pixels = object footprints, black = open terrain.
//
// How it works:
//   1. An orthographic camera looks straight down at the terrain.
//   2. It renders objects on specified layers into an R8 RenderTexture.
//   3. A Gaussian blur pass softens the mask edges.
//   4. The mask RT + world-space mapping are pushed to the terrain material.
//
// Resolution guide (for a 2000×3300 terrain):
//   0.5 px/m  →  1000×1650  ≈  1.6 MB  — fast, soft, good for large patches
//   1   px/m  →  2000×3300  ≈  6.3 MB  — balanced default
//   2   px/m  →  4000×6600  ≈ 25.2 MB  — sharp, for small buildings
//   4   px/m  →  8000×13200 ≈ 101 MB   — very sharp, HIGH MEMORY
//   8   px/m  → 16000×26400 ≈ 403 MB   — EXTREME, will likely crash
//
// Usage:
//   - Add this component to a GameObject in the scene.
//   - Set the Terrain reference (or it auto-finds one).
//   - Set which layers contain objects that should produce contact dirt.
//   - Hit Play or press "Bake Mask" in the Inspector to generate.
//   - The shader picks it up automatically via _ContactMask.
// =============================================================================

using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class TerrainContactMaskGenerator : MonoBehaviour
{
    // ── Configuration ──────────────────────────────────────────────────
    [Header("Terrain")]
    [Tooltip("The terrain to generate the contact mask for. Auto-detected if null.")]
    public Terrain terrain;

    [Header("Mask Resolution")]
    [Tooltip("Resolution mode: choose a preset or Custom for full control.")]
    public ResolutionPreset resolutionPreset = ResolutionPreset.Balanced_1pxm;

    [Tooltip("Custom pixels per world unit (only used when preset = Custom).\n" +
             "1 = 1 pixel per metre. 4 = 4 pixels per metre (sharp but heavy).")]
    [Range(0.1f, 10f)]
    public float customPixelsPerUnit = 2f;

    [Tooltip("Hard maximum resolution on either axis. Prevents GPU memory disasters.\n" +
             "8192 = safe for most GPUs. 16384 = risky on older hardware.")]
    public int maxResolution = 8192;

    [Header("Object Detection")]
    [Tooltip("Which layers contain objects that should produce contact dirt.\n" +
             "IMPORTANT: Exclude the Terrain layer or you'll see terrain in the mask.")]
    public LayerMask objectLayers = ~0;

    [Tooltip("Ignore objects smaller than this bound size (metres). Filters out tiny debris.")]
    public float minObjectSize = 0.5f;

    [Header("Blur")]
    [Tooltip("Number of Gaussian blur passes on the mask. More = softer / wider dirt spread.\n" +
             "0 = razor sharp edges, 3 = natural, 6+ = very wide spread.")]
    [Range(0, 12)]
    public int blurPasses = 3;

    [Tooltip("Blur spread per pass in pixels. Increase for wider contact area.")]
    [Range(0.5f, 6f)]
    public float blurSpread = 1.5f;

    [Header("Update")]
    [Tooltip("Re-generate every N seconds (0 = manual only). Use 0 for baked, >0 for moving objects.")]
    public float updateInterval = 0f;

    [Header("Debug")]
    [Tooltip("Show the mask RT preview and memory info in the inspector.")]
    public bool showDebug = true;

    // ── Resolution Presets ─────────────────────────────────────────────
    public enum ResolutionPreset
    {
        Low_05pxm,        // 0.5 px/m — fast, soft
        Balanced_1pxm,    // 1 px/m   — good default
        High_2pxm,        // 2 px/m   — sharp, good for small objects
        VeryHigh_4pxm,    // 4 px/m   — very sharp, costs memory
        Custom             // use customPixelsPerUnit
    }

    // ── Internal State ────────────────────────────────────────────────
    RenderTexture _maskRT;
    RenderTexture _blurTempRT;
    Material      _blurMat;
    Camera        _maskCam;
    GameObject    _maskCamGO;
    float         _lastUpdateTime;

    // ── Public Access ─────────────────────────────────────────────────
    /// <summary>The generated contact mask RenderTexture.</summary>
    public RenderTexture MaskTexture => _maskRT;

    /// <summary>Effective pixels per unit being used.</summary>
    public float EffectivePixelsPerUnit => GetPixelsPerUnit();

    // ================================================================
    //  RESOLUTION HELPERS
    // ================================================================

    float GetPixelsPerUnit()
    {
        return resolutionPreset switch
        {
            ResolutionPreset.Low_05pxm     => 0.5f,
            ResolutionPreset.Balanced_1pxm => 1f,
            ResolutionPreset.High_2pxm     => 2f,
            ResolutionPreset.VeryHigh_4pxm => 4f,
            ResolutionPreset.Custom        => customPixelsPerUnit,
            _                               => 1f
        };
    }

    /// <summary>
    /// Calculates the mask resolution and estimated VRAM cost.
    /// </summary>
    public void GetMaskInfo(out int width, out int height, out float megabytes, out float metersPerPixel)
    {
        float ppu = GetPixelsPerUnit();
        Vector3 size = Vector3.zero;

        if (terrain != null && terrain.terrainData != null)
            size = terrain.terrainData.size;
        else
        {
            // Fallback estimate
            size = new Vector3(2000, 450, 3300);
        }

        width  = Mathf.Clamp(Mathf.CeilToInt(size.x * ppu), 64, maxResolution);
        height = Mathf.Clamp(Mathf.CeilToInt(size.z * ppu), 64, maxResolution);

        // R8 = 1 byte per pixel × 2 RTs (mask + blur temp)
        megabytes = (width * height * 2f) / (1024f * 1024f);
        metersPerPixel = 1f / Mathf.Max(ppu, 0.01f);
    }

    // ================================================================
    //  LIFECYCLE
    // ================================================================

    bool _needsInitialBake;

    void OnEnable()
    {
        if (terrain == null)
            terrain = FindFirstObjectByType<Terrain>();

        EnsureResources();
        // Don't bake immediately — URP may not be initialized yet during domain reload.
        // Defer to the first Update() frame instead.
        _needsInitialBake = true;
    }

    void OnDisable()
    {
        CleanupResources();
    }

    void Update()
    {
        // Deferred initial bake — safe because URP is fully initialized by Update time
        if (_needsInitialBake)
        {
            _needsInitialBake = false;
            BakeMask();
        }

        if (updateInterval > 0f && Time.time - _lastUpdateTime >= updateInterval)
        {
            BakeMask();
        }
    }

    // ================================================================
    //  PUBLIC API
    // ================================================================

    /// <summary>
    /// (Re)generate the contact mask and push it to the terrain material.
    /// </summary>
    public void BakeMask()
    {
        if (terrain == null)
        {
            Debug.LogWarning("[ContactMask] No terrain assigned.");
            return;
        }

        EnsureResources();
        RenderMask();
        ApplyBlur();
        PushToMaterial();

        _lastUpdateTime = Time.time;
    }

    // ================================================================
    //  RESOURCE MANAGEMENT
    // ================================================================

    void EnsureResources()
    {
        if (terrain == null) return;

        GetMaskInfo(out int w, out int h, out float mb, out float mpp);

        // Warn at high memory thresholds
        if (mb > 100f)
            Debug.LogWarning($"[ContactMask] HIGH MEMORY: {w}×{h} mask will use ~{mb:F0} MB VRAM. " +
                             $"Consider lowering resolution or maxResolution.");
        else if (mb > 50f)
            Debug.LogWarning($"[ContactMask] Moderate memory: {w}×{h} mask = ~{mb:F0} MB VRAM.");

        // Create or recreate if resolution changed
        if (_maskRT == null || _maskRT.width != w || _maskRT.height != h)
        {
            if (_maskRT != null) ReleaseRT(_maskRT);
            if (_blurTempRT != null) ReleaseRT(_blurTempRT);

            _maskRT = new RenderTexture(w, h, 16, RenderTextureFormat.R8);
            _maskRT.name = "TerrainContactMask";
            _maskRT.wrapMode = TextureWrapMode.Clamp;
            _maskRT.filterMode = FilterMode.Bilinear;
            _maskRT.Create();

            _blurTempRT = new RenderTexture(w, h, 0, RenderTextureFormat.R8);
            _blurTempRT.name = "TerrainContactMask_BlurTemp";
            _blurTempRT.wrapMode = TextureWrapMode.Clamp;
            _blurTempRT.filterMode = FilterMode.Bilinear;
            _blurTempRT.Create();

            Debug.Log($"[ContactMask] Created {w}×{h} mask ({mb:F1} MB VRAM, " +
                      $"{mpp:F2}m per pixel, {GetPixelsPerUnit():F1} px/m)");
        }

        // Blur material
        if (_blurMat == null)
        {
            Shader blurShader = Shader.Find("Hidden/ContactMaskBlur");
            if (blurShader == null)
                Debug.LogWarning("[ContactMask] Blur shader not found. Mask will have hard edges.");
            else
            {
                _blurMat = new Material(blurShader);
                _blurMat.hideFlags = HideFlags.HideAndDontSave;
            }
        }
    }

    void CleanupResources()
    {
        if (_maskCamGO != null)
        {
            if (Application.isPlaying) Destroy(_maskCamGO);
            else DestroyImmediate(_maskCamGO);
        }
        _maskCam = null;
        _maskCamGO = null;

        ReleaseRT(_maskRT);     _maskRT = null;
        ReleaseRT(_blurTempRT); _blurTempRT = null;

        if (_blurMat != null)
        {
            if (Application.isPlaying) Destroy(_blurMat);
            else DestroyImmediate(_blurMat);
            _blurMat = null;
        }
    }

    static void ReleaseRT(RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        if (Application.isPlaying) Destroy(rt);
        else DestroyImmediate(rt);
    }

    // ================================================================
    //  MASK RENDERING
    // ================================================================

    void RenderMask()
    {
        TerrainData td = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;
        Vector3 size = td.size;
        Vector3 center = terrainPos + size * 0.5f;

        // Create temporary orthographic camera
        if (_maskCamGO == null)
        {
            _maskCamGO = new GameObject("_ContactMaskCam");
            _maskCamGO.hideFlags = HideFlags.HideAndDontSave;
            _maskCam = _maskCamGO.AddComponent<Camera>();
            _maskCam.enabled = false; // we render manually
        }

        // Position camera above terrain center, looking straight down
        _maskCamGO.transform.position = new Vector3(center.x, terrainPos.y + size.y + 100f, center.z);
        _maskCamGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        _maskCam.orthographic = true;
        _maskCam.orthographicSize = size.z * 0.5f;
        _maskCam.aspect = size.x / size.z;
        _maskCam.nearClipPlane = 1f;
        _maskCam.farClipPlane = size.y + 200f;
        _maskCam.clearFlags = CameraClearFlags.SolidColor;
        _maskCam.backgroundColor = Color.black;
        _maskCam.cullingMask = objectLayers;
        _maskCam.targetTexture = _maskRT;

        // Replacement shader: render all geometry as solid white
        Shader whiteShader = Shader.Find("Hidden/ContactMaskWhite");
        if (whiteShader != null)
            _maskCam.SetReplacementShader(whiteShader, "");

        _maskCam.Render();
        _maskCam.targetTexture = null;
    }

    // ================================================================
    //  GAUSSIAN BLUR
    // ================================================================

    void ApplyBlur()
    {
        if (_blurMat == null || blurPasses <= 0) return;

        for (int i = 0; i < blurPasses; i++)
        {
            _blurMat.SetVector("_BlurDir", new Vector4(blurSpread / _maskRT.width, 0, 0, 0));
            Graphics.Blit(_maskRT, _blurTempRT, _blurMat);

            _blurMat.SetVector("_BlurDir", new Vector4(0, blurSpread / _maskRT.height, 0, 0));
            Graphics.Blit(_blurTempRT, _maskRT, _blurMat);
        }
    }

    // ================================================================
    //  PUSH TO MATERIAL
    // ================================================================

    void PushToMaterial()
    {
        if (terrain == null || _maskRT == null) return;

        Material mat = terrain.materialTemplate;
        if (mat == null)
        {
            Debug.LogWarning("[ContactMask] Terrain has no material assigned.");
            return;
        }

        TerrainData td = terrain.terrainData;
        Vector3 terrainPos = terrain.transform.position;
        Vector3 size = td.size;

        mat.SetTexture("_ContactMask", _maskRT);
        mat.SetVector("_ContactOrigin", new Vector4(terrainPos.x, terrainPos.z, 0, 0));
        mat.SetVector("_ContactSize", new Vector4(size.x, size.z, 0, 0));
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
        Vector3 center = pos + size * 0.5f;

        Gizmos.color = new Color(1f, 0.5f, 0f, 0.3f);
        Gizmos.DrawWireCube(center, size);
    }
    #endif
}

// =============================================================================
//  CUSTOM EDITOR — resolution info, warnings, bake button, preview
// =============================================================================
#if UNITY_EDITOR
[CustomEditor(typeof(TerrainContactMaskGenerator))]
public class TerrainContactMaskGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var gen = (TerrainContactMaskGenerator)target;

        // ── Resolution Info Box ──────────────────────────────────────
        EditorGUILayout.Space(8);
        gen.GetMaskInfo(out int w, out int h, out float mb, out float mpp);

        // Color-code the info box based on cost
        MessageType msgType;
        string sizeLabel;
        if (mb > 100f)
        {
            msgType = MessageType.Error;
            sizeLabel = "EXTREME";
        }
        else if (mb > 50f)
        {
            msgType = MessageType.Warning;
            sizeLabel = "HIGH";
        }
        else if (mb > 20f)
        {
            msgType = MessageType.Warning;
            sizeLabel = "MODERATE";
        }
        else
        {
            msgType = MessageType.Info;
            sizeLabel = "OK";
        }

        string buildingNote = mpp > 1f
            ? $"  Buildings smaller than ~{mpp:F1}m may not register in the mask."
            : mpp > 0.5f
                ? "  Sub-metre objects may appear soft."
                : "  High precision — small objects will be captured.";

        EditorGUILayout.HelpBox(
            $"Mask Resolution: {w} × {h}  ({sizeLabel})\n" +
            $"VRAM Cost: ~{mb:F1} MB  (2× R8 RTs)\n" +
            $"Precision: {mpp:F2}m per pixel ({gen.EffectivePixelsPerUnit:F1} px/m)\n" +
            buildingNote,
            msgType);

        // ── Bake Button ──────────────────────────────────────────────
        EditorGUILayout.Space(4);

        GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
        if (GUILayout.Button("Bake Contact Mask", GUILayout.Height(32)))
        {
            gen.BakeMask();
            EditorUtility.SetDirty(gen);
        }
        GUI.backgroundColor = Color.white;

        // ── Preview ──────────────────────────────────────────────────
        if (gen.showDebug && gen.MaskTexture != null)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Contact Mask Preview", EditorStyles.boldLabel);

            float aspect = (float)gen.MaskTexture.width / gen.MaskTexture.height;
            Rect r = GUILayoutUtility.GetAspectRect(aspect);
            EditorGUI.DrawPreviewTexture(r, gen.MaskTexture);

            EditorGUILayout.LabelField(
                $"  {gen.MaskTexture.width}×{gen.MaskTexture.height}  |  " +
                $"Format: {gen.MaskTexture.format}",
                EditorStyles.miniLabel);
        }
    }
}
#endif
