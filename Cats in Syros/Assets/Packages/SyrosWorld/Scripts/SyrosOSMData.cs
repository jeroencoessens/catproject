using System.Collections.Generic;
using UnityEngine;

namespace SyrosWorld
{
    // =====================================================================
    //  DATA MODELS
    //  Plain data classes produced by SyrosOSMParser and consumed by every
    //  downstream stage (terrain, buildings, streets).  They carry geometry
    //  in geographic coordinates (lon/lat) plus optional metadata like
    //  elevation, name, and type tags.
    // =====================================================================

    /// <summary>
    /// A single parsed OSM building footprint.
    /// Polygon vertices are stored in geo space (lon, lat).
    /// </summary>
    [System.Serializable]
    public class SyrosBuilding
    {
        /// <summary>Outer-ring polygon vertices in geo coordinates (lon, lat).</summary>
        public List<Vector2d> polygon = new List<Vector2d>();

        /// <summary>Human-readable building name from OSM. <c>null</c> if unnamed.</summary>
        public string name;

        /// <summary>Raw OSM <c>building=*</c> tag value (e.g. "yes", "church").</summary>
        public string buildingType;

        /// <summary>
        /// Ground-level elevation in metres above sea level, sourced from the
        /// GeoJSON <c>elevation</c> property.  –1 means no data available.
        /// </summary>
        public float elevation = -1f;

        /// <summary><c>true</c> if the building has a non-empty name (point of interest).</summary>
        public bool IsPOI => !string.IsNullOrEmpty(name);

        /// <summary>
        /// Arithmetic centroid of the polygon vertices in geo coordinates.
        /// Cheap approximation (ignores signed-area weighting) but fine for
        /// building footprints which are small and roughly convex.
        /// </summary>
        public Vector2d Centroid
        {
            get
            {
                if (polygon == null || polygon.Count == 0)
                    return new Vector2d(0, 0);

                double cx = 0, cy = 0;
                for (int i = 0; i < polygon.Count; i++)
                {
                    cx += polygon[i].x;
                    cy += polygon[i].y;
                }
                return new Vector2d(cx / polygon.Count, cy / polygon.Count);
            }
        }
    }

    /// <summary>
    /// A single parsed OSM street / highway polyline.
    /// Vertices are stored in geo space (lon, lat).
    /// </summary>
    [System.Serializable]
    public class SyrosStreet
    {
        /// <summary>Polyline vertices in geo coordinates (lon, lat).</summary>
        public List<Vector2d> points = new List<Vector2d>();

        /// <summary>OSM <c>highway=*</c> tag (residential, secondary, footway, steps, …).</summary>
        public string highwayType;

        /// <summary>Street name from OSM. <c>null</c> if unnamed.</summary>
        public string name;

        /// <summary>
        /// Ground-level elevation in metres above sea level.  –1 means no data.
        /// </summary>
        public float elevation = -1f;
    }

    // =====================================================================
    //  AGGREGATE CONTAINER
    // =====================================================================

    /// <summary>
    /// Top-level container holding all parsed OSM features plus computed
    /// geographic bounds.  Produced by <see cref="SyrosOSMParser"/>.
    /// </summary>
    [System.Serializable]
    public class SyrosMapData
    {
        public List<SyrosBuilding> buildings = new List<SyrosBuilding>();
        public List<SyrosStreet>   streets   = new List<SyrosStreet>();

        /// <summary>
        /// Convenience accessor: returns only buildings that have a name (POIs).
        /// Allocates a new list each call — cache the result if called in a loop.
        /// </summary>
        public List<SyrosBuilding> POIs
        {
            get
            {
                var list = new List<SyrosBuilding>();
                foreach (var b in buildings)
                    if (b.IsPOI) list.Add(b);
                return list;
            }
        }

        // ── Computed geo bounds (populated by ComputeBounds) ────────────
        public double minLon = double.MaxValue;
        public double maxLon = double.MinValue;
        public double minLat = double.MaxValue;
        public double maxLat = double.MinValue;

        /// <summary>
        /// Scan every vertex in every feature and update the min/max bounds.
        /// Called automatically after parsing.
        /// </summary>
        public void ComputeBounds()
        {
            minLon = double.MaxValue;
            maxLon = double.MinValue;
            minLat = double.MaxValue;
            maxLat = double.MinValue;

            foreach (var b in buildings)
                foreach (var p in b.polygon)
                    ExpandBounds(p.x, p.y);

            foreach (var s in streets)
                foreach (var p in s.points)
                    ExpandBounds(p.x, p.y);
        }

        void ExpandBounds(double lon, double lat)
        {
            if (lon < minLon) minLon = lon;
            if (lon > maxLon) maxLon = lon;
            if (lat < minLat) minLat = lat;
            if (lat > maxLat) maxLat = lat;
        }

        // ── Elevation helpers ───────────────────────────────────────────

        /// <summary>
        /// Collect sparse elevation samples from every feature that carries
        /// elevation data.  Used by <see cref="SyrosTerrainGenerator"/> to
        /// build an intermediate IDW raster grid.
        ///
        /// <b>Buildings</b> contribute their centroid <i>plus</i> every polygon
        /// vertex (for denser coverage).  <b>Streets</b> contribute every
        /// polyline vertex.  Features with <c>elevation &lt; 0</c> are skipped.
        /// </summary>
        public List<ElevationPoint> GetElevationPoints()
        {
            var pts = new List<ElevationPoint>();

            foreach (var b in buildings)
            {
                if (b.elevation < 0f) continue;

                // Centroid gives one clean sample per building footprint
                var c = b.Centroid;
                pts.Add(new ElevationPoint(c.x, c.y, b.elevation));

                // Polygon vertices add denser spatial coverage
                foreach (var v in b.polygon)
                    pts.Add(new ElevationPoint(v.x, v.y, b.elevation));
            }

            foreach (var s in streets)
            {
                if (s.elevation < 0f) continue;

                // Every vertex of a street polyline is a data point
                foreach (var p in s.points)
                    pts.Add(new ElevationPoint(p.x, p.y, s.elevation));
            }

            return pts;
        }
    }

    // =====================================================================
    //  SUPPORTING STRUCTS
    // =====================================================================

    /// <summary>
    /// A single known-elevation sample in geographic space, used as input
    /// to the terrain elevation grid rasteriser.
    /// </summary>
    public struct ElevationPoint
    {
        /// <summary>Longitude (degrees east).</summary>
        public double lon;

        /// <summary>Latitude (degrees north).</summary>
        public double lat;

        /// <summary>Elevation in metres above sea level.</summary>
        public float elevation;

        public ElevationPoint(double lon, double lat, float elevation)
        {
            this.lon = lon;
            this.lat = lat;
            this.elevation = elevation;
        }
    }

    /// <summary>
    /// Double-precision 2D vector used for geographic coordinates.
    /// Convention: <c>x</c> = longitude, <c>y</c> = latitude.
    /// </summary>
    [System.Serializable]
    public struct Vector2d
    {
        public double x;  // longitude
        public double y;  // latitude

        public Vector2d(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2d operator +(Vector2d a, Vector2d b) =>
            new Vector2d(a.x + b.x, a.y + b.y);

        public static Vector2d operator -(Vector2d a, Vector2d b) =>
            new Vector2d(a.x - b.x, a.y - b.y);

        public static Vector2d operator *(Vector2d a, double s) =>
            new Vector2d(a.x * s, a.y * s);

        public override string ToString() => $"({x:F6}, {y:F6})";
    }
}
