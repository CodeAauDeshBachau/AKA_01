using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections.LowLevel.Unsafe;
using System.Collections.Generic;

public class ARDepthEstimator : MonoBehaviour
{
    [Header("AR Components")]
    [SerializeField] private AROcclusionManager occlusionManager;

    [Header("UI Output")]
    [SerializeField] private Text distanceText;
    [SerializeField] private Text debugInfoText; // Optional: helps debug resolution/format

    [Header("Tuning")]
    [SerializeField] private float measurementInterval = 0.5f; // Faster updates for testing
    // Lower radius for non-sensor phones (Depth map is low res, 160x120)
    [SerializeField] private int sampleRadius = 2;

    private float timer = 0f;

    void Start()
    {
        if (occlusionManager == null)
            occlusionManager = FindObjectOfType<AROcclusionManager>();

        // IMPORTANT: "Fastest" is bad for non-sensor phones. 
        // "Medium" or "Best" gives the AI more time to resolve distance.
        if (occlusionManager != null)
        {
            occlusionManager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Medium;
        }
    }

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= measurementInterval)
        {
            MeasureCenterDepth();
            timer = 0f;
        }
    }

    void MeasureCenterDepth()
    {
        if (occlusionManager == null ||
            !occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
        {
            return;
        }

        using (depthImage)
        {
            // 1. Target the DEAD CENTER of the raw image directly
            // This ignores screen aspect ratio/cropping issues.
            int centerX = depthImage.width / 2;
            int centerY = depthImage.height / 2;

            // 2. Get the depth
            float distance = GetFilteredDepth(depthImage, centerX, centerY);

            // 3. Update UI
            if (distance > 0)
            {
                distanceText.text = $"{distance:F2}m";
                distanceText.color = Color.green;
            }
            else
            {
                distanceText.text = "Scanning..."; // 0 usually means "Uncertain"
                distanceText.color = Color.yellow;
            }

            // Debug info to verify we are getting data
            if (debugInfoText != null)
                debugInfoText.text = $"Map: {depthImage.width}x{depthImage.height} | Format: {depthImage.format}";
        }
    }

    unsafe float GetFilteredDepth(XRCpuImage depthImage, int centerX, int centerY)
    {
        var plane = depthImage.GetPlane(0);
        byte* dataPtr = (byte*)plane.data.GetUnsafePtr();

        // We will collect valid samples in a list to find the Median
        // Average is bad because one "0" reading ruins the accuracy.
        List<float> validReadings = new List<float>();

        for (int y = centerY - sampleRadius; y <= centerY + sampleRadius; y++)
        {
            for (int x = centerX - sampleRadius; x <= centerX + sampleRadius; x++)
            {
                // Boundary check
                if (x < 0 || x >= depthImage.width || y < 0 || y >= depthImage.height)
                    continue;

                // Calculate Index
                int index = (y * plane.rowStride) + (x * plane.pixelStride);
                float depthInMeters = 0f;

                // Read Data based on format
                if (depthImage.format == XRCpuImage.Format.DepthUint16)
                {
                    // ARCore usually uses this. Value is millimeters.
                    ushort depthRaw = *(ushort*)(dataPtr + index);
                    depthInMeters = depthRaw / 1000f;
                }
                else if (depthImage.format == XRCpuImage.Format.DepthFloat32)
                {
                    depthInMeters = *(float*)(dataPtr + index);
                }

                // Filter: AI Depth often returns 0 or >20m when it's confused.
                // We only want reasonable "Interaction" distances (e.g., 0.2m to 5m)
                if (depthInMeters > 0.2f && depthInMeters < 8.0f)
                {
                    validReadings.Add(depthInMeters);
                }
            }
        }

        if (validReadings.Count == 0) return 0f;

        // RETURN MEDIAN (More accurate than average for depth)
        validReadings.Sort();
        return validReadings[validReadings.Count / 2];
    }
}