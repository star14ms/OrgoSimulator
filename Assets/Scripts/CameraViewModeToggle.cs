using UnityEngine;

/// <summary>
/// Switches the main camera between the legacy <b>SampleScene</b> 2D setup (orthographic XY)
/// and the <b>Main3D</b> setup (perspective + orbit). Values match those scenes' Main Cameras.
/// Refreshes bonds and orbital layout after each switch.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class CameraViewModeToggle : MonoBehaviour
{
    /// <summary>Matches <c>Assets/Scenes/SampleScene.unity</c> — Main Camera (legacy 2D).</summary>
    [System.Serializable]
    struct ViewPreset
    {
        public Vector3 position;
        public Vector3 eulerAngles;
        public bool orthographic;
        public float orthographicSize;
        public float fieldOfView;

        /// <summary>SampleScene.unity Main Camera.</summary>
        public static ViewPreset SampleScene2DLegacy() =>
            new ViewPreset
            {
                position = new Vector3(0f, 0f, -10f),
                eulerAngles = Vector3.zero,
                orthographic = true,
                orthographicSize = 5f,
                fieldOfView = 34f
            };

        /// <summary>Main3D.unity Main Camera.</summary>
        public static ViewPreset Main3DScene() =>
            new ViewPreset
            {
                position = new Vector3(0f, 5.5f, -14f),
                eulerAngles = new Vector3(35f, 0f, 0f),
                orthographic = false,
                orthographicSize = 5f,
                fieldOfView = 55f
            };

        public void Apply(Transform camTransform, Camera cam)
        {
            camTransform.SetPositionAndRotation(position, Quaternion.Euler(eulerAngles));
            cam.orthographic = orthographic;
            cam.orthographicSize = orthographicSize;
            cam.fieldOfView = fieldOfView;
        }
    }

    Camera cam;
    ViewPreset presetSampleScene2D;
    ViewPreset presetMain3D;
    bool presetsReady;

    public bool IsPerspective3D => cam != null && !cam.orthographic;

    void Awake()
    {
        cam = GetComponent<Camera>();
    }

    void Start()
    {
        EnsureCanonicalPresets();
        var orbit = GetComponent<ScrollOrbitCamera>();
        if (IsPerspective3D)
        {
            if (orbit == null) orbit = gameObject.AddComponent<ScrollOrbitCamera>();
            orbit.enabled = true;
            orbit.SyncMoleculeWorkPlaneToView();
        }
        else if (orbit != null) orbit.enabled = false;
    }

    void EnsureCanonicalPresets()
    {
        if (presetsReady) return;
        presetSampleScene2D = ViewPreset.SampleScene2DLegacy();
        presetMain3D = ViewPreset.Main3DScene();
        presetsReady = true;
    }

    /// <param name="perspective">true = Main3D scene camera; false = SampleScene legacy 2D.</param>
    public void SetPerspective3D(bool perspective)
    {
        EnsureCanonicalPresets();
        if (!presetsReady || cam == null) return;

        var p = perspective ? presetMain3D : presetSampleScene2D;
        p.Apply(transform, cam);

        if (perspective)
        {
            var orbit = GetComponent<ScrollOrbitCamera>();
            if (orbit == null) orbit = gameObject.AddComponent<ScrollOrbitCamera>();
            orbit.enabled = true;
            orbit.SyncMoleculeWorkPlaneToView();
        }
        else
        {
            var orbit = GetComponent<ScrollOrbitCamera>();
            if (orbit != null) orbit.enabled = false;
        }

        RefreshSceneAfterCameraModeChange();
    }

    public void Toggle() => SetPerspective3D(!IsPerspective3D);

    static void RefreshSceneAfterCameraModeChange()
    {
        foreach (var bond in Object.FindObjectsByType<CovalentBond>(FindObjectsSortMode.None))
        {
            if (bond != null) bond.RefreshLineVisualForCameraMode();
        }

        foreach (var atom in Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None))
        {
            if (atom != null) atom.RedistributeOrbitals();
        }

        AtomFunction.SetupGlobalIgnoreCollisions();
        var edit = Object.FindFirstObjectByType<EditModeManager>();
        edit?.RepositionDeselectBackground();
        edit?.RefreshEditSelectionHighlights();
    }
}
