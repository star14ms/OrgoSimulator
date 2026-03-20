using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Orbits the camera around a focus point from mouse wheel / trackpad scroll.
/// Uses <see cref="InputSystem.onAfterUpdate"/> so scroll is read after the Input System applies wheel deltas
/// (early <c>Update</c> often sees zero because <c>Mouse</c> resets/applies scroll in the input update).
/// The <see cref="MoleculeWorkPlane"/> is synced on enable and once after scroll input goes idle (not every tick).
/// When a new scroll gesture starts (after idle), the orbit pivot snaps to <see cref="EditModeManager.SelectedAtom"/>
/// if any; with no selection it resets to the focus point from the inspector at load. Clicking an atom alone does not move the pivot until the user scrolls.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class ScrollOrbitCamera : MonoBehaviour
{
    [SerializeField] Vector3 focusPoint = Vector3.zero;
    [Tooltip("Degrees applied per unit of scroll delta (mouse wheel steps are often around ±120).")]
    [SerializeField] float scrollSensitivity = 1f;
    [Tooltip("Perspective: min/max distance along camera forward for <see cref=\"MoleculeWorkPlane\"/> after orbit (focus projected onto view axis, clamped).")]
    [SerializeField] float moleculeWorkPlaneDepthMin = 4f;
    [SerializeField] float moleculeWorkPlaneDepthMax = 42f;
    [Tooltip("After orbit scroll stops, wait this long (unscaled seconds) then update the work plane once. Avoids work every scroll tick.")]
    [SerializeField] float moleculeWorkPlaneSyncAfterScrollEndDelay = 0.15f;

    int _lastOrbitFrame = -1;
    float _orbitScrollLastActivityUnscaledTime = -1f;
    Camera _cam;
    /// <summary><see cref="focusPoint"/> as serialized when this component awakens (orbit origin when nothing is selected).</summary>
    Vector3 _focusPointInitial;

    void Awake() => _focusPointInitial = focusPoint;

    void OnEnable()
    {
        _cam = GetComponent<Camera>();
        InputSystem.onAfterUpdate += OnAfterInputUpdate;
        SyncMoleculeWorkPlaneToView();
    }

    void OnDisable()
    {
        InputSystem.onAfterUpdate -= OnAfterInputUpdate;
    }

    void OnAfterInputUpdate()
    {
        if (!isActiveAndEnabled) return;

        var mouse = Mouse.current ?? InputSystem.GetDevice<Mouse>();
        Vector2 scroll = mouse != null ? mouse.scroll.ReadValue() : Vector2.zero;

        if (scroll.sqrMagnitude < 1e-6f) return;
        if (Time.frameCount == _lastOrbitFrame) return;

        if (ShouldBlockOrbitBecausePointerIsOverScrollableUi())
            return;

        // New scroll burst (see LateUpdate clearing _orbitScrollLastActivityUnscaledTime after idle).
        if (_orbitScrollLastActivityUnscaledTime < 0f)
            ApplyOrbitFocusForNewScrollGesture();

        float s = scrollSensitivity;
        transform.RotateAround(focusPoint, Vector3.up, scroll.x * s);
        transform.RotateAround(focusPoint, transform.right, -scroll.y * s);
        _lastOrbitFrame = Time.frameCount;
        _orbitScrollLastActivityUnscaledTime = Time.unscaledTime;
    }

    void LateUpdate()
    {
        if (_orbitScrollLastActivityUnscaledTime < 0f) return;
        if (Time.unscaledTime - _orbitScrollLastActivityUnscaledTime < moleculeWorkPlaneSyncAfterScrollEndDelay)
            return;
        _orbitScrollLastActivityUnscaledTime = -1f;
        SyncMoleculeWorkPlaneToView();
    }

    /// <summary>Updates <see cref="MoleculeWorkPlane"/> to face <see cref="Camera.main"/> with depth from <see cref="focusPoint"/>.</summary>
    public void SyncMoleculeWorkPlaneToView()
    {
        var cam = _cam != null ? _cam : GetComponent<Camera>();
        if (cam == null || cam.orthographic) return;
        if (MoleculeWorkPlane.Instance == null) return;
        MoleculeWorkPlane.Instance.SyncToPerspectiveOrbit(cam, focusPoint, moleculeWorkPlaneDepthMin, moleculeWorkPlaneDepthMax);
        Object.FindFirstObjectByType<EditModeManager>()?.RepositionDeselectBackground();
    }

    /// <summary>
    /// Only skip over scrollable UI (e.g. periodic table list), not the whole HUD.
    /// </summary>
    static bool ShouldBlockOrbitBecausePointerIsOverScrollableUi()
    {
        var es = EventSystem.current;
        var mouse = Mouse.current ?? InputSystem.GetDevice<Mouse>();
        if (es == null || mouse == null) return false;

        var ped = new PointerEventData(es) { position = mouse.position.ReadValue() };
        var results = new List<RaycastResult>(8);
        es.RaycastAll(ped, results);
        if (results.Count == 0) return false;

        return results[0].gameObject.GetComponentInParent<ScrollRect>() != null;
    }

    void ApplyOrbitFocusForNewScrollGesture()
    {
        var cam = _cam != null ? _cam : GetComponent<Camera>();
        if (cam != null && cam.orthographic) return;

        var em = Object.FindFirstObjectByType<EditModeManager>();
        var atom = em != null ? em.SelectedAtom : null;
        focusPoint = atom != null ? atom.transform.position : _focusPointInitial;
    }
}
