using UnityEngine;

/// <summary>
/// Consistent world-space angle convention for orbitals.
/// 0° = right (1,0), 90° = up (0,1), 180° = left (-1,0), -90° = down (0,-1).
/// Use this when comparing orbital directions across different atoms to avoid local-space mismatches.
/// </summary>
public static class OrbitalAngleUtility
{
    /// <summary>Convert a world-space direction to angle (0° = right).</summary>
    public static float DirectionToAngleWorld(Vector3 worldDir)
    {
        worldDir.z = 0;
        if (worldDir.sqrMagnitude < 0.01f) return 0f;
        worldDir.Normalize();
        return Mathf.Atan2(worldDir.y, worldDir.x) * Mathf.Rad2Deg;
    }

    /// <summary>Get the world-space direction an orbital points (its local right axis in world space).</summary>
    public static Vector3 GetOrbitalDirectionWorld(Transform orbital)
    {
        var dir = orbital.TransformDirection(Vector3.right);
        dir.z = 0;
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
}
