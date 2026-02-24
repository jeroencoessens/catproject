#ifndef GREEK_ISLAND_TERRAIN_INPUT_INCLUDED
#define GREEK_ISLAND_TERRAIN_INPUT_INCLUDED

// =============================================================================
// Shared terrain GPU-instancing and holes support for Custom/GreekIslandTerrain
//
// Modeled after URP's TerrainLitInput.hlsl to ensure correct data binding.
// Unity's terrain system binds heightmap scale/reciprocal size through a CBUFFER
// named "_Terrain". The instancing buffer MUST remain unguarded so the shader
// compiler always recognises the instancing layout.
//
// Include AFTER Core.hlsl (needs TEXTURE2D, UnpackHeightmap, etc.).
// =============================================================================

// ---- Terrain constant buffer ------------------------------------------------
// Unity's terrain system populates these through the "_Terrain" CBUFFER.
// They MUST live inside CBUFFER_START(_Terrain) — loose globals won't receive
// the values, causing vertex positions to collapse to zero (= black terrain).
CBUFFER_START(_Terrain)
    float4 _TerrainHeightmapRecipSize;   // (1/w, 1/h, 1/(w-1), 1/(h-1))
    float4 _TerrainHeightmapScale;       // (sizeX/(res-1), sizeY, sizeZ/(res-1), 0)
CBUFFER_END

// ---- Heightmap & normalmap textures (only bound when GPU instancing is on) --
#ifdef UNITY_INSTANCING_ENABLED
    TEXTURE2D(_TerrainHeightmapTexture);
    TEXTURE2D(_TerrainNormalmapTexture);
    SAMPLER(sampler_TerrainNormalmapTexture);
#endif

// ---- Per-instance patch data ------------------------------------------------
// MUST remain unguarded so the shader compiler always sees the instancing layout.
UNITY_INSTANCING_BUFFER_START(Terrain)
    UNITY_DEFINE_INSTANCED_PROP(float4, _TerrainPatchInstanceData)
UNITY_INSTANCING_BUFFER_END(Terrain)

// ---- Terrain holes ----------------------------------------------------------
#ifdef _ALPHATEST_ON
    TEXTURE2D(_TerrainHolesTexture);
    SAMPLER(sampler_TerrainHolesTexture);
#endif

// =============================================================================
// TerrainInstancing
//
// When "Draw Instanced" is ON (URP default), Unity supplies flat 2D patch
// vertices plus instance data. This function reconstructs the full 3D position,
// normal, and UV from the terrain heightmap/normalmap textures.
// When instancing is OFF this is a complete no-op.
// =============================================================================
void TerrainInstancing(inout float4 positionOS, inout float3 normal, inout float2 uv)
{
#ifdef UNITY_INSTANCING_ENABLED
    float2 patchVertex = positionOS.xy;
    float4 instanceData = UNITY_ACCESS_INSTANCED_PROP(Terrain, _TerrainPatchInstanceData);

    float2 sampleCoords = (patchVertex.xy + instanceData.xy) * instanceData.z;
    float height = UnpackHeightmap(_TerrainHeightmapTexture.Load(int3(sampleCoords, 0)));

    positionOS.xz = sampleCoords * _TerrainHeightmapScale.xz;
    positionOS.y = height * _TerrainHeightmapScale.y;

    normal = _TerrainNormalmapTexture.Load(int3(sampleCoords, 0)).rgb * 2 - 1;

    uv = sampleCoords * _TerrainHeightmapRecipSize.zw;
#endif
}

// =============================================================================
// ClipTerrainHoles
// Discards the fragment when the terrain has a painted hole at this pixel.
// =============================================================================
void ClipTerrainHoles(float2 uv)
{
#ifdef _ALPHATEST_ON
    float hole = SAMPLE_TEXTURE2D(_TerrainHolesTexture, sampler_TerrainHolesTexture, uv).r;
    // Match URP's epsilon check for compressed textures (UUM-61913)
    float epsilon = 0.0005;
    clip(hole < epsilon ? -1 : 1);
#endif
}

#endif // GREEK_ISLAND_TERRAIN_INPUT_INCLUDED
