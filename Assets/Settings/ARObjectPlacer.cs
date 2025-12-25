using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.InputSystem;



public class ARObjectPlacer : MonoBehaviour
{
    [Header("Placement Settings")]
    [SerializeField] private GameObject objectToPlace;

    [Header("Placement Mode")]
    [Tooltip("What surfaces to detect")]
    public PlacementMode placementMode = PlacementMode.DepthAndPlanes;

    // References
    private ARRaycastManager raycastManager;
    private AROcclusionManager occlusionManager;
    private GameObject placedObject;
    private List<ARRaycastHit> hits = new List<ARRaycastHit>();

    // Debug
    private int tapCount = 0;
    private bool depthSupported = false;
    private string statusMessage = "";

    public enum PlacementMode
    {
        PlanesOnly,           // Only flat surfaces (floors, tables, walls)
        DepthOnly,            // Anywhere depth is detected (people, poles, etc)
        DepthAndPlanes,       // Try depth first, fallback to planes
        FeaturePoints,        // Tracked feature points
        Everything            // Try all methods
    }

    void Start()
    {
        Debug.Log("=== ARObjectPlacer Started ===");

        // Get components
        raycastManager = GetComponent<ARRaycastManager>();
        occlusionManager = GetComponent<AROcclusionManager>();

        // Check components
        if (raycastManager == null)
        {
            Debug.LogError("❌ ARRaycastManager NOT FOUND!");
            statusMessage = "Error: No RaycastManager";
            return;
        }

        if (objectToPlace == null)
        {
            Debug.LogError("❌ Object to place NOT ASSIGNED!");
            statusMessage = "Error: No object assigned";
            return;
        }

        // Check depth support
        CheckDepthSupport();

        Debug.Log($"✓ Setup complete - Mode: {placementMode}");
        statusMessage = "Ready to tap!";
    }

    void CheckDepthSupport()
    {
        // Check if device supports depth
        if (occlusionManager != null)
        {
            depthSupported = true;
            Debug.Log("✓ Depth API is SUPPORTED on this device!");
            statusMessage = "Depth supported!";
        }
        else
        {
            depthSupported = false;
            Debug.LogWarning("⚠️ Depth API NOT supported on this device");
            Debug.LogWarning("   Will use planes and feature points instead");
            statusMessage = "No depth - using planes";

            // Auto-switch to planes if depth not supported
            if (placementMode == PlacementMode.DepthOnly)
            {
                placementMode = PlacementMode.PlanesOnly;
                Debug.LogWarning("   Switched to PlanesOnly mode");
            }
        }
    }

    void Update()
    {
        // NEW INPUT SYSTEM - Touchscreen
        if (Touchscreen.current != null)
        {
            if (Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
            {
                tapCount++;
                Vector2 touchPosition = Touchscreen.current.primaryTouch.position.ReadValue();
                Debug.Log($"📱 TAP #{tapCount} at: {touchPosition}");
                TryPlaceObject(touchPosition);
                return;
            }
        }

        // FALLBACK - Mouse for Editor
        if (Mouse.current != null)
        {
            if (Mouse.current.leftButton.wasPressedThisFrame)
            {
                tapCount++;
                Vector2 mousePosition = Mouse.current.position.ReadValue();
                Debug.Log($"🖱️ CLICK #{tapCount} at: {mousePosition}");
                TryPlaceObject(mousePosition);
                return;
            }
        }
    }

    void TryPlaceObject(Vector2 screenPosition)
    {
        Debug.Log($"→ Mode: {placementMode}");
        hits.Clear();
        bool didHit = false;

        switch (placementMode)
        {
            case PlacementMode.PlanesOnly:
                didHit = TryPlaceOnPlanes(screenPosition);
                break;

            case PlacementMode.DepthOnly:
                didHit = TryPlaceOnDepth(screenPosition);
                break;

            case PlacementMode.DepthAndPlanes:
                // Try depth first (for irregular surfaces)
                didHit = TryPlaceOnDepth(screenPosition);
                // If depth fails, try planes
                if (!didHit)
                {
                    Debug.Log("   Depth missed, trying planes...");
                    didHit = TryPlaceOnPlanes(screenPosition);
                }
                break;

            case PlacementMode.FeaturePoints:
                didHit = TryPlaceOnFeaturePoints(screenPosition);
                break;

            case PlacementMode.Everything:
                // Try in order: Depth -> Planes -> Feature Points
                didHit = TryPlaceOnDepth(screenPosition);
                if (!didHit) didHit = TryPlaceOnPlanes(screenPosition);
                if (!didHit) didHit = TryPlaceOnFeaturePoints(screenPosition);
                break;
        }

        if (!didHit)
        {
            Debug.LogWarning("❌ Nothing detected at tap location");
            statusMessage = "Tap missed - try different spot";
        }
    }

    /// <summary>
    /// Simulate a tap at specific screen coordinates
    /// </summary>
    /// <param name="x">Screen X coordinate (pixels)</param>
    /// <param name="y">Screen Y coordinate (pixels)</param>
    public void SimulateTapAt(float x, float y)
    {
        tapCount++;
        Vector2 screenPosition = new Vector2(x, y);
        Debug.Log($"SIMULATED TAP #{tapCount} at: {screenPosition}");
        TryPlaceObject(screenPosition);
    }

    bool TryPlaceOnDepth(Vector2 screenPosition)
    {
        if (!depthSupported)
        {
            Debug.Log("   Depth not supported, skipping");
            return false;
        }

        hits.Clear();
        bool didHit = raycastManager.Raycast(
            screenPosition,
            hits,
            TrackableType.Depth
        );

        if (didHit && hits.Count > 0)
        {
            Debug.Log($"✓ HIT DEPTH! Distance: {hits[0].distance}m");
            PlaceObjectAtHit(hits[0], "Depth");
            return true;
        }

        return false;
    }

    bool TryPlaceOnPlanes(Vector2 screenPosition)
    {
        hits.Clear();
        bool didHit = raycastManager.Raycast(
            screenPosition,
            hits,
            TrackableType.PlaneWithinPolygon
        );

        if (didHit && hits.Count > 0)
        {
            Debug.Log($"✓ HIT PLANE! Distance: {hits[0].distance}m");
            PlaceObjectAtHit(hits[0], "Plane");
            return true;
        }

        return false;
    }

    bool TryPlaceOnFeaturePoints(Vector2 screenPosition)
    {
        hits.Clear();
        bool didHit = raycastManager.Raycast(
            screenPosition,
            hits,
            TrackableType.FeaturePoint
        );

        if (didHit && hits.Count > 0)
        {
            Debug.Log($"✓ HIT FEATURE POINT! Distance: {hits[0].distance}m");
            PlaceObjectAtHit(hits[0], "Feature Point");
            return true;
        }

        return false;
    }

    void PlaceObjectAtHit(ARRaycastHit hit, string hitType)
    {
        if (objectToPlace == null) return;

        // Remove previous object
        if (placedObject != null)
        {
            Destroy(placedObject);
        }

        // Create object at hit position
        placedObject = Instantiate(
            objectToPlace,
            hit.pose.position,
            hit.pose.rotation
        );

        // Make it visible
        placedObject.transform.localScale = Vector3.one * 0.3f;

        Debug.Log($"✓✓✓ PLACED on {hitType}! Position: {hit.pose.position}");
        statusMessage = $"Placed on {hitType}!";
    }

    // On-screen debug display
    void OnGUI()
    {
        GUIStyle style = new GUIStyle();
        style.fontSize = 25;
        style.normal.textColor = Color.white;
        style.normal.background = MakeTex(2, 2, new Color(0, 0, 0, 0.5f));

        GUI.Label(new Rect(10, 10, 600, 35), $"Mode: {placementMode}", style);
        GUI.Label(new Rect(10, 50, 600, 35), $"Depth: {(depthSupported ? "✓ YES" : "✗ NO")}", style);
        GUI.Label(new Rect(10, 90, 600, 35), $"Taps: {tapCount}", style);
        GUI.Label(new Rect(10, 130, 600, 35), $"Status: {statusMessage}", style);

        if (placedObject != null)
        {
            style.normal.textColor = Color.green;
            GUI.Label(new Rect(10, 170, 600, 35), "Object: PLACED ✓", style);
        }
    }

    // Helper to create colored texture
    private Texture2D MakeTex(int width, int height, Color col)
    {
        Color[] pix = new Color[width * height];
        for (int i = 0; i < pix.Length; i++)
            pix[i] = col;

        Texture2D result = new Texture2D(width, height);
        result.SetPixels(pix);
        result.Apply();
        return result;
    }
}