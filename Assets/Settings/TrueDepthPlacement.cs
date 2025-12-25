using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections.LowLevel.Unsafe;
using TMPro;

[RequireComponent(typeof(AROcclusionManager))]
public class TrueDepthPlacement : MonoBehaviour
{
    [Header("Setup")]
    public Camera arCamera;
    public GameObject cubePrefab;
    public TMP_Text debugText;

    [Header("Settings")]
    [Range(0.1f, 10.0f)]
    public float maxDistance = 4.0f;

    [Range(1.0f, 10.0f)]
    public float scanInterval = 5.0f; // Scan every 5 seconds

    private AROcclusionManager occlusionManager;
    private GameObject spawnedCube;
    private float nextScanTime = 0f;

    // Store last scan results
    private Vector3 lastWorldPos = Vector3.zero;
    private float lastDistance = 0f;
    private bool hasValidScan = false;

    void Awake()
    {
        occlusionManager = GetComponent<AROcclusionManager>();

        if (cubePrefab != null)
        {
            spawnedCube = Instantiate(cubePrefab);
            spawnedCube.SetActive(false);
        }

        // Verify environment depth is supported
        if (occlusionManager != null)
        {
            Debug.Log($"Environment Depth Mode: {occlusionManager.requestedEnvironmentDepthMode}");
        }
    }

    void Start()
    {
        // Ensure environment depth mode is set
        if (occlusionManager != null)
        {
            occlusionManager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Best;
            Debug.Log("Environment Depth Mode set to: Best");
        }

        // Perform first scan immediately
        nextScanTime = Time.time;
    }

    void Update()
    {
        if (occlusionManager == null || arCamera == null || spawnedCube == null)
        {
            UpdateDebugText("Missing components!", Vector3.zero, 0f, false);
            return;
        }

        // Check if it's time to scan
        if (Time.time >= nextScanTime)
        {
            PerformDepthScan();
            nextScanTime = Time.time + scanInterval;
        }

        // Update visualization with last scan results
        if (hasValidScan)
        {
            spawnedCube.transform.position = lastWorldPos;
            spawnedCube.SetActive(lastDistance <= maxDistance);
            UpdateDebugText("Scan Complete", lastWorldPos, lastDistance, true);
        }
        else
        {
            spawnedCube.SetActive(false);
        }
    }

    void PerformDepthScan()
    {
        // Try to acquire environment depth image
        if (!occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
        {
            UpdateDebugText("Acquiring Environment Depth...", Vector3.zero, 0f, false);
            hasValidScan = false;
            return;
        }

        using (depthImage)
        {
            // Log depth image info for verification
            Debug.Log($"Depth Image - Width: {depthImage.width}, Height: {depthImage.height}, Format: {depthImage.format}");

            Vector3 worldPos = GetWorldPosFromDepth(depthImage);

            if (worldPos != Vector3.zero)
            {
                float distance = Vector3.Distance(arCamera.transform.position, worldPos);

                // Store scan results
                lastWorldPos = worldPos;
                lastDistance = distance;
                hasValidScan = true;

                Debug.Log($"Depth Scan - Distance: {distance:F2}m, Position: {worldPos}");
            }
            else
            {
                UpdateDebugText("No valid depth at center", Vector3.zero, 0f, false);
                hasValidScan = false;
            }
        }
    }

    Vector3 GetWorldPosFromDepth(XRCpuImage image)
    {
        // Verify we have a valid plane
        if (image.planeCount == 0)
        {
            Debug.LogWarning("Depth image has no planes!");
            return Vector3.zero;
        }

        var plane = image.GetPlane(0);
        var data = plane.data;

        // Sample from center of depth image
        int x = image.width / 2;
        int y = image.height / 2;

        float depthMeters = 0;

        unsafe
        {
            float* ptr = (float*)data.GetUnsafeReadOnlyPtr();
            depthMeters = ptr[y * image.width + x];
        }

        // Validate depth value
        if (depthMeters <= 0.1f || float.IsNaN(depthMeters) || float.IsInfinity(depthMeters))
        {
            Debug.LogWarning($"Invalid depth value: {depthMeters}");
            return Vector3.zero;
        }

        // Convert screen center + depth to world coordinates
        Vector3 screenPoint = new Vector3(Screen.width / 2f, Screen.height / 2f, depthMeters);
        Vector3 worldPos = arCamera.ScreenToWorldPoint(screenPoint);

        return worldPos;
    }

    void UpdateDebugText(string status, Vector3 pos, float dist, bool isValid)
    {
        if (debugText == null) return;

        float timeUntilNextScan = Mathf.Max(0, nextScanTime - Time.time);

        if (isValid)
        {
            string rangeStatus = dist <= maxDistance
                ? "<color=green>IN RANGE</color>"
                : "<color=red>TOO FAR</color>";

            debugText.text = $"<b>ENVIRONMENT DEPTH SCAN</b>\n" +
                           $"{rangeStatus}\n" +
                           $"Distance: {dist:F2}m\n" +
                           $"X: {pos.x:F2}\n" +
                           $"Y: {pos.y:F2}\n" +
                           $"Z: {pos.z:F2}\n" +
                           $"Next scan in: {timeUntilNextScan:F1}s";
        }
        else
        {
            debugText.text = $"<b>ENVIRONMENT DEPTH SCAN</b>\n" +
                           $"<color=yellow>{status}</color>\n" +
                           $"Next scan in: {timeUntilNextScan:F1}s\n\n" +
                           $"<size=14>Point camera at a surface\n" +
                           $"Depth Mode: {occlusionManager.currentEnvironmentDepthMode}</size>";
        }
    }

    // Optional: Manual scan trigger (call this from a button or other script)
    public void TriggerManualScan()
    {
        nextScanTime = Time.time; // Force immediate scan
        Debug.Log("Manual depth scan triggered");
    }

    // Get the last scan results (useful for other scripts)
    public bool TryGetLastScanResults(out Vector3 position, out float distance)
    {
        position = lastWorldPos;
        distance = lastDistance;
        return hasValidScan;
    }
}