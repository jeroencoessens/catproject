using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace SyrosWorld
{
    /// <summary>
    /// Parses a GeoJSON <c>FeatureCollection</c> (exported from OSM + DEM
    /// enrichment) into a <see cref="SyrosMapData"/> container.
    ///
    /// Expected GeoJSON structure per feature:
    /// <code>
    /// {
    ///   "type": "Feature",
    ///   "properties": {
    ///     "building": "yes",          // → SyrosBuilding
    ///     "highway":  "residential",  // → SyrosStreet
    ///     "name":     "Αγία Νικόλαος",
    ///     "elevation": 42             // metres ASL (optional)
    ///   },
    ///   "geometry": {
    ///     "type": "Polygon" | "LineString",
    ///     "coordinates": [...]
    ///   }
    /// }
    /// </code>
    ///
    /// Requires the <c>com.unity.nuget.newtonsoft-json</c> package
    /// (Window → Package Manager → + → Add package by name).
    /// </summary>
    public static class SyrosOSMParser
    {
        // =================================================================
        //  PUBLIC API
        // =================================================================

        /// <summary>Parse a GeoJSON <see cref="TextAsset"/> into map data.</summary>
        public static SyrosMapData Parse(TextAsset jsonAsset)
        {
            if (jsonAsset == null)
            {
                Debug.LogError("[SyrosOSMParser] JSON TextAsset is null.");
                return new SyrosMapData();
            }
            return Parse(jsonAsset.text);
        }

        /// <summary>Parse a raw GeoJSON string into map data.</summary>
        public static SyrosMapData Parse(string json)
        {
            var data = new SyrosMapData();

            // ── Parse root JSON ─────────────────────────────────────────
            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[SyrosOSMParser] JSON parse failed: {ex.Message}");
                return data;
            }

            var features = root["features"] as JArray;
            if (features == null)
            {
                Debug.LogError("[SyrosOSMParser] No 'features' array in GeoJSON.");
                return data;
            }

            // ── Iterate features ────────────────────────────────────────
            int totalFeatures = features.Count;
            int processed = 0;

            foreach (JObject feature in features)
            {
                processed++;

                var properties = feature["properties"] as JObject;
                var geometry   = feature["geometry"]   as JObject;
                if (properties == null || geometry == null) continue;

                // Read common properties
                string geomType = geometry["type"]?.ToString();
                string building = NullIfEmpty(properties["building"]?.ToString());
                string highway  = NullIfEmpty(properties["highway"]?.ToString());
                string name     = NullIfEmpty(properties["name"]?.ToString());

                // Read elevation (metres ASL).  Missing / null → –1.
                float elevation = -1f;
                var elevToken = properties["elevation"];
                if (elevToken != null && elevToken.Type != JTokenType.Null)
                    elevation = elevToken.Value<float>();

                var coordsToken = geometry["coordinates"];
                if (coordsToken == null) continue;

                // ── Buildings (Polygon geometry with a building tag) ────
                if (building != null && geomType == "Polygon")
                {
                    var bld = ParseBuilding(coordsToken, name, building);
                    if (bld != null)
                    {
                        bld.elevation = elevation;
                        data.buildings.Add(bld);
                    }
                }
                // ── Streets (LineString geometry with a highway tag) ────
                else if (highway != null && geomType == "LineString")
                {
                    var street = ParseStreet(coordsToken, name, highway);
                    if (street != null)
                    {
                        street.elevation = elevation;
                        data.streets.Add(street);
                    }
                }

                // Progress feedback in the editor
                #if UNITY_EDITOR
                if (processed % 5000 == 0)
                {
                    float pct = (float)processed / totalFeatures * 100f;
                    Debug.Log($"[SyrosOSMParser] Parsed {processed}/{totalFeatures} ({pct:F0}%)");
                }
                #endif
            }

            // ── Finalise ────────────────────────────────────────────────
            data.ComputeBounds();

            Debug.Log($"[SyrosOSMParser] Done: {data.buildings.Count} buildings " +
                      $"({data.POIs.Count} POIs), {data.streets.Count} streets.  " +
                      $"Bounds: ({data.minLon:F4}, {data.minLat:F4}) – " +
                      $"({data.maxLon:F4}, {data.maxLat:F4})");

            return data;
        }

        // =================================================================
        //  GEOMETRY PARSERS
        // =================================================================

        /// <summary>
        /// Parse a Polygon geometry into a <see cref="SyrosBuilding"/>.
        /// Only reads the outer ring (index 0).  Returns <c>null</c> if fewer
        /// than 3 vertices are found.
        /// </summary>
        static SyrosBuilding ParseBuilding(JToken coordsToken, string name, string buildingType)
        {
            // Polygon coords: [ [ [lon,lat], … ] ]  (array of rings)
            var rings = coordsToken as JArray;
            if (rings == null || rings.Count == 0) return null;

            var outerRing = rings[0] as JArray;
            if (outerRing == null || outerRing.Count < 3) return null;

            var bld = new SyrosBuilding
            {
                name         = name,
                buildingType = buildingType
            };

            foreach (JArray point in outerRing)
            {
                if (point.Count >= 2)
                {
                    double lon = point[0].Value<double>();
                    double lat = point[1].Value<double>();
                    bld.polygon.Add(new Vector2d(lon, lat));
                }
            }

            return bld.polygon.Count >= 3 ? bld : null;
        }

        /// <summary>
        /// Parse a LineString geometry into a <see cref="SyrosStreet"/>.
        /// Returns <c>null</c> if fewer than 2 vertices are found.
        /// </summary>
        static SyrosStreet ParseStreet(JToken coordsToken, string name, string highwayType)
        {
            // LineString coords: [ [lon,lat], … ]
            var points = coordsToken as JArray;
            if (points == null || points.Count < 2) return null;

            var street = new SyrosStreet
            {
                name        = name,
                highwayType = highwayType
            };

            foreach (JArray point in points)
            {
                if (point.Count >= 2)
                {
                    double lon = point[0].Value<double>();
                    double lat = point[1].Value<double>();
                    street.points.Add(new Vector2d(lon, lat));
                }
            }

            return street.points.Count >= 2 ? street : null;
        }

        // =================================================================
        //  HELPERS
        // =================================================================

        /// <summary>Return <c>null</c> if the string is null or empty.</summary>
        static string NullIfEmpty(string s) =>
            string.IsNullOrEmpty(s) ? null : s;
    }
}
