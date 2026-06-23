using TMPro;
using UnityEngine;

public class PlayerNameManager : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI playerNameText;

    public ulong ClientId { get; private set; }

    public void Initialize(ulong clientId)
    {
        ClientId = clientId;
        ApplyClientIdToText();
    }

    private void OnEnable()
    {
        ApplyClientIdToText();
    }

    private void ApplyClientIdToText()
    {
        ResolvePlayerNameText();
        if (playerNameText == null)
        {
            return;
        }

        playerNameText.text = $"Player {ClientId}";
    }

    private void ResolvePlayerNameText()
    {
        if (playerNameText == null)
        {
            playerNameText = GetComponentInChildren<TextMeshProUGUI>(true);
        }
    }
}
