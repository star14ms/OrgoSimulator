using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Ensures the main camera has PhysicsRaycaster (3D) and Physics2DRaycaster (2D)
/// so objects with colliders receive pointer events when using the new Input System.
/// </summary>
public static class PhysicsRaycasterSetup
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsurePhysicsRaycasters()
    {
        var cam = Camera.main;
        if (cam == null) return;

        if (cam.GetComponent<PhysicsRaycaster>() == null)
            cam.gameObject.AddComponent<PhysicsRaycaster>();

        if (cam.GetComponent<Physics2DRaycaster>() == null)
            cam.gameObject.AddComponent<Physics2DRaycaster>();
    }
}
