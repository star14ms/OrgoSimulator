using UnityEngine;

/// <summary>
/// Maps screen / viewport positions to points on the molecule work plane. Falls back to the original orthographic mapping when no <see cref="MoleculeWorkPlane"/> exists.
/// </summary>
public static class PlanarPointerInteraction
{
    public static Vector3 ScreenToWorldPoint(Vector2 screenPosition)
    {
        var cam = Camera.main;
        if (cam == null)
            return Vector3.zero;

        if (MoleculeWorkPlane.Instance != null &&
            MoleculeWorkPlane.Instance.TryGetWorldPoint(cam, screenPosition, out var hit))
            return hit;

        var mouse = new Vector3(screenPosition.x, screenPosition.y, -cam.transform.position.z);
        return cam.ScreenToWorldPoint(mouse);
    }

    public static Vector3 RandomWorldPointInMarginedViewport(float viewportMargin)
    {
        float min = viewportMargin;
        float max = 1f - viewportMargin;
        var cam = Camera.main;
        if (cam == null)
            return Vector3.zero;

        if (MoleculeWorkPlane.Instance != null &&
            MoleculeWorkPlane.Instance.TryViewportToPlane(cam, new Vector3(min, min, 0f), out var wMin) &&
            MoleculeWorkPlane.Instance.TryViewportToPlane(cam, new Vector3(max, max, 0f), out var wMax))
        {
            float x0 = Mathf.Min(wMin.x, wMax.x);
            float x1 = Mathf.Max(wMin.x, wMax.x);
            float y0 = Mathf.Min(wMin.y, wMax.y);
            float y1 = Mathf.Max(wMin.y, wMax.y);
            return new Vector3(Random.Range(x0, x1), Random.Range(y0, y1), wMin.z);
        }

        Vector3 minWorld = cam.ViewportToWorldPoint(new Vector3(min, min, -cam.transform.position.z));
        Vector3 maxWorld = cam.ViewportToWorldPoint(new Vector3(max, max, -cam.transform.position.z));
        return new Vector3(
            Random.Range(minWorld.x, maxWorld.x),
            Random.Range(minWorld.y, maxWorld.y),
            minWorld.z);
    }

    public static Vector3 ViewportCenterOnWorkPlane(float viewportMargin)
    {
        float min = viewportMargin;
        float max = 1f - viewportMargin;
        float mid = (min + max) * 0.5f;
        var cam = Camera.main;
        if (cam == null)
            return Vector3.zero;

        if (MoleculeWorkPlane.Instance != null &&
            MoleculeWorkPlane.Instance.TryViewportToPlane(cam, new Vector3(mid, mid, 0f), out var world))
            return world;

        return cam.ViewportToWorldPoint(new Vector3(mid, mid, -cam.transform.position.z));
    }
}
