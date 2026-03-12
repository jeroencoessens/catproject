// =============================================================================
// FoliageCommon.hlsl
//
// Shared types and vertex transform for EdgeFoliageRender passes.
// Include AFTER URP Core.hlsl so TransformWorldToHClip / _Time are available.
// =============================================================================

#ifndef FOLIAGE_COMMON_INCLUDED
#define FOLIAGE_COMMON_INCLUDED

// ── Instance struct (must match compute shader & C#) ───────────────────
struct FoliageInstance
{
    float3 position;   // world-space base
    float  rotation;   // Y-axis radians
    float2 scale;      // width, height
    float  colorVar;   // 0-1 random seed
    float  fade;       // distance fade (written by cull)
};

StructuredBuffer<FoliageInstance> _VisibleBuffer;

// ── Shared vertex attributes ───────────────────────────────────────────
struct FoliageAttributes
{
    float4 posOS : POSITION;
    float2 uv    : TEXCOORD0;
};

// ── Compute world position for a foliage instance ──────────────────────
// Returns final world-space position with scale, rotation, wind and fade.
float3 ComputeFoliageWorldPos(
    float3 posOS,
    FoliageInstance gi,
    float  uniformScale,
    float  windSpeed,
    float  windStrength)
{
    float w = gi.scale.x * uniformScale;
    float h = gi.scale.y * uniformScale;

    float3 localPos = posOS;
    localPos.x *= w;
    localPos.z *= w;
    localPos.y *= h;

    // Anchor at base
    localPos.y += h * 0.5;

    // Rotate around Y
    float s, c;
    sincos(gi.rotation, s, c);
    float3 rotated;
    rotated.x = localPos.x * c - localPos.z * s;
    rotated.z = localPos.x * s + localPos.z * c;
    rotated.y = localPos.y;

    float3 worldPos = gi.position + rotated;

    // Wind displacement (stronger at top)
    float windPhase  = _Time.y * windSpeed + gi.position.x * 0.3 + gi.position.z * 0.2;
    float windAmount = sin(windPhase) * windStrength;
    float heightFactor = saturate(posOS.y + 0.5);
    worldPos.x += windAmount * heightFactor;
    worldPos.z += windAmount * 0.5 * heightFactor;

    // Distance fade — shrink into ground
    worldPos.y = gi.position.y + (worldPos.y - gi.position.y) * gi.fade;

    return worldPos;
}

#endif // FOLIAGE_COMMON_INCLUDED
