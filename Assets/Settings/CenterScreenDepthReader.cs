using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;
using System;
using System.Collections.Generic;
using TMPro;

/// <summary>
/// Places objects on the nearest detected surface while intelligently filtering out floor planes.
/// Uses depth data from AR occlusion and plane detection to identify meaningful placement targets.
/// </summary>
public class CenterScreenDepthReader : MonoBehaviour
{
    [Header("AR Components")]
    [SerializeField] private AROcclusionManager _occlusionManager;
    [SerializeField] private ARRaycastManager _raycastManager;
    [SerializeField] private ARPlaneManager _planeManager;

    [Header("Placement Settings")]
    [SerializeField] private GameObject objectToPlacePrefab;

    [Tooltip("Objects will not be placed if the nearest obstacle is further than this (meters).")]
    [SerializeField] private float maxDistance = 4.0f;

    [Tooltip("How many frames to wait before firing the placement raycast.")]
    [SerializeField] private int placementFrameInterval = 10;

    [Header("Floor Filtering")]
    [Tooltip("Enable to ignore depth pixels that belong to detected floor planes.")]
    [SerializeField] private bool ignoreFloorPlanes = true;

    [Tooltip("Vertical tolerance for considering a depth point as part of a floor plane (meters).")]
    [SerializeField] private float floorHeightTolerance = 0.15f;

    [Tooltip("Minimum distance above floor to consider valid (meters). Helps filter chair legs, etc.")]
    [SerializeField] private float minHeightAboveFloor = 0.3f;

    [Header("UI Components")]
    [Tooltip("Assign 8 Text elements. Order: Row1(Left->Right), then Row2(Left->Right)")]
    [SerializeField] private TextMeshProUGUI[] gridTexts;

    // Internal Depth Data
    private Texture2D _depthTexture;
    private short[] _depthArray;
    private int _depthWidth;
    private int _depthHeight;
    private Vector2[] _gridCenters; // Size 8

    // Raycast Data
    private List<ARRaycastHit> _hits = new List<ARRaycastHit>();
    private GameObject _placedObject;

    // Floor Plane Caching
    private List<FloorPlaneData> _cachedFloorPlanes = new List<FloorPlaneData>();
    private int _lastPlaneUpdateFrame = -1;
    private const int PLANE_CACHE_UPDATE_INTERVAL = 30; // Update cache every 30 frames

    // Constants
    private const float InvalidDepthValue = -1.0f;
    private const float MillimeterToMeter = 0.001f;

    /// <summary>
    /// Cached floor plane data for efficient spatial queries
    /// </summary>
    private struct FloorPlaneData
    {
        public Vector3 center;
        public float yPosition;
        public Vector2 extents;
        public Quaternion rotation;
        public Plane infinitePlane;

        public FloorPlaneData(ARPlane arPlane)
        {
            center = arPlane.center;
            yPosition = arPlane.center.y;
            extents = arPlane.extents;
            rotation = arPlane.transform.rotation;
            infinitePlane = arPlane.infinitePlane;
        }
    }

    void Start()
    {
        // 1. Validation
        if (_occlusionManager == null || _raycastManager == null || objectToPlacePrefab == null)
        {
            Debug.LogError("[NearestGridObjectPlacer] Missing AR Components or Prefab. Please assign in Inspector.");
            enabled = false;
            return;
        }

        if (_planeManager == null && ignoreFloorPlanes)
        {
            Debug.LogWarning("[NearestGridObjectPlacer] ARPlaneManager not assigned. Floor filtering disabled.");
            ignoreFloorPlanes = false;
        }

        // 2. Initialize Depth Buffers
        _depthWidth = 1;
        _depthHeight = 1;
        _depthTexture = new Texture2D(_depthWidth, _depthHeight, TextureFormat.R16, false);
        _depthArray = new short[_depthWidth * _depthHeight];

        // 3. Define Grid UVs (8 Points in the Middle Band)
        // These form a 2x4 grid focused on the center vertical band of the screen
        _gridCenters = new Vector2[8];

        // Row 1: Upper Middle (Y = 0.6)
        _gridCenters[0] = new Vector2(0.2f, 0.6f);
        _gridCenters[1] = new Vector2(0.4f, 0.6f);
        _gridCenters[2] = new Vector2(0.6f, 0.6f);
        _gridCenters[3] = new Vector2(0.8f, 0.6f);

        // Row 2: Lower Middle (Y = 0.4)
        _gridCenters[4] = new Vector2(0.2f, 0.4f);
        _gridCenters[5] = new Vector2(0.4f, 0.4f);
        _gridCenters[6] = new Vector2(0.6f, 0.4f);
        _gridCenters[7] = new Vector2(0.8f, 0.4f);
    }

    void Update()
    {
        // 1. Update depth data from AR system
        UpdateEnvironmentDepthImage();

        // 2. Update floor plane cache periodically
        if (ignoreFloorPlanes && _planeManager != null)
        {
            UpdateFloorPlaneCache();
        }

        // 3. Scan all 8 grid points
        float minDistance = float.MaxValue;
        int nearestIndex = -1;
        float[] currentDepths = new float[8];

        for (int i = 0; i < 8; i++)
        {
            float depth = GetDepthFromUV(_gridCenters[i], _depthArray);
            currentDepths[i] = depth;

            // Apply multi-stage filtering
            if (IsValidDepthCandidate(depth, _gridCenters[i]))
            {
                if (depth < minDistance)
                {
                    minDistance = depth;
                    nearestIndex = i;
                }
            }
        }

        // 4. Update UI (Visual Feedback)
        UpdateUITexts(currentDepths, nearestIndex);

        // 5. Place Object Logic
        if (nearestIndex != -1)
        {
            // Found a valid target within range
            // Optimization: Only raycast every N frames to reduce CPU load
            if (Time.frameCount % placementFrameInterval == 0)
            {
                Vector2 winnerUV = _gridCenters[nearestIndex];

                // Convert UV to Screen Coordinates
                Vector2 screenPosition = new Vector2(
                    winnerUV.x * Screen.width,
                    winnerUV.y * Screen.height
                );

                TryPlaceOnDepth(screenPosition);
            }
        }
        else
        {
            // No valid target found - hide the object
            if (_placedObject != null && _placedObject.activeSelf)
            {
                _placedObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// Multi-stage filtering to determine if a depth reading is a valid placement candidate
    /// </summary>
    private bool IsValidDepthCandidate(float depth, Vector2 uv)
    {
        // Stage 1: Basic validity checks
        if (depth == InvalidDepthValue || depth <= 0.1f)
            return false;

        // Stage 2: Range check
        if (depth >= maxDistance)
            return false;

        // Stage 3: Floor plane filtering (if enabled)
        if (ignoreFloorPlanes && _cachedFloorPlanes.Count > 0)
        {
            // Convert UV + depth to approximate world position
            Vector3 worldPos = DepthUVToWorldPosition(uv, depth);

            if (IsPointOnOrNearFloor(worldPos))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Updates cached floor plane data periodically for performance
    /// </summary>
    private void UpdateFloorPlaneCache()
    {
        // Only update cache every N frames
        if (Time.frameCount - _lastPlaneUpdateFrame < PLANE_CACHE_UPDATE_INTERVAL)
            return;

        _lastPlaneUpdateFrame = Time.frameCount;
        _cachedFloorPlanes.Clear();

        // Iterate through all tracked planes
        foreach (var plane in _planeManager.trackables)
        {
            // Only cache horizontal upward-facing planes (floors)
            if (plane.alignment == PlaneAlignment.HorizontalUp)
            {
                _cachedFloorPlanes.Add(new FloorPlaneData(plane));
            }
        }
    }

    /// <summary>
    /// Checks if a world position is on or very close to a detected floor plane
    /// </summary>
    private bool IsPointOnOrNearFloor(Vector3 worldPos)
    {
        foreach (var floorPlane in _cachedFloorPlanes)
        {
            // Quick Y-coordinate check first (fast rejection)
            float heightAboveFloor = worldPos.y - floorPlane.yPosition;

            // If point is below floor or too close above it
            if (heightAboveFloor < minHeightAboveFloor)
            {
                // More precise check: is it within the plane's horizontal bounds?
                if (IsPointInPlaneBounds(worldPos, floorPlane))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a world position falls within a plane's horizontal bounds
    /// Uses a simplified 2D bounds check in XZ plane
    /// </summary>
    private bool IsPointInPlaneBounds(Vector3 worldPos, FloorPlaneData plane)
    {
        // Transform world point to plane's local space
        Vector3 localPos = Quaternion.Inverse(plane.rotation) * (worldPos - plane.center);

        // Check if within extents (extents are half-dimensions)
        return Mathf.Abs(localPos.x) <= plane.extents.x &&
               Mathf.Abs(localPos.z) <= plane.extents.y;
    }

    /// <summary>
    /// Converts UV coordinates + depth to an approximate world position
    /// Note: This is an approximation. For precise positioning, use ARRaycast results.
    /// </summary>
    private Vector3 DepthUVToWorldPosition(Vector2 uv, float depth)
    {
        // Get camera
        Camera arCamera = Camera.main;
        if (arCamera == null) return Vector3.zero;

        // Convert UV to viewport point
        Vector3 viewportPoint = new Vector3(uv.x, uv.y, depth);

        // Convert to world space
        // Note: This assumes the depth is measured from camera's near plane
        Vector3 screenPoint = new Vector3(
            viewportPoint.x * Screen.width,
            viewportPoint.y * Screen.height,
            depth
        );

        return arCamera.ScreenToWorldPoint(screenPoint);
    }

    /// <summary>
    /// Attempts to place the object at the specified screen position using AR raycasting
    /// </summary>
    private void TryPlaceOnDepth(Vector2 screenPosition)
    {
        _hits.Clear();

        // Fire raycast at the winning grid point
        bool didHit = _raycastManager.Raycast(
            screenPosition,
            _hits,
            TrackableType.Depth
        );

        if (didHit && _hits.Count > 0)
        {
            Pose hitPose = _hits[0].pose;

            // Double-check: ensure the hit position isn't on a floor plane
            if (ignoreFloorPlanes && IsPointOnOrNearFloor(hitPose.position))
            {
                // Hit a floor plane - hide object
                if (_placedObject != null && _placedObject.activeSelf)
                {
                    _placedObject.SetActive(false);
                }
                return;
            }

            if (_placedObject == null)
            {
                _placedObject = Instantiate(objectToPlacePrefab, hitPose.position, hitPose.rotation);
            }
            else
            {
                _placedObject.transform.position = hitPose.position;
                _placedObject.transform.rotation = hitPose.rotation;

                // Ensure it is visible
                if (!_placedObject.activeSelf)
                    _placedObject.SetActive(true);
            }
        }
    }

    /// <summary>
    /// Updates UI text elements with depth information and visual feedback
    /// </summary>
    private void UpdateUITexts(float[] depths, int nearestIndex)
    {
        if (gridTexts == null) return;

        for (int i = 0; i < 8; i++)
        {
            if (i >= gridTexts.Length || gridTexts[i] == null) continue;

            float d = depths[i];

            if (d == InvalidDepthValue || d <= 0)
            {
                gridTexts[i].text = "N/A";
                gridTexts[i].color = Color.gray; // No data
            }
            else if (d >= maxDistance)
            {
                gridTexts[i].text = $"{d:F2}m";
                gridTexts[i].color = Color.red; // Too far
            }
            else if (ignoreFloorPlanes && IsPointOnOrNearFloor(DepthUVToWorldPosition(_gridCenters[i], d)))
            {
                gridTexts[i].text = $"{d:F2}m FLOOR";
                gridTexts[i].color = Color.yellow; // Filtered out (floor)
            }
            else
            {
                gridTexts[i].text = $"{d:F2}m";
                // Green if winner, White if valid candidate
                gridTexts[i].color = (i == nearestIndex) ? Color.green : Color.white;
                gridTexts[i].fontWeight = (i == nearestIndex) ? FontWeight.Bold : FontWeight.Regular;
            }
        }
    }

    // --- DEPTH IMAGE PROCESSING ---

    /// <summary>
    /// Acquires and processes the latest environment depth data from AR system
    /// </summary>
    private void UpdateEnvironmentDepthImage()
    {
        if (_occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage image))
        {
            using (image)
            {
                // Reinitialize texture if dimensions change
                if (_depthWidth != image.width || _depthHeight != image.height)
                {
                    _depthWidth = image.width;
                    _depthHeight = image.height;
                    _depthTexture.Reinitialize(_depthWidth, _depthHeight);
                    _depthArray = new short[_depthWidth * _depthHeight];
                }

                // Load raw data into texture
                var rawData = image.GetPlane(0).data;
                _depthTexture.LoadRawTextureData(rawData);
                _depthTexture.Apply();

                // Copy to short array for CPU access
                NativeArray<byte> byteBuffer = _depthTexture.GetRawTextureData<byte>();
                if (_depthArray.Length * sizeof(short) == byteBuffer.Length)
                {
                    Buffer.BlockCopy(byteBuffer.ToArray(), 0, _depthArray, 0, byteBuffer.Length);
                }
            }
        }
    }

    /// <summary>
    /// Retrieves depth value at specified UV coordinates
    /// </summary>
    public float GetDepthFromUV(Vector2 uv, short[] depthArray)
    {
        int depthX = (int)(uv.x * (_depthWidth - 1));
        int depthY = (int)(uv.y * (_depthHeight - 1));
        return GetDepthFromXY(depthX, depthY, depthArray);
    }

    /// <summary>
    /// Retrieves depth value at specified pixel coordinates
    /// </summary>
    public float GetDepthFromXY(int x, int y, short[] depthArray)
    {
        if (x >= _depthWidth || x < 0 || y >= _depthHeight || y < 0)
            return InvalidDepthValue;

        var depthIndex = (y * _depthWidth) + x;
        if (depthIndex < 0 || depthIndex >= depthArray.Length)
            return InvalidDepthValue;

        var depthInShort = depthArray[depthIndex];
        return depthInShort * MillimeterToMeter;
    }

    // --- DEBUGGING ---

    void OnDrawGizmos()
    {
        // Visualize cached floor planes in Scene view
        if (!Application.isPlaying || _cachedFloorPlanes == null) return;

        Gizmos.color = Color.yellow;
        foreach (var plane in _cachedFloorPlanes)
        {
            // Draw floor plane bounds
            Gizmos.matrix = Matrix4x4.TRS(plane.center, plane.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, new Vector3(plane.extents.x * 2, 0.01f, plane.extents.y * 2));
        }
    }
}