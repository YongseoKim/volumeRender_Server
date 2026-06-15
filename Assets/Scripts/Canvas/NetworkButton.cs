using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode.Components;

public class NetworkButton : MonoBehaviour
{
    [SerializeField] private Button serverButton;

    private void Awake()
    {
        serverButton.onClick.AddListener(() => {
            NetworkManager.Singleton.StartServer();
        });
    }
}
