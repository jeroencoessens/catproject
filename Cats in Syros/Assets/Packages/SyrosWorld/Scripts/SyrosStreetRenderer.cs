using System.Collections.Generic;
using UnityEngine;

namespace SyrosWorld
{
    /// <summary>
    /// Generates street mesh strips projected onto the terrain surface.
    ///
    /// Each street polyline is expanded into a two-triangle-wide strip at
    /// the configured width, then lifted slightly above the terrain via
    /// <see cref="SyrosWorldConfig.streetYOffset"/> to avoid z-fighting.
    ///
    /// Streets are grouped by highway category and merged into batched meshes
    /// (splitting at Unity's 16-bit index vertex limit) so material draw calls
    /// are minimised.
    /// </summary>
    public static class SyrosStreetRenderer
    {
        /// <summary>Maximum vertices per mesh (Unity 16-bit index limit is 65 535).</summary>
        const int MaxVerticesPerMesh = 60000;

        // =================================================================
        //  PUBLIC API
        // =================================================================

        /// <summary>
        /// Render all streets as mesh strips on the terrain.
        /// </summary>
        public static List<GameObject> RenderStreets(
            List<SyrosStreet> streets,
            SyrosGeoConverter converter,
            SyrosWorldConfig config,
            Terrain terrain,
            Transform parent)
        {
            var created = new List<GameObject>();
            Transform streetsParent = new GameObject("Streets").transform;
            if (parent != null) streetsParent.SetParent(parent);

            // ── Group streets by highway category for material batching ──
            var categories = new Dictionary<string, List<SyrosStreet>>();
            foreach (var street in streets)
            {
                string cat = CategorizeHighway(street.highwayType);
                if (!categories.ContainsKey(cat))
                    categories[cat] = new List<SyrosStreet>();
                categories[cat].Add(street);
            }

            // ── Build and instantiate meshes per category ───────────────
            foreach (var kvp in categories)
            {
                string category   = kvp.Key;
                var    streetList = kvp.Value;

                // Determine the colour for this category (from the first street's type)
                Color color = config.GetStreetColor(
                    streetList.Count > 0 ? streetList[0].highwayType : "");

                var meshes = BuildStreetMeshes(streetList, converter, config, terrain);

                for (int m = 0; m < meshes.Count; m++)
                {
                    var go = new GameObject($"Streets_{category}_{m}");
                    go.transform.SetParent(streetsParent);
                    go.isStatic = true;

                    go.AddComponent<MeshFilter>().sharedMesh = meshes[m];
                    go.AddComponent<MeshRenderer>().sharedMaterial =
                        SyrosMaterialHelper.CreateColorMaterial(color);

                    created.Add(go);
                }
            }

            Debug.Log($"[SyrosStreetRenderer] Rendered {streets.Count} streets " +
                      $"in {created.Count} mesh chunks across {categories.Count} categories.");

            return created;
        }

        // =================================================================
        //  MESH BUILDING
        // =================================================================

        /// <summary>
        /// Build combined mesh(es) for a list of streets, splitting when the
        /// 16-bit vertex limit is approached.  Each street is converted to a
        /// triangle-strip ribbon centred on the polyline.
        /// </summary>
        static List<Mesh> BuildStreetMeshes(
            List<SyrosStreet> streets,
            SyrosGeoConverter converter,
            SyrosWorldConfig config,
            Terrain terrain)
        {
            var meshes    = new List<Mesh>();
            var vertices  = new List<Vector3>();
            var triangles = new List<int>();
            var uvs       = new List<Vector2>();

            foreach (var street in streets)
            {
                float halfWidth = config.GetStreetWidth(street.highwayType) * 0.5f;
                float yOffset   = config.streetYOffset;

                // Convert points to world space + apply Y offset
                var worldPoints = new List<Vector3>();
                foreach (var geo in street.points)
                {
                    Vector3 wp = converter.GeoToWorld(geo, terrain);
                    wp.y += yOffset;
                    worldPoints.Add(wp);
                }

                if (worldPoints.Count < 2) continue;

                // Flush current mesh if adding this street would exceed the limit
                int newVerts = worldPoints.Count * 2;
                if (vertices.Count + newVerts > MaxVerticesPerMesh && vertices.Count > 0)
                {
                    meshes.Add(CreateMesh(vertices, triangles, uvs));
                    vertices.Clear();
                    triangles.Clear();
                    uvs.Clear();
                }

                // ── Generate ribbon geometry ────────────────────────────
                int   baseVertex = vertices.Count;
                float totalDist  = 0f;

                for (int i = 0; i < worldPoints.Count; i++)
                {
                    Vector3 point = worldPoints[i];

                    // Tangent direction (smoothed at interior vertices)
                    Vector3 forward;
                    if (i == 0)
                        forward = worldPoints[1] - worldPoints[0];
                    else if (i == worldPoints.Count - 1)
                        forward = worldPoints[i] - worldPoints[i - 1];
                    else
                        forward = worldPoints[i + 1] - worldPoints[i - 1];

                    forward.y = 0f;
                    if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
                    forward.Normalize();

                    // Perpendicular (cross with up)
                    Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

                    // Two vertices per polyline point (left and right edges)
                    vertices.Add(point - right * halfWidth);
                    vertices.Add(point + right * halfWidth);

                    if (i > 0)
                        totalDist += Vector3.Distance(worldPoints[i], worldPoints[i - 1]);

                    float vCoord = totalDist / (halfWidth * 2f);
                    uvs.Add(new Vector2(0f, vCoord));
                    uvs.Add(new Vector2(1f, vCoord));

                    // Two triangles per segment (quad)
                    if (i > 0)
                    {
                        int v = baseVertex + i * 2;
                        triangles.Add(v - 2);
                        triangles.Add(v);
                        triangles.Add(v - 1);

                        triangles.Add(v - 1);
                        triangles.Add(v);
                        triangles.Add(v + 1);
                    }
                }
            }

            // Flush remaining geometry
            if (vertices.Count > 0)
                meshes.Add(CreateMesh(vertices, triangles, uvs));

            return meshes;
        }

        /// <summary>Create a Mesh from vertex/triangle/UV lists.</summary>
        static Mesh CreateMesh(List<Vector3> vertices, List<int> triangles, List<Vector2> uvs)
        {
            var mesh = new Mesh { name = "StreetMesh" };

            if (vertices.Count > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uvs);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // =================================================================
        //  CATEGORY MAPPING
        // =================================================================

        /// <summary>Map an OSM highway tag to a rendering category string.</summary>
        static string CategorizeHighway(string highwayType)
        {
            if (string.IsNullOrEmpty(highwayType)) return "other";
            switch (highwayType)
            {
                case "primary":
                case "primary_link":
                case "trunk":
                case "trunk_link":      return "primary";

                case "secondary":
                case "secondary_link":
                case "tertiary":
                case "tertiary_link":   return "secondary";

                case "residential":
                case "living_street":
                case "unclassified":
                case "service":         return "residential";

                case "footway":
                case "path":
                case "track":
                case "pedestrian":      return "footpath";

                case "steps":           return "steps";

                default:                return "other";
            }
        }
    }
}
