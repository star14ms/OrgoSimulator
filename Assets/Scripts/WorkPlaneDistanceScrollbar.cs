using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Vertical scrollbar on the right edge: dolly <see cref="Camera.main"/> along its view axis to change distance to the
/// <see cref="MoleculeWorkPlane"/> (perspective), or adjust orthographic size (2D). Handle tracks depth after orbit and dolly.
/// </summary>
[DefaultExecutionOrder(10)]
public class WorkPlaneDistanceScrollbar : MonoBehaviour
{
    [SerializeField] float trackWidthPx = 22f;
    [Tooltip("Fallback right inset (design px × screen/1080) when no AtomQuickAddUI is on this object.")]
    [SerializeField] float marginFromRightPxFallback = 4f;
    [Tooltip("Fraction of canvas height used by the scrollbar track (1 = full height).")]
    [SerializeField] float trackHeightFraction = 0.5f;
    [Tooltip("Orthographic: min size (zoomed in) at scrollbar top.")]
    [SerializeField] float orthographicSizeMin = 2.5f;
    [Tooltip("Orthographic: max size (zoomed out) at scrollbar bottom.")]
    [SerializeField] float orthographicSizeMax = 12f;

    Scrollbar scrollbar;
    ScrollOrbitCamera orbit;
    bool suppressCallback;

    void Start()
    {
        var canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            canvas = GetComponentInChildren<Canvas>();
        if (canvas == null)
            canvas = FindFirstObjectByType<Canvas>();
        if (canvas == null) return;

        float hud = Mathf.Max(1e-6f, Mathf.Min(Screen.width, Screen.height) / 1080f);
        var quickAdd = GetComponentInParent<AtomQuickAddUI>();
        float w = quickAdd != null
            ? Mathf.Max(16f, quickAdd.HudPx(trackWidthPx))
            : Mathf.Max(16f, trackWidthPx * hud);
        // Match TogglesPanel upper-right inset (AtomQuickAddUI uses Px(10f) for -x).
        float margin = quickAdd != null
            ? quickAdd.HudPx(10f)
            : Mathf.Max(0f, marginFromRightPxFallback * hud);

        var root = new GameObject("WorkPlaneDistanceScrollbar");
        root.transform.SetParent(canvas.transform, false);
        var rect = root.AddComponent<RectTransform>();
        float frac = Mathf.Clamp(trackHeightFraction, 0.15f, 1f);
        float inset = (1f - frac) * 0.5f;
        rect.anchorMin = new Vector2(1f, inset);
        rect.anchorMax = new Vector2(1f, 1f - inset);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.anchoredPosition = new Vector2(-margin, 0f);
        rect.sizeDelta = new Vector2(w, 0f);

        scrollbar = BuildScrollbarUi(root.transform, w);

        scrollbar.onValueChanged.AddListener(OnScrollbarValueChanged);
        scrollbar.direction = Scrollbar.Direction.BottomToTop;

        SyncScrollbarFromCamera();
    }

    static Scrollbar BuildScrollbarUi(Transform parent, float trackWidth)
    {
        var bg = new GameObject("Background");
        bg.transform.SetParent(parent, false);
        var bgRect = bg.AddComponent<RectTransform>();
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = Vector2.one;
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = new Color(0.1f, 0.12f, 0.16f, 0.88f);

        var slide = new GameObject("Sliding Area");
        slide.transform.SetParent(parent, false);
        var slideRect = slide.AddComponent<RectTransform>();
        slideRect.anchorMin = Vector2.zero;
        slideRect.anchorMax = Vector2.one;
        slideRect.offsetMin = new Vector2(3f, 5f);
        slideRect.offsetMax = new Vector2(-3f, -5f);

        var handle = new GameObject("Handle");
        handle.transform.SetParent(slide.transform, false);
        var handleRect = handle.AddComponent<RectTransform>();
        handleRect.anchorMin = new Vector2(0f, 0f);
        handleRect.anchorMax = new Vector2(1f, 1f);
        handleRect.pivot = new Vector2(0.5f, 0.5f);
        handleRect.sizeDelta = Vector2.zero;
        var handleImg = handle.AddComponent<Image>();
        handleImg.color = new Color(0.42f, 0.58f, 0.88f, 0.95f);

        var sb = parent.GetComponent<Scrollbar>();
        if (sb == null) sb = parent.gameObject.AddComponent<Scrollbar>();
        sb.targetGraphic = handleImg;
        sb.handleRect = handleRect;
        sb.direction = Scrollbar.Direction.BottomToTop;
        return sb;
    }

    void LateUpdate()
    {
        if (scrollbar == null) return;
        SyncScrollbarFromCamera();
    }

    void OnScrollbarValueChanged(float value)
    {
        if (suppressCallback) return;
        var cam = Camera.main;
        if (cam == null) return;

        if (cam.orthographic)
        {
            float lo = Mathf.Min(orthographicSizeMin, orthographicSizeMax);
            float hi = Mathf.Max(orthographicSizeMin, orthographicSizeMax);
            cam.orthographicSize = Mathf.Lerp(hi, lo, value);
            return;
        }

        orbit = cam.GetComponent<ScrollOrbitCamera>();
        if (orbit == null) return;
        orbit.GetDepthClampRange(out float loD, out float hiD);
        float depth = Mathf.Lerp(hiD, loD, value);
        orbit.ApplyDollyToTargetDepthAlongView(depth);
        orbit.SyncMoleculeWorkPlaneToView();
    }

    void SyncScrollbarFromCamera()
    {
        if (scrollbar == null) return;
        var cam = Camera.main;
        if (cam == null) return;

        float value;
        if (cam.orthographic)
        {
            float lo = Mathf.Min(orthographicSizeMin, orthographicSizeMax);
            float hi = Mathf.Max(orthographicSizeMin, orthographicSizeMax);
            float s = Mathf.Clamp(cam.orthographicSize, lo, hi);
            value = Mathf.InverseLerp(hi, lo, s);
        }
        else
        {
            orbit = cam.GetComponent<ScrollOrbitCamera>();
            if (orbit == null) return;
            orbit.GetDepthClampRange(out float loD, out float hiD);
            float d = orbit.GetDepthAlongViewClamped();
            value = Mathf.InverseLerp(hiD, loD, d);
        }

        if (Mathf.Approximately(scrollbar.value, value)) return;
        suppressCallback = true;
        scrollbar.value = value;
        suppressCallback = false;
    }
}
