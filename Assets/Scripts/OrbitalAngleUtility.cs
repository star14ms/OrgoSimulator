using UnityEngine;

/// <summary>
/// Consistent world-space angle convention for orbitals.
/// 0° = right (1,0), 90° = up (0,1), 180° = left (-1,0), -90° = down (0,-1).
/// Use this when comparing orbital directions across different atoms to avoid local-space mismatches.
/// </summary>
public static class OrbitalAngleUtility
{
    /// <summary>Perspective camera: orbitals use full 3D VSEPR layout. Orthographic: XY work-plane only.</summary>
    public static bool UseFull3DOrbitalGeometry =>
        Camera.main != null && !Camera.main.orthographic;

    /// <summary>Convert a world-space direction to angle (0° = right).</summary>
    public static float DirectionToAngleWorld(Vector3 worldDir)
    {
        if (!UseFull3DOrbitalGeometry)
            worldDir.z = 0;
        if (worldDir.sqrMagnitude < 0.01f) return 0f;
        worldDir.Normalize();
        return Mathf.Atan2(worldDir.y, worldDir.x) * Mathf.Rad2Deg;
    }

    /// <summary>Get the world-space direction an orbital points (its local right axis in world space).</summary>
    public static Vector3 GetOrbitalDirectionWorld(Transform orbital)
    {
        var dir = orbital.TransformDirection(Vector3.right);
        if (!UseFull3DOrbitalGeometry)
        {
            dir.z = 0f;
            if (dir.sqrMagnitude < 0.01f) return Vector3.right;
            return dir.normalized;
        }
        if (dir.sqrMagnitude < 0.01f) return Vector3.right;
        return dir.normalized;
    }

    /// <summary>Get the world-space angle an orbital points (0° = right).</summary>
    public static float GetOrbitalAngleWorld(Transform orbital)
    {
        return DirectionToAngleWorld(GetOrbitalDirectionWorld(orbital));
    }

    /// <summary>Get world-space angle from a local rotation when the orbital is a child of parent.</summary>
    public static float LocalRotationToAngleWorld(Transform parent, Quaternion localRotation)
    {
        var dir = parent.TransformDirection(localRotation * Vector3.right);
        return DirectionToAngleWorld(dir);
    }

    public static float NormalizeAngle(float deg)
    {
        while (deg > 180f) deg -= 360f;
        while (deg < -180f) deg += 360f;
        return deg;
    }

    /// <summary>SLERP on the shorter quaternion arc (flip b when dot &lt; 0) so orbitals do not spin ~360°.</summary>
    public static Quaternion SlerpShortest(Quaternion a, Quaternion b, float t)
    {
        if (Quaternion.Dot(a, b) < 0f)
            b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
        return Quaternion.Slerp(a, b, t);
    }
}
