using UnityEngine;

namespace SyrosWorld
{
    /// <summary>
    /// MonoBehaviour attached to named buildings (Points of Interest).
    ///
    /// Stores the building's OSM metadata (name, type) and provides
    /// elevation-debug fields so you can verify that generation placed
    /// the building at the correct real-world altitude.
    ///
    /// Scene-view gizmos draw an orange sphere above each POI with its
    /// name, expected elevation, actual Unity Y, and the delta between
    /// the two.  A positive delta means Unity placed it too high;
    /// negative means too low.
    /// </summary>
    public class SyrosPOIMarker : MonoBehaviour
    {
        // =============================================================
        //  OSM METADATA
        // =============================================================

        /// <summary>OSM "name" tag for this building.</summary>
        [Tooltip("The OSM name of this point of interest")]
        public string poiName;

        /// <summary>OSM "building" tag value (e.g. "church", "yes").</summary>
        [Tooltip("The OSM building type tag")]
        public string buildingType;

        // =============================================================
        //  ELEVATION DEBUG
        // =============================================================

        /// <summary>
        /// Ground elevation from the GeoJSON "elevation" property.
        /// Metres above sea level.  -1 means the source had no value.
        /// </summary>
        [Header("Elevation Debug")]
        [Tooltip("Expected ground elevation from JSON data (metres above sea level). -1 = unknown.")]
        public float expectedElevation = -1f;

        /// <summary>Unity world-space Y recorded at build time.</summary>
        [Tooltip("Actual Unity Y position at generation time")]
        public float actualUnityY = -1f;

        /// <summary>
        /// Signed difference: <c>actualUnityY - expectedElevation</c>.
        /// Positive = Unity placed the building too high.
        /// Returns 0 when either value is unknown (-1).
        /// </summary>
        public float ElevationDelta => (expectedElevation >= 0f && actualUnityY >= 0f)
            ? actualUnityY - expectedElevation : 0f;

        // =============================================================
        //  RUNTIME LABEL
        // =============================================================

        /// <summary>Whether an in-game label should be shown above this POI.</summary>
        [Header("Runtime")]
        [Tooltip("Optional label shown in-game")]
        public bool showLabel = true;

        /// <summary>World-space Y offset of the label above the building top.</summary>
        [Tooltip("Label Y offset above the building")]
        public float labelYOffset = 2f;

        /// <summary>World-space position of the label anchor point.</summary>
        public Vector3 LabelPosition => transform.position +
            Vector3.up * (transform.localScale.y * 0.5f + labelYOffset);

        // =============================================================
        //  EDITOR GIZMOS
        // =============================================================

        #if UNITY_EDITOR
        /// <summary>Draw an orange sphere + elevation label in the Scene view.</summary>
        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.45f, 0.15f, 0.8f);
            Gizmos.DrawWireSphere(LabelPosition, 1f);

            string label = poiName ?? "POI";
            if (expectedElevation >= 0f)
            {
                float delta = ElevationDelta;
                label += $"\nExpected: {expectedElevation:F0}m  Unity Y: {transform.position.y:F1}";
                label += $"\nΔ {delta:+0.0;-0.0;0}m";
            }
            UnityEditor.Handles.Label(LabelPosition + Vector3.up * 1.5f, label);
        }
        #endif

        /// <summary>Highlight bounding box when selected in the hierarchy.</summary>
        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(transform.position,
                transform.localScale + Vector3.one * 0.2f);
        }
    }
}
