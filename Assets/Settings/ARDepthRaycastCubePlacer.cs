using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System.Collections.Generic;
using System.Linq;

public class ARDepthRaycastCubePlacer : MonoBehaviour
{
    [Header("Placement")]
    [SerializeField] private GameObject objectToPlace;
    [SerializeField] private float updateInterval = 0.25f;
    [SerializeField] private bool autoPlace = true;

    [Header("Depth Sampling")]
    [SerializeField] private int depthStep = 4;
    [SerializeField] private int topN = 30;
    [SerializeField] private float minDepth = 0.2f;
    [SerializeField] private float maxDepth = 5.0f;

    [Header("Filtering")]
    [SerializeField] private float smoothingFactor = 0.85f;
    [SerializeField] private float verticalIgnoreDot = 0.75f;
    [SerializeField] private float minMovementThreshold = 0.05f;

    [Header("Debug")]
    [SerializeField] private bool showDebugInfo = true;
    [SerializeField] private bool drawDebugLine = true;
    [SerializeField] private bool verboseLogging = false;

    private AROcclusionManager occlusionManager;
    private Camera arCamera;
    private GameObject placedObject;

    private Vector3 smoothedPosition;
    private bool hasSmoothedPosition;
    private float timer;

    private int depthWidth;
    private int depthHeight;

    private int updateAttempts;
    private int successfulUpdates;
    private int depthFailures;
    private int noValidPixels;
    private int floorRejects;

    void Start()
    {
        occlusionManager = FindObjectOfType<AROcclusionManager>();
        arCamera = Camera.main;

        if (!occlusionManager || !arCamera || !objectToPlace)
        {
            Debug.LogError("❌ Missing required AR components");
            enabled = false;
            return;
        }

        occlusionManager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Best;
        occlusionManager.environmentDepthTemporalSmoothingRequested = true;

        StartCoroutine(DelayedDepthCheck());
    }

    System.Collections.IEnumerator DelayedDepthCheck()
    {
        yield return new WaitForSeconds(1f);

        if (occlusionManager.subsystem == null)
        {
            Debug.LogError("❌ Occlusion subsystem not running");
            yield break;
        }

        Debug.Log($"Requested depth mode: {occlusionManager.requestedEnvironmentDepthMode}");
        Debug.Log($"Current depth mode: {occlusionManager.currentEnvironmentDepthMode}");
    }

    void Update()
    {
        if (!autoPlace) return;

        timer += Time.deltaTime;
        if (timer >= updateInterval)
        {
            timer = 0f;
            UpdateNearestObstacle();
        }

        if (drawDebugLine && hasSmoothedPosition)
        {
            Debug.DrawLine(
                arCamera.transform.position,
                smoothedPosition,
                Color.cyan,
                0.1f
            );
        }
    }

    void UpdateNearestObstacle()
    {
        updateAttempts++;

        if (occlusionManager.subsystem == null ||
            !occlusionManager.subsystem.running)
            return;

        if (!occlusionManager.TryAcquireEnvironmentDepthCpuImage(
            out XRCpuImage depthImage))
        {
            depthFailures++;
            return;
        }

        using (depthImage)
        {
            depthWidth = depthImage.width;
            depthHeight = depthImage.height;

            if (!TryGetMedianDepthPixel(
                depthImage,
                out Vector2 pixel,
                out float depth))
            {
                noValidPixels++;
                return;
            }

            Vector3 worldPos =
                DepthPixelToWorld(pixel, depth, depthImage);

            if (IsFloorOrCeiling(worldPos))
            {
                floorRejects++;
                return;
            }

            if (hasSmoothedPosition &&
                Vector3.Distance(worldPos, smoothedPosition)
                < minMovementThreshold)
                return;

            ApplySmoothedPlacement(worldPos);
            successfulUpdates++;
        }
    }

    bool TryGetMedianDepthPixel(
        XRCpuImage image,
        out Vector2 medianPixel,
        out float medianDepth)
    {
        medianPixel = Vector2.zero;
        medianDepth = -1f;

        var conversion = new XRCpuImage.ConversionParams
        {
            inputRect = new RectInt(0, 0, image.width, image.height),
            outputDimensions =
                new Vector2Int(image.width, image.height),
            outputFormat = TextureFormat.RFloat,
            transformation = XRCpuImage.Transformation.None
        };

        int size = image.GetConvertedDataSize(conversion);
        var buffer = new NativeArray<byte>(size, Allocator.Temp);

        try
        {
            image.Convert(conversion, buffer);
        }
        catch
        {
            buffer.Dispose();
            return false;
        }

        List<(float d, Vector2 p)> candidates =
            new List<(float, Vector2)>(topN * 2);

        unsafe
        {
            float* depthData =
                (float*)buffer.GetUnsafeReadOnlyPtr();

            for (int y = 0; y < image.height; y += depthStep)
            {
                for (int x = 0; x < image.width; x += depthStep)
                {
                    int idx = y * image.width + x;
                    float d = depthData[idx];

                    if (float.IsNaN(d) || d <= 0f) continue;
                    if (d < minDepth || d > maxDepth) continue;

                    candidates.Add((d, new Vector2(x, y)));
                }
            }
        }

        buffer.Dispose();

        if (candidates.Count == 0)
            return false;

        var closest = candidates
            .OrderBy(c => c.d)
            .Take(topN)
            .OrderBy(c => c.d)
            .ToList();

        int mid = closest.Count / 2;
        medianDepth = closest[mid].d;
        medianPixel = closest[mid].p;

        return true;
    }

    Vector3 DepthPixelToWorld(
        Vector2 pixel,
        float depth,
        XRCpuImage image)
    {
        Vector2 viewport = new Vector2(
            pixel.x / image.width,
            pixel.y / image.height
        );

        Ray ray = arCamera.ViewportPointToRay(viewport);
        return ray.origin + ray.direction * depth;
    }

    bool IsFloorOrCeiling(Vector3 worldPos)
    {
        Vector3 dir =
            (worldPos - arCamera.transform.position).normalized;
        return Mathf.Abs(Vector3.Dot(dir, Vector3.up))
               > verticalIgnoreDot;
    }

    void ApplySmoothedPlacement(Vector3 target)
    {
        if (!hasSmoothedPosition)
        {
            smoothedPosition = target;
            hasSmoothedPosition = true;
        }
        else
        {
            smoothedPosition = Vector3.Lerp(
                target,
                smoothedPosition,
                smoothingFactor
            );
        }

        if (!placedObject)
        {
            placedObject = Instantiate(
                objectToPlace,
                smoothedPosition,
                Quaternion.identity
            );
            placedObject.transform.localScale =
                Vector3.one * 0.3f;
        }
        else
        {
            placedObject.transform.position = smoothedPosition;
        }
    }

    void OnGUI()
    {
        if (!showDebugInfo) return;

        bool depthActive =
            occlusionManager.subsystem != null &&
            occlusionManager.currentEnvironmentDepthMode
            != EnvironmentDepthMode.Disabled;

        GUI.Label(
            new Rect(10, 10, 600, 30),
            depthActive ? "✅ Depth ACTIVE" : "❌ Depth NOT AVAILABLE"
        );

        GUI.Label(
            new Rect(10, 40, 600, 30),
            hasSmoothedPosition
                ? $"Obstacle @ {smoothedPosition}"
                : "Searching for obstacle..."
        );
    }
}
