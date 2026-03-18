using UnityEngine;
using UnityEngine.EventSystems;

public class BackgroundClickHandler : MonoBehaviour, IPointerDownHandler
{
    public EditModeManager editModeManager;

    public void OnPointerDown(PointerEventData eventData)
    {
        editModeManager?.OnBackgroundClicked();
    }
}
