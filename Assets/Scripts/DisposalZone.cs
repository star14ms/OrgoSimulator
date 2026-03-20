using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Bottom-right panel. When a molecule is dragged over it and released, the molecule is destroyed.
/// </summary>
public class DisposalZone : MonoBehaviour
{
    RectTransform rectTransform;

    void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
        if (rectTransform == null)
            rectTransform = gameObject.AddComponent<RectTransform>();
    }


    /// <summary>Call from AtomFunction.OnPointerUp when a molecule is released. Returns true if the release was over this zone.</summary>
    public bool ContainsScreenPoint(Vector2 screenPoint)
    {
        if (rectTransform == null) return false;
        var canvas = GetComponentInParent<Canvas>();
        var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        return RectTransformUtility.RectangleContainsScreenPoint(rectTransform, screenPoint, cam);
    }

    /// <summary>Destroy all atoms and their bonds in the given molecule.</summary>
    public void DestroyMolecule(System.Collections.Generic.HashSet<AtomFunction> atoms)
    {
        if (atoms == null) return;
        var edit = Object.FindFirstObjectByType<EditModeManager>();
        if (edit != null && edit.SelectedAtom != null && atoms.Contains(edit.SelectedAtom))
            edit.OnBackgroundClicked();

        foreach (var a in atoms)
        {
            if (a == null) continue;
            foreach (var b in a.CovalentBonds)
            {
                if (b != null && b.gameObject != null)
                    Destroy(b.gameObject);
            }
        }
        foreach (var a in atoms)
        {
            if (a != null && a.gameObject != null)
                Destroy(a.gameObject);
        }
    }
}
