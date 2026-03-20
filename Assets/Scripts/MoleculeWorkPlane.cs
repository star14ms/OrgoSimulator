using UnityEngine;

/// <summary>
/// Defines the infinite plane used for pointer picking and dragging (2D chemistry board or 3D view of the same XY plane at z = 0).
/// Place one instance in scenes that use a perspective camera; SampleScene can omit it and fall back to the legacy orthographic mapping.
/// </summary>
public class MoleculeWorkPlane : MonoBehaviour
{
    public static MoleculeWorkPlane Instance { get; private set; }

    [SerializeField] Vector3 planeNormal = new Vector3(0f, 0f, 1f);
    [SerializeField] Vector3 planePoint = Vector3.zero;

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public bool TryGetWorldPoint(Camera cam, Vector2 screenPosition, out Vector3 world)
    {
        world = default;
        if (cam == null)
            return false;

        var plane = new Plane(planeNormal.normalized, planePoint);
        var ray = cam.ScreenPointToRay(screenPosition);
        if (!plane.Raycast(ray, out float enter))
            return false;

        world = ray.GetPoint(enter);
        return true;
    }

    /// <summary>Maps viewport xy (z ignored) to a point on this work plane; z component of viewport is unused.</summary>
    public bool TryViewportToPlane(Camera cam, Vector3 viewportXy01, out Vector3 world)
    {
        if (cam == null)
        {
            world = default;
            return false;
        }

        var sp = cam.ViewportToScreenPoint(viewportXy01);
        return TryGetWorldPoint(cam, new Vector2(sp.x, sp.y), out world);
    }
}
