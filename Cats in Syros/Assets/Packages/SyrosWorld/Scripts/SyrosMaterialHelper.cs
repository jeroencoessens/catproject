using UnityEngine;
using UnityEngine.Rendering;

namespace SyrosWorld
{
    /// <summary>
    /// Pipeline-agnostic material factory.
    ///
    /// <c>Shader.Find()</c> is unreliable at edit-time under URP because
    /// shaders may not yet be loaded.  This helper therefore probes
    /// <see cref="GraphicsSettings.currentRenderPipeline"/> first and
    /// falls back through increasingly generic shader names.
    ///
    /// <b>Fallback chain (lit objects):</b>
    /// <list type="number">
    ///   <item>Active pipeline's default material shader</item>
    ///   <item>"Universal Render Pipeline/Lit"</item>
    ///   <item>"Universal Render Pipeline/Simple Lit"</item>
    ///   <item>"Standard" (Built-in)</item>
    ///   <item>"Diffuse" (legacy)</item>
    /// </list>
    /// </summary>
    public static class SyrosMaterialHelper
    {
        /// <summary>Cached result of <see cref="FindLitShader"/>.</summary>
        static Shader _cachedLitShader;

        // =============================================================
        //  SHADER LOOKUP
        // =============================================================

        /// <summary>
        /// Resolve the best available Lit shader for the current render
        /// pipeline.  The result is cached until <see cref="ClearCache"/>
        /// is called.
        /// </summary>
        public static Shader FindLitShader()
        {
            if (_cachedLitShader != null) return _cachedLitShader;

            // 1. Ask the active pipeline for its default material's shader
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                var defaultMat = GraphicsSettings.currentRenderPipeline.defaultMaterial;
                if (defaultMat != null && defaultMat.shader != null)
                {
                    _cachedLitShader = defaultMat.shader;
                    return _cachedLitShader;
                }
            }

            // 2. Explicit URP shader names
            _cachedLitShader = Shader.Find("Universal Render Pipeline/Lit");
            if (_cachedLitShader != null) return _cachedLitShader;

            _cachedLitShader = Shader.Find("Universal Render Pipeline/Simple Lit");
            if (_cachedLitShader != null) return _cachedLitShader;

            // 3. Built-in fallbacks
            _cachedLitShader = Shader.Find("Standard");
            if (_cachedLitShader != null) return _cachedLitShader;

            _cachedLitShader = Shader.Find("Diffuse");
            return _cachedLitShader;
        }

        // =============================================================
        //  MATERIAL CREATION
        // =============================================================

        /// <summary>
        /// Create a solid-colour material using the best available shader.
        /// Sets both <c>_BaseColor</c> (URP) and <c>_Color</c> (Built-in)
        /// so the tint works regardless of pipeline.
        /// </summary>
        /// <param name="color">Desired albedo / base colour.</param>
        /// <returns>A new <see cref="Material"/> instance.</returns>
        public static Material CreateColorMaterial(Color color)
        {
            var shader = FindLitShader();
            if (shader == null)
            {
                Debug.LogWarning("[SyrosMaterialHelper] No valid shader found! Material will be pink.");
                return new Material(Shader.Find("Hidden/InternalErrorShader"));
            }

            var mat = new Material(shader);

            // URP Lit uses _BaseColor; Built-in Standard uses _Color
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);

            // White base-map so the colour tint is visible
            if (mat.HasProperty("_BaseMap"))
                mat.SetTexture("_BaseMap", Texture2D.whiteTexture);

            return mat;
        }

        /// <summary>
        /// Get a pipeline-appropriate terrain material, or <c>null</c> if
        /// no suitable shader is available.
        /// </summary>
        public static Material GetTerrainMaterial()
        {
            // 1. Ask the active pipeline
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                var terrainMat = GraphicsSettings.currentRenderPipeline.defaultTerrainMaterial;
                if (terrainMat != null) return terrainMat;
            }

            // 2. URP terrain shader
            var shader = Shader.Find("Universal Render Pipeline/Terrain/Lit");
            if (shader != null) return new Material(shader);

            // 3. Built-in terrain shader
            shader = Shader.Find("Nature/Terrain/Standard");
            if (shader != null) return new Material(shader);

            return null;
        }

        // =============================================================
        //  CACHE MANAGEMENT
        // =============================================================

        /// <summary>
        /// Clear the cached shader so the next <see cref="FindLitShader"/>
        /// call re-probes.  Useful after a pipeline change.
        /// </summary>
        public static void ClearCache()
        {
            _cachedLitShader = null;
        }
    }
}
