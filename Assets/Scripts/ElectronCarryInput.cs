using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// After double-tapping an occupied lone orbital or spawning e⁻ from the toolbar, the electron follows the pointer;
/// the next primary click applies trash / orbital absorption / free placement in world space.
/// </summary>
[DefaultExecutionOrder(-1000)]
public class ElectronCarryInput : MonoBehaviour
{
    static ElectronCarryInput _instance;

    /// <summary>When true, <see cref="ElectronOrbitalFunction"/> ignores <see cref="IPointerDownHandler"/> this frame so a carry-release click does not start an orbital drag.</summary>
    public static bool BlockOrbitalPointerForCarryFinalize { get; private set; }

    public static ElectronCarryInput Instance
    {
        get
        {
            if (_instance == null)
                CreateHost();
            return _instance;
        }
    }

    ElectronFunction carried;
    int suppressFinalizePresses;

    static void CreateHost()
    {
        var go = new GameObject(nameof(ElectronCarryInput));
        go.AddComponent<ElectronCarryInput>();
    }

    void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
    }

    void OnDestroy()
    {
        if (_instance == this)
            _instance = null;
    }

    /// <summary>Screen position for carry/placement (new Input System when available).</summary>
    public static Vector2 PrimaryPointerScreen()
    {
        if (Mouse.current != null)
            return Mouse.current.position.ReadValue();
        return Input.mousePosition;
    }

    /// <summary>True the frame the primary button was pressed.</summary>
    public static bool PrimaryPressedThisFrame()
    {
        if (Mouse.current != null)
            return Mouse.current.leftButton.wasPressedThisFrame;
        return Input.GetMouseButtonDown(0);
    }

    /// <param name="suppressMouseButtonDowns">Primary clicks to skip before finalizing (usually 0).</param>
    public void StartCarrying(ElectronFunction electron, int suppressMouseButtonDowns = 0)
    {
        if (electron == null) return;
        if (carried != null && carried != electron)
            EndCarryWithoutDestroy(carried);
        carried = electron;
        carried.SetPointerFollowCarry(true);
        suppressFinalizePresses = suppressMouseButtonDowns;
    }

    public void NotifyCarriedDestroyed(ElectronFunction electron)
    {
        if (carried == electron)
            carried = null;
    }

    /// <summary>Call from <see cref="ElectronFunction.OnDestroy"/> without forcing <see cref="Instance"/> creation.</summary>
    public static void NotifyCarriedDestroyedIfHost(ElectronFunction electron)
    {
        if (_instance != null && electron != null)
            _instance.NotifyCarriedDestroyed(electron);
    }

    void Update()
    {
        if (carried == null) return;

        // Whole-session guard: while dragging e⁻, orbitals must not see a "real" pointer down (paired with a later up),
        // or orphan ups peel again. Paired-pointer flag fixes orphan ups; this fixes orphan downs during the carry.
        BlockOrbitalPointerForCarryFinalize = true;

        Vector2 screen = PrimaryPointerScreen();
        carried.transform.position = PlanarPointerInteraction.SnapWorldToWorkPlaneIfPresent(
            PlanarPointerInteraction.ScreenToWorldPoint(screen));

        if (!PrimaryPressedThisFrame())
            return;
        if (suppressFinalizePresses > 0)
        {
            suppressFinalizePresses--;
            return;
        }

        var disposal = Object.FindFirstObjectByType<DisposalZone>();
        bool overTrash = disposal != null && disposal.ContainsScreenPoint(screen);

        var e = carried;
        carried = null;
        e.SetPointerFollowCarry(false);

        if (overTrash)
        {
            Destroy(e.gameObject);
            return;
        }

        e.TryAcceptIntoOrbital();
    }

    void LateUpdate()
    {
        if (carried == null)
            BlockOrbitalPointerForCarryFinalize = false;
    }

    static void EndCarryWithoutDestroy(ElectronFunction electron)
    {
        if (electron == null) return;
        electron.SetPointerFollowCarry(false);
        electron.TryAcceptIntoOrbital();
    }
}
