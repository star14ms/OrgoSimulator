using UnityEngine;
using UnityEngine.UI;

public class ButtonCreateAtom : MonoBehaviour
{
    [SerializeField] GameObject atomPrefab;
    [SerializeField] float viewportMargin = 0.1f;

    PeriodicTableUI periodicTable;

    void Start()
    {
        if (TryGetComponent<Button>(out var button))
            button.onClick.AddListener(ShowPeriodicTable);

        periodicTable = GetComponent<PeriodicTableUI>();
        if (periodicTable == null)
            periodicTable = gameObject.AddComponent<PeriodicTableUI>();
    }

    void ShowPeriodicTable()
    {
        if (periodicTable == null) return;
        periodicTable.SetAtomPrefab(atomPrefab);
        periodicTable.SetViewportMargin(viewportMargin);
        periodicTable.Show();
    }
}
