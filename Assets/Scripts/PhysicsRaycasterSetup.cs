using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Ensures the main camera has 3D/2D physics raycasters so colliders receive pointer events with the Input System.
/// Uses UI-first subclasses so screen-space HUD is not starved by world hits in 3D scenes.
/// </summary>
public static class PhysicsRaycasterSetup
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsurePhysicsRaycasters()
    {
        var cam = Camera.main;
        if (cam == null) return;

        if (cam.GetComponent<PhysicsRaycasterUiFirst>() == null)
        {
            var legacy3D = cam.GetComponent<PhysicsRaycaster>();
            if (legacy3D != null && legacy3D.GetType() == typeof(PhysicsRaycaster))
            {
                legacy3D.enabled = false;
                Object.Destroy(legacy3D);
            }
            if (cam.GetComponent<PhysicsRaycasterUiFirst>() == null)
                cam.gameObject.AddComponent<PhysicsRaycasterUiFirst>();
        }

        if (cam.GetComponent<Physics2DRaycasterUiFirst>() == null)
        {
            var legacy2D = cam.GetComponent<Physics2DRaycaster>();
            if (legacy2D != null && legacy2D.GetType() == typeof(Physics2DRaycaster))
            {
                legacy2D.enabled = false;
                Object.Destroy(legacy2D);
            }
            if (cam.GetComponent<Physics2DRaycasterUiFirst>() == null)
                cam.gameObject.AddComponent<Physics2DRaycasterUiFirst>();
        }
    }
}
