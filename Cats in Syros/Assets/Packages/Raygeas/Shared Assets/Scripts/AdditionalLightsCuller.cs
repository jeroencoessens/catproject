using UnityEngine;
using System.Collections.Generic;

namespace Raygeas
{
    public class AdditionalLightsCuller : MonoBehaviour
    {
        public Camera playerCamera;
        public float fullShadowsDistance = 15f;
        public float noShadowsDistance = 25f;
        public float fullLightDistance = 35f;
        public float offLightDistance = 45f;
        public float disableIntensityEpsilon = 0.01f;

        private class LightData
        {
            public LightShadows originalShadowMode;
            public float originalShadowStrength;
            public float originalIntensity;
            public bool hadShadows;
        }

        private readonly Dictionary<Light, LightData> _data = new Dictionary<Light, LightData>();

        void Awake()
        {
            if (playerCamera == null) playerCamera = Camera.main;
            CacheExistingLights();
        }

        void Update()   
        {
            if (!playerCamera) return;

            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                TryCacheLight(l);
            }

            Vector3 camPos = playerCamera.transform.position;

            foreach (var kvp in _data)
            {
                var light = kvp.Key;
                if (!light) continue;
                if (light.type == LightType.Directional) continue;

                var d = kvp.Value;
                float dist = Vector3.Distance(light.transform.position, camPos);

                if (dist <= fullShadowsDistance)
                {
                    EnsureEnabled(light);
                    if (d.hadShadows)
                    {
                        light.shadows = d.originalShadowMode;
                        light.shadowStrength = d.originalShadowStrength;
                    }
                    else
                    {
                        light.shadows = LightShadows.None;
                        light.shadowStrength = 0f;
                    }
                    light.intensity = d.originalIntensity;
                    continue;
                }

                if (dist <= noShadowsDistance)
                {
                    EnsureEnabled(light);
                    if (d.hadShadows)
                    {
                        float t = Mathf.InverseLerp(fullShadowsDistance, noShadowsDistance, dist);
                        light.shadows = d.originalShadowMode;
                        light.shadowStrength = Mathf.Lerp(d.originalShadowStrength, 0f, t);
                    }
                    else
                    {
                        light.shadows = LightShadows.None;
                        light.shadowStrength = 0f;
                    }
                    light.intensity = d.originalIntensity;
                    continue;
                }

                if (dist <= fullLightDistance)
                {
                    EnsureEnabled(light);
                    light.shadows = LightShadows.None;
                    light.shadowStrength = 0f;
                    light.intensity = d.originalIntensity;
                    continue;
                }

                if (dist <= offLightDistance)
                {
                    EnsureEnabled(light);
                    light.shadows = LightShadows.None;
                    light.shadowStrength = 0f;

                    float t = Mathf.InverseLerp(fullLightDistance, offLightDistance, dist);
                    float targetIntensity = Mathf.Lerp(d.originalIntensity, 0f, t);
                    light.intensity = targetIntensity;

                    if (light.intensity <= disableIntensityEpsilon)
                        light.enabled = false;

                    continue;
                }

                light.shadows = LightShadows.None;
                light.shadowStrength = 0f;
                light.intensity = 0f;
                light.enabled = false;
            }
        }

        void OnValidate()
        {
            fullShadowsDistance = Mathf.Max(0f, fullShadowsDistance);
            noShadowsDistance = Mathf.Max(fullShadowsDistance, noShadowsDistance);
            fullLightDistance = Mathf.Max(noShadowsDistance, fullLightDistance);
            offLightDistance = Mathf.Max(fullLightDistance, offLightDistance);
            disableIntensityEpsilon = Mathf.Clamp(disableIntensityEpsilon, 0f, 0.1f);
        }

        private void CacheExistingLights()
        {
            _data.Clear();
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                TryCacheLight(l);
        }

        private void TryCacheLight(Light l)
        {
            if (!l) return;
            if (l.type == LightType.Directional) return;
            if (_data.ContainsKey(l)) return;

            _data[l] = new LightData
            {
                originalShadowMode = l.shadows,
                originalShadowStrength = l.shadowStrength,
                originalIntensity = l.intensity,
                hadShadows = l.shadows != LightShadows.None
            };
        }

        private static void EnsureEnabled(Light l)
        {
            if (!l.enabled) l.enabled = true;
        }
    }
}