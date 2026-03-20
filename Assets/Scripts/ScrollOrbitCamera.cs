using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Orbits the camera around a focus point from mouse wheel / trackpad scroll.
/// Uses <see cref="InputSystem.onAfterUpdate"/> so scroll is read after the Input System applies wheel deltas
/// (early <c>Update</c> often sees zero because <c>Mouse</c> resets/applies scroll in the input update).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class ScrollOrbitCamera : MonoBehaviour
{
    [SerializeField] Vector3 focusPoint = Vector3.zero;
    [Tooltip("Degrees applied per unit of scroll delta (mouse wheel steps are often around ±120).")]
    [SerializeField] float scrollSensitivity = 1f;

    int _lastOrbitFrame = -1;

    void OnEnable()
    {
        InputSystem.onAfterUpdate += OnAfterInputUpdate;
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

        float s = scrollSensitivity;
        transform.RotateAround(focusPoint, Vector3.up, scroll.x * s);
        transform.RotateAround(focusPoint, transform.right, -scroll.y * s);
        _lastOrbitFrame = Time.frameCount;
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
}
