using UnityEngine;
using UnityEngine.UI;

public class ButtonCreateAtom : MonoBehaviour
{
    [SerializeField] GameObject atomPrefab;
    [SerializeField] float viewportMargin = 0.1f;

    void Start()
    {
        if (TryGetComponent<Button>(out var button))
            button.onClick.AddListener(CreateAtom);
    }

    public void CreateAtom()
    {
        if (atomPrefab == null || Camera.main == null) return;

        Vector3 pos = GetRandomPositionInView();
        int z = Random.Range(1, 21); // 1–20
        var atomObj = Instantiate(atomPrefab, pos, Quaternion.identity);
        if (atomObj.TryGetComponent<AtomFunction>(out var atom))
            atom.AtomicNumber = z;
    }

    Vector3 GetRandomPositionInView()
    {
        float min = viewportMargin;
        float max = 1f - viewportMargin;
        Vector3 minWorld = Camera.main.ViewportToWorldPoint(new Vector3(min, min, -Camera.main.transform.position.z));
        Vector3 maxWorld = Camera.main.ViewportToWorldPoint(new Vector3(max, max, -Camera.main.transform.position.z));
        return new Vector3(
            Random.Range(minWorld.x, maxWorld.x),
            Random.Range(minWorld.y, maxWorld.y),
            minWorld.z
        );
    }
}
