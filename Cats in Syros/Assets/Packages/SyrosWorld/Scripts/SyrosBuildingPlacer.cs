using System.Collections.Generic;
using UnityEngine;

namespace SyrosWorld
{
    /// <summary>
    /// Places building blockout shapes from prefabs based on parsed OSM data.
    ///
    /// Each building is positioned at the centroid of its polygon footprint,
    /// oriented along the longest edge, and scaled to the footprint's
    /// axis-aligned bounding box.  Named buildings (POIs) use a separate
    /// prefab and are tagged with a <see cref="SyrosPOIMarker"/> component.
    ///
    /// <b>Placement modes</b> (configured via <see cref="SyrosWorldConfig.buildingPlacementMode"/>):
    /// <list type="bullet">
    ///   <item><b>Level</b> — flat rotation, bottom face sits on terrain.</item>
    ///   <item><b>SlopeAligned</b> — rotated to match terrain surface normal.</item>
    ///   <item><b>Foundation</b> — level rotation, TOP face at terrain level,
    ///         mesh extends downward (acts as a foundation/basement).</item>
    /// </list>
    ///
    /// <b>Material handling:</b> When <see cref="SyrosWorldConfig.usePrefabMaterials"/>
    /// is ON and a prefab is assigned, the prefab's own materials are preserved.
    /// Otherwise, flat-colour materials are generated from the config colours.
    /// </summary>
    public static class SyrosBuildingPlacer
    {
        // =================================================================
        //  PUBLIC API
        // =================================================================

        /// <summary>
        /// Instantiate building GameObjects for every parsed building.
        /// </summary>
        public static List<GameObject> PlaceBuildings(
            List<SyrosBuilding> buildings,
            SyrosGeoConverter converter,
            SyrosWorldConfig config,
            Terrain terrain,
            GameObject defaultPrefab,
            GameObject poiPrefab,
            Transform parent)
        {
            var created = new List<GameObject>();

            if (defaultPrefab == null)
                Debug.Log("[SyrosBuildingPlacer] No default prefab — falling back to primitive cubes.");

            // Organise output under sub-parents
            Transform buildingsParent = CreateChild(parent, "Buildings");
            Transform poisParent      = CreateChild(parent, "POIs");

            int total = buildings.Count;

            for (int i = 0; i < total; i++)
            {
                var  bld   = buildings[i];
                bool isPOI = bld.IsPOI;

                // ── Convert polygon to world space ──────────────────────
                var worldPoly = new List<Vector3>();
                foreach (var geoPoint in bld.polygon)
                    worldPoly.Add(converter.GeoToWorld(geoPoint, terrain));

                if (worldPoly.Count < 3) continue;

                // ── Footprint AABB & centroid ───────────────────────────
                Vector3 centroid = Vector3.zero;
                Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                foreach (var p in worldPoly)
                {
                    centroid += p;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                centroid /= worldPoly.Count;

                float sizeX = max.x - min.x;
                float sizeZ = max.z - min.z;

                // Skip degenerate / tiny footprints
                if (sizeX < 0.1f || sizeZ < 0.1f) continue;

                // ── Determine prefab, height, colour ────────────────────
                GameObject prefab       = isPOI ? (poiPrefab ?? defaultPrefab) : defaultPrefab;
                float      height       = isPOI ? config.poiBuildingHeight : config.defaultBuildingHeight;
                Color      color        = isPOI ? config.poiBuildingColor  : config.defaultBuildingColor;
                Transform  targetParent = isPOI ? poisParent : buildingsParent;

                // Should we keep the prefab's own materials?
                bool keepPrefabMats = config.usePrefabMaterials && prefab != null;

                // ── Instantiate ─────────────────────────────────────────
                GameObject go;
                if (prefab != null)
                {
                    #if UNITY_EDITOR
                    go = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab, targetParent);
                    #else
                    go = Object.Instantiate(prefab, targetParent);
                    #endif
                }
                else
                {
                    go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.transform.SetParent(targetParent);
                }

                // Name
                go.name = isPOI && !string.IsNullOrEmpty(bld.name)
                    ? $"POI_{bld.name}"
                    : $"Building_{i}";

                // Orientation: align with the longest polygon edge
                float angle = CalculatePrimaryAngle(worldPoly);

                // ── Position & rotation based on placement mode ─────────
                ApplyPlacementMode(go, config, centroid, angle, sizeX, height, sizeZ, terrain);

                // ── Material ────────────────────────────────────────────
                if (!keepPrefabMats)
                    ApplyColor(go, color);
                // else: prefab's materials are already present from instantiation

                // Static batching
                go.isStatic = true;

                // ── POI marker with elevation debug data ────────────────
                if (isPOI)
                {
                    var marker = go.AddComponent<SyrosPOIMarker>();
                    marker.poiName           = bld.name;
                    marker.buildingType      = bld.buildingType;
                    marker.expectedElevation = bld.elevation;
                    marker.actualUnityY      = centroid.y;
                }

                created.Add(go);

                // Progress bar
                #if UNITY_EDITOR
                if (i % 2000 == 0)
                {
                    float pct = (float)i / total;
                    UnityEditor.EditorUtility.DisplayProgressBar(
                        "Placing Buildings",
                        $"{i}/{total} ({pct * 100f:F0}%)", pct);
                }
                #endif
            }

            #if UNITY_EDITOR
            UnityEditor.EditorUtility.ClearProgressBar();
            #endif

            Debug.Log($"[SyrosBuildingPlacer] Placed {created.Count} buildings " +
                      $"(mode: {config.buildingPlacementMode}, " +
                      $"prefab mats: {config.usePrefabMaterials}).");

            return created;
        }

        // =================================================================
        //  PLACEMENT MODES
        // =================================================================

        /// <summary>
        /// Apply position, rotation, and scale to a building based on the
        /// active <see cref="BuildingPlacementMode"/>.
        /// </summary>
        static void ApplyPlacementMode(
            GameObject go,
            SyrosWorldConfig config,
            Vector3 centroid,
            float yawAngle,
            float sizeX, float height, float sizeZ,
            Terrain terrain)
        {
            switch (config.buildingPlacementMode)
            {
                // ─────────────────────────────────────────────────────────
                case BuildingPlacementMode.Level:
                default:
                {
                    // Bottom face sits on terrain + Y offset, mesh extends up.
                    go.transform.position = new Vector3(
                        centroid.x,
                        centroid.y + config.buildingYOffset + height * 0.5f,
                        centroid.z);
                    go.transform.rotation   = Quaternion.Euler(0, yawAngle, 0);
                    go.transform.localScale = new Vector3(sizeX, height, sizeZ);
                    break;
                }

                // ─────────────────────────────────────────────────────────
                case BuildingPlacementMode.SlopeAligned:
                {
                    // Rotate the building to align with terrain surface normal.
                    Vector3 terrainNormal = GetTerrainNormal(terrain, centroid);
                    Quaternion slopeRot = Quaternion.FromToRotation(Vector3.up, terrainNormal);
                    Quaternion yawRot   = Quaternion.Euler(0, yawAngle, 0);

                    go.transform.position = new Vector3(
                        centroid.x,
                        centroid.y + config.buildingYOffset + height * 0.5f,
                        centroid.z);
                    go.transform.rotation   = slopeRot * yawRot;
                    go.transform.localScale = new Vector3(sizeX, height, sizeZ);
                    break;
                }

                // ─────────────────────────────────────────────────────────
                case BuildingPlacementMode.Foundation:
                {
                    // Top face sits at terrain level (+ small offset).
                    // Mesh extends DOWNWARD, acting as a foundation.
                    // Position = terrain Y + topOffset - half height
                    //   so the top face is at (terrain Y + topOffset).
                    float topY = centroid.y + config.foundationTopOffset;
                    go.transform.position = new Vector3(
                        centroid.x,
                        topY - height * 0.5f,
                        centroid.z);
                    go.transform.rotation   = Quaternion.Euler(0, yawAngle, 0);
                    go.transform.localScale = new Vector3(sizeX, height, sizeZ);
                    break;
                }
            }
        }

        /// <summary>
        /// Sample the terrain surface normal at a given world position.
        /// Returns <c>Vector3.up</c> if terrain is null.
        /// </summary>
        static Vector3 GetTerrainNormal(Terrain terrain, Vector3 worldPos)
        {
            if (terrain == null || terrain.terrainData == null)
                return Vector3.up;

            Vector3 terrainPos = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;

            // Normalised position on the terrain [0,1]
            float normX = (worldPos.x - terrainPos.x) / size.x;
            float normZ = (worldPos.z - terrainPos.z) / size.z;

            normX = Mathf.Clamp01(normX);
            normZ = Mathf.Clamp01(normZ);

            return terrain.terrainData.GetInterpolatedNormal(normX, normZ);
        }

        // =================================================================
        //  INTERNAL HELPERS
        // =================================================================

        /// <summary>
        /// Return the angle (degrees, Y-up) of the longest edge in the polygon.
        /// </summary>
        static float CalculatePrimaryAngle(List<Vector3> poly)
        {
            float maxLen = 0f;
            float angle  = 0f;

            for (int i = 0; i < poly.Count - 1; i++)
            {
                Vector3 edge = poly[i + 1] - poly[i];
                float len = new Vector2(edge.x, edge.z).sqrMagnitude;
                if (len > maxLen)
                {
                    maxLen = len;
                    angle  = Mathf.Atan2(edge.x, edge.z) * Mathf.Rad2Deg;
                }
            }

            return angle;
        }

        /// <summary>
        /// Apply a flat-colour material to all renderers on the object.
        /// Used when prefab materials are NOT being preserved.
        /// </summary>
        static void ApplyColor(GameObject go, Color color)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();

            if (renderers.Length > 0)
            {
                var mat = SyrosMaterialHelper.CreateColorMaterial(color);
                foreach (var r in renderers)
                    r.sharedMaterial = mat;
            }
            else
            {
                // No renderer on the object — add one with a unit-cube mesh
                var mf = go.GetComponent<MeshFilter>();
                if (mf == null)
                {
                    mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = CreateCubeMesh();
                }
                var mr = go.GetComponent<MeshRenderer>();
                if (mr == null)
                    mr = go.AddComponent<MeshRenderer>();

                mr.sharedMaterial = SyrosMaterialHelper.CreateColorMaterial(color);
            }
        }

        /// <summary>Create a unit-cube mesh by cloning Unity's built-in primitive.</summary>
        static Mesh CreateCubeMesh()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh mesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(cube);
            return mesh;
        }

        /// <summary>Create a child GameObject under a parent.</summary>
        static Transform CreateChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent);
            return go.transform;
        }
    }
}
