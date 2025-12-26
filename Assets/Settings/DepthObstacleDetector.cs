using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Collections;

[RequireComponent(typeof(AROcclusionManager))]
public class DepthObstacleDetector : MonoBehaviour
{
    [Header("Detection Settings")]
    public float detectionInterval = 0.15f;
    public float maxDepth = 4.0f;
    public float minDepth = 0.2f;

    [Header("Audio Settings (3D Spatial)")]
    public AudioClip warningClip;
    public AnimationCurve pitchCurve = AnimationCurve.Linear(0.2f, 2.0f, 4.0f, 0.5f);
    public float maxBeepInterval = 1.0f;
    public float minBeepInterval = 0.05f;

    [Header("Visual Debugging")]
    public GameObject debugCubePrefab;

    [Header("Floor Detection")]
    public ARPlaneManager planeManager;
    public float floorYThreshold = 0.15f;

    [Header("Sampling Grid")]
    public int gridWidth = 15;
    public int gridHeight = 10;
    [Range(0f, 1f)] public float topIgnore = 0.2f;
    [Range(0f, 1f)] public float bottomIgnore = 0.3f;

    // Live State
    private float nearestDistance;
    private Vector3 nearestWorldPosition;
    private bool obstacleDetected;

    // Internal Systems
    private AROcclusionManager occlusionManager;
    private Camera arCamera;
    private float lastDetectionTime = 0f;
    private float nextBeepTime;
    private float detectedFloorY = float.MinValue;

    // Objects created by script
    private GameObject spawnedVisualMarker;
    private MeshRenderer markerRenderer;
    private GameObject audioEmitter;
    private AudioSource generatedAudioSource;

    void Awake()
    {
        occlusionManager = GetComponent<AROcclusionManager>();
        arCamera = Camera.main;
        if (planeManager == null) planeManager = FindObjectOfType<ARPlaneManager>();
    }

    void Start()
    {
        CreateAudioObject();
    }

    void CreateAudioObject()
    {
        // 1. Create the invisible Audio GameObject
        audioEmitter = new GameObject("DynamicAudioEmitter");
        generatedAudioSource = audioEmitter.AddComponent<AudioSource>();

        generatedAudioSource.clip = warningClip;
        generatedAudioSource.playOnAwake = false;
        generatedAudioSource.loop = false; // We trigger beeps manually

        // --- 3D CONFIGURATION ---
        generatedAudioSource.spatialBlend = 1.0f; // 1.0 = Fully 3D (Unity handles panning)
        generatedAudioSource.dopplerLevel = 0.0f; // Disable pitch shift when moving fast
        generatedAudioSource.spread = 0.0f;       // 0 = Sound comes from a specific point

        // Automatic Volume Rolloff (Unity handles fading)
        generatedAudioSource.rolloffMode = AudioRolloffMode.Logarithmic;
        generatedAudioSource.minDistance = 0.5f;   // Distance where sound is loudest
        generatedAudioSource.maxDistance = maxDepth; // Distance where sound becomes quiet
    }

    void OnEnable()
    {
        occlusionManager.requestedEnvironmentDepthMode = EnvironmentDepthMode.Medium;
    }

    void Update()
    {
        // 1. Run Detection
        if (Time.time >= lastDetectionTime + detectionInterval)
        {
            if (occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
            {
                lastDetectionTime = Time.time;
                UpdateFloorHeight();
                using (depthImage) { ProcessDepth(depthImage); }
            }
        }

        // 2. Update Visuals
        UpdateVisualMarker();

        // 3. Update Audio
        UpdateAudio();
    }

    void UpdateAudio()
    {
        if (generatedAudioSource == null || warningClip == null) return;
        if (!obstacleDetected) return;

        // --- STEP 1: POSITIONING ---
        // We simply move the audio object to the obstacle. 
        // Because spatialBlend is 1.0, Unity automatically calculates panning 
        // based on the AR Camera's rotation relative to this position.
        audioEmitter.transform.position = nearestWorldPosition;

        // --- STEP 2: DYNAMIC BEEPING ---
        float dist = Vector3.Distance(arCamera.transform.position, nearestWorldPosition);

        // Pitch effect
        generatedAudioSource.pitch = pitchCurve.Evaluate(dist);

        // Beep Interval effect
        float proximityPercent = Mathf.InverseLerp(maxDepth, minDepth, dist);
        float currentInterval = Mathf.Lerp(maxBeepInterval, minBeepInterval, proximityPercent);

        if (Time.time >= nextBeepTime)
        {
            generatedAudioSource.PlayOneShot(warningClip);
            nextBeepTime = Time.time + currentInterval;
        }
    }

    void UpdateVisualMarker()
    {
        if (spawnedVisualMarker == null && debugCubePrefab != null)
        {
            spawnedVisualMarker = Instantiate(debugCubePrefab);
            spawnedVisualMarker.transform.localScale = Vector3.one * 0.15f;
            markerRenderer = spawnedVisualMarker.GetComponent<MeshRenderer>();
            var oldAudio = spawnedVisualMarker.GetComponent<AudioSource>();
            if (oldAudio) Destroy(oldAudio);
        }

        if (spawnedVisualMarker != null)
        {
            if (obstacleDetected)
            {
                if (markerRenderer != null) markerRenderer.enabled = true;
                spawnedVisualMarker.transform.position = Vector3.Lerp(spawnedVisualMarker.transform.position, nearestWorldPosition, Time.deltaTime * 15f);
                spawnedVisualMarker.transform.LookAt(arCamera.transform);
            }
            else
            {
                if (markerRenderer != null) markerRenderer.enabled = false;
            }
        }
    }

    // --- DETECTION LOGIC (UNCHANGED) ---
    void UpdateFloorHeight()
    {
        float lowestY = float.MaxValue;
        bool foundFloor = false;
        foreach (var plane in planeManager.trackables)
        {
            if (plane.alignment == PlaneAlignment.HorizontalUp && plane.transform.position.y < lowestY)
            {
                lowestY = plane.transform.position.y;
                foundFloor = true;
            }
        }
        detectedFloorY = foundFloor ? lowestY : float.MinValue;
    }

    void ProcessDepth(XRCpuImage depthImage)
    {
        float currentNearest = float.MaxValue;
        bool foundSomething = false;
        Vector3 bestWorldPos = Vector3.zero;

        var plane = depthImage.GetPlane(0);
        var data = plane.data.Reinterpret<ushort>(1);
        int rowStride = plane.rowStride / 2;
        int width = depthImage.width;
        int height = depthImage.height;

        for (int y = 0; y < gridHeight; y++)
        {
            for (int x = 0; x < gridWidth; x++)
            {
                float u = (x + 0.5f) / gridWidth;
                float vMapped = Mathf.Lerp(topIgnore, 1f - bottomIgnore, (y + 0.5f) / gridHeight);

                int px = Mathf.FloorToInt(u * (width - 1));
                int py = Mathf.FloorToInt(vMapped * (height - 1));

                int index = py * rowStride + px;
                float depthMeters = data[index] * 0.001f;

                if (depthMeters <= minDepth || depthMeters > maxDepth) continue;
                if (depthMeters >= currentNearest) continue;

                Vector3 candidatePos = DepthPixelToWorld(u, vMapped, depthMeters);

                if (detectedFloorY > float.MinValue && Mathf.Abs(candidatePos.y - detectedFloorY) < floorYThreshold)
                    continue;

                currentNearest = depthMeters;
                bestWorldPos = candidatePos;
                foundSomething = true;
            }
        }

        obstacleDetected = foundSomething;
        if (foundSomething)
        {
            nearestDistance = currentNearest;
            nearestWorldPosition = bestWorldPos;
        }
    }

    Vector3 DepthPixelToWorld(float u, float v, float depthMeters)
    {
        Ray ray = arCamera.ViewportPointToRay(new Vector3(u, v, 0f));
        return ray.origin + (ray.direction * depthMeters);
    }
}