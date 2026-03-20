using UnityEngine;

/// <summary>
/// Keeps the gameplay HUD as screen-space overlay so it renders flat 2D on top of 3D scenes.
/// </summary>
public static class UiScreenSpace
{
    public static void EnforceOverlay(Canvas canvas)
    {
        if (canvas == null) return;
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.worldCamera = null;
    }
}
