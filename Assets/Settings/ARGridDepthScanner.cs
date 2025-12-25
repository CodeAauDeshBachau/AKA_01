using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections.LowLevel.Unsafe;
using System.Collections.Generic;

/// <summary>
/// Scans the screen in a grid pattern to find the nearest object using depth data
/// </summary>
public class ARGridDepthScanner : MonoBehaviour
{
    [Header("AR Components")]
    [SerializeField] private AROcclusionManager occlusionManager;
    [SerializeField] private ARCameraManager cameraManager;

    [Header("Placement Component")]
    [SerializeField] private ARObjectPlacer objectPlacer;

    [Header("Grid Settings")]
    [SerializeField] private int gridColumns = 3;
    [SerializeField] private int gridRows = 3;
    [SerializeField] private float scanInterval = 0.5f;

    [Header("Filtering")]
    [SerializeField] private int sampleRadius = 2;
    [SerializeField] private float minDepth = 0.3f; // Ignore very close objects
    [SerializeField] private float maxDepth = 8.0f; // Ignore far objects

    [Header("Debug")]
    [SerializeField] private bool showDebugOverlay = true;

    private float scanTimer = 0f;
    private GridScanResult lastScanResult;

    // Track screen dimensions
    private int screenWidth;
    private int screenHeight;

    public struct GridCell
    {
        public Vector2 screenCenter;  // Center of grid cell in screen coordinates
        public float distance;        // Measured distance (0 if invalid)
        public bool isValid;          // Whether we got a good reading
    }

    public class GridScanResult
    {
        public GridCell[] cells;
        public GridCell nearestCell;
        public bool hasValidData;
        public int validCellCount;
    }

    void Start()
    {
        if (occlusionManager == null)
            occlusionManager = FindObjectOfType<AROcclusionManager>();

        if (cameraManager == null)
            cameraManager = FindObjectOfType<ARCameraManager>();

        if (objectPlacer == null)
            objectPlacer = FindObjectOfType<ARObjectPlacer>();

        // Set depth mode for better AI-based depth
        if (occlusionManager != null)
        {
            occlusionManager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Medium;
        }

        UpdateScreenDimensions();
    }

    void Update()
    {
        UpdateScreenDimensions();

        scanTimer += Time.deltaTime;
        if (scanTimer >= scanInterval)
        {
            ScanGrid();
            scanTimer = 0f;
        }

        // Auto-place on nearest object (optional - can also trigger manually)
        if (lastScanResult != null && lastScanResult.hasValidData)
        {
            // Uncomment to auto-place on nearest object
            // PlaceOnNearestObject();
        }
    }

    void UpdateScreenDimensions()
    {
        screenWidth = Screen.width;
        screenHeight = Screen.height;
    }

    /// <summary>
    /// Scan the entire screen grid and find distances
    /// </summary>
    public GridScanResult ScanGrid()
    {
        if (occlusionManager == null ||
            !occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
        {
            return null;
        }

        using (depthImage)
        {
            GridScanResult result = new GridScanResult();
            result.cells = new GridCell[gridColumns * gridRows];

            float nearestDistance = float.MaxValue;
            GridCell nearestCell = new GridCell();
            int validCount = 0;

            // Scan each grid cell
            for (int row = 0; row < gridRows; row++)
            {
                for (int col = 0; col < gridColumns; col++)
                {
                    int index = row * gridColumns + col;

                    // Calculate screen position for this grid cell center
                    Vector2 screenPos = GetGridCellCenter(col, row);

                    // Convert screen position to depth image coordinates
                    Vector2 depthCoord = ScreenToDepthCoordinate(
                        screenPos,
                        depthImage.width,
                        depthImage.height
                    );

                    // Measure depth at this location
                    float distance = GetFilteredDepth(
                        depthImage,
                        (int)depthCoord.x,
                        (int)depthCoord.y
                    );

                    // Store result
                    GridCell cell = new GridCell
                    {
                        screenCenter = screenPos,
                        distance = distance,
                        isValid = distance > 0
                    };
                    result.cells[index] = cell;

                    // Track nearest valid cell
                    if (cell.isValid)
                    {
                        validCount++;
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearestCell = cell;
                        }
                    }
                }
            }

            result.nearestCell = nearestCell;
            result.hasValidData = validCount > 0;
            result.validCellCount = validCount;
            lastScanResult = result;

            Debug.Log($"Grid Scan: {validCount}/{result.cells.Length} valid cells, Nearest: {nearestDistance:F2}m at {nearestCell.screenCenter}");

            return result;
        }
    }

    /// <summary>
    /// Get the screen coordinate for the center of a grid cell
    /// </summary>
    Vector2 GetGridCellCenter(int col, int row)
    {
        // Calculate cell dimensions
        float cellWidth = screenWidth / (float)gridColumns;
        float cellHeight = screenHeight / (float)gridRows;

        // Get center of this cell
        float x = (col + 0.5f) * cellWidth;
        float y = (row + 0.5f) * cellHeight;

        return new Vector2(x, y);
    }

    /// <summary>
    /// Convert screen coordinates to depth image coordinates
    /// CRITICAL: Accounts for aspect ratio differences
    /// </summary>
    Vector2 ScreenToDepthCoordinate(Vector2 screenPos, int depthWidth, int depthHeight)
    {
        // Normalize screen position (0-1)
        float normalizedX = screenPos.x / screenWidth;
        float normalizedY = screenPos.y / screenHeight;

        // Handle potential camera rotation/flip based on device orientation
        // AR Foundation usually handles this, but we can account for it
        if (cameraManager != null && cameraManager.currentConfiguration.HasValue)
        {
            // The depth image might need transformation based on camera orientation
            // For most devices, this direct mapping works:
            float depthX = normalizedX * depthWidth;
            float depthY = normalizedY * depthHeight;

            return new Vector2(depthX, depthY);
        }

        // Fallback: direct mapping
        return new Vector2(
            normalizedX * depthWidth,
            normalizedY * depthHeight
        );
    }

    /// <summary>
    /// Get filtered depth reading at a specific location
    /// (Adapted from your original code)
    /// </summary>
    unsafe float GetFilteredDepth(XRCpuImage depthImage, int centerX, int centerY)
    {
        var plane = depthImage.GetPlane(0);
        byte* dataPtr = (byte*)plane.data.GetUnsafePtr();

        List<float> validReadings = new List<float>();

        for (int y = centerY - sampleRadius; y <= centerY + sampleRadius; y++)
        {
            for (int x = centerX - sampleRadius; x <= centerX + sampleRadius; x++)
            {
                if (x < 0 || x >= depthImage.width || y < 0 || y >= depthImage.height)
                    continue;

                int index = (y * plane.rowStride) + (x * plane.pixelStride);
                float depthInMeters = 0f;

                if (depthImage.format == XRCpuImage.Format.DepthUint16)
                {
                    ushort depthRaw = *(ushort*)(dataPtr + index);
                    depthInMeters = depthRaw / 1000f;
                }
                else if (depthImage.format == XRCpuImage.Format.DepthFloat32)
                {
                    depthInMeters = *(float*)(dataPtr + index);
                }

                // Filter valid interaction distances
                if (depthInMeters > minDepth && depthInMeters < maxDepth)
                {
                    validReadings.Add(depthInMeters);
                }
            }
        }

        if (validReadings.Count == 0) return 0f;

        // Return median for robustness
        validReadings.Sort();
        return validReadings[validReadings.Count / 2];
    }

    /// <summary>
    /// Place object at the nearest detected object
    /// </summary>
    public void PlaceOnNearestObject()
    {
        if (lastScanResult == null || !lastScanResult.hasValidData)
        {
            Debug.LogWarning("No valid scan data available");
            return;
        }

        if (objectPlacer == null)
        {
            Debug.LogError("ARObjectPlacer not assigned!");
            return;
        }

        Vector2 targetPos = lastScanResult.nearestCell.screenCenter;
        Debug.Log($"Placing at nearest object: {targetPos} ({lastScanResult.nearestCell.distance:F2}m away)");

        objectPlacer.SimulateTapAt(targetPos.x, targetPos.y);
    }

    /// <summary>
    /// Manual trigger for placement (call from button or gesture)
    /// </summary>
    public void TriggerPlacement()
    {
        PlaceOnNearestObject();
    }

    // Debug visualization
    void OnGUI()
    {
        if (!showDebugOverlay || lastScanResult == null) return;

        GUIStyle style = new GUIStyle();
        style.fontSize = 20;
        style.normal.textColor = Color.white;

        // Draw grid cells
        for (int i = 0; i < lastScanResult.cells.Length; i++)
        {
            GridCell cell = lastScanResult.cells[i];

            if (cell.isValid)
            {
                // Draw distance at cell center
                bool isNearest = Mathf.Approximately(cell.distance, lastScanResult.nearestCell.distance);
                style.normal.textColor = isNearest ? Color.green : Color.white;

                GUI.Label(
                    new Rect(cell.screenCenter.x - 30, cell.screenCenter.y - 10, 60, 25),
                    $"{cell.distance:F2}m",
                    style
                );

                // Draw crosshair
                DrawCrosshair(cell.screenCenter, isNearest ? Color.green : Color.yellow);
            }
        }

        // Draw info
        style.normal.textColor = Color.yellow;
        style.fontSize = 25;
        GUI.Label(new Rect(10, 10, 400, 35),
            $"Valid Cells: {lastScanResult.validCellCount}/{lastScanResult.cells.Length}",
            style);

        if (lastScanResult.hasValidData)
        {
            style.normal.textColor = Color.green;
            GUI.Label(new Rect(10, 50, 400, 35),
                $"Nearest: {lastScanResult.nearestCell.distance:F2}m",
                style);
        }
    }

    void DrawCrosshair(Vector2 center, Color color)
    {
        Texture2D tex = new Texture2D(1, 1);
        tex.SetPixel(0, 0, color);
        tex.Apply();

        int size = 20;
        int thickness = 2;

        // Horizontal line
        GUI.DrawTexture(new Rect(center.x - size, center.y - thickness / 2, size * 2, thickness), tex);
        // Vertical line
        GUI.DrawTexture(new Rect(center.x - thickness / 2, center.y - size, thickness, size * 2), tex);

        Destroy(tex);
    }
}