using UnityEngine;
using TMPro;

public class ToggleSlices : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI text;
    
    [SerializeField] private GameObject rawImages;
    private bool isSlicesVisible = true;
    
    public void OnToggleSlices()
    {
        isSlicesVisible = !isSlicesVisible;
        rawImages.SetActive(isSlicesVisible);
        text.text = isSlicesVisible ? "Hide" : "Show";
    }
}
