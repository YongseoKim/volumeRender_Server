using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class PlayerListManager : MonoBehaviour
{
    private const string LogPrefix = "[PlayerList]";

    [SerializeField] private PlayerNameManager clientButtonPrefab;
    [SerializeField] private Transform playerListContent;
    [SerializeField] private DataNetManager dataNetManager;

    private readonly Dictionary<ulong, PlayerNameManager> _spawnedButtons = new();
    private bool _subscribed;
    private bool _networkSubscribed;

    private void OnEnable()
    {
        TrySubscribe();
        TrySubscribeToNetworkEvents();
        SyncExistingClients();
    }

    private void Start()
    {
        TrySubscribe();
        TrySubscribeToNetworkEvents();
        SyncExistingClients();
    }

    private void OnDisable()
    {
        Unsubscribe();
        UnsubscribeFromNetworkEvents();
    }

    private void TrySubscribe()
    {
        if (_subscribed)
        {
            return;
        }

        ResolveDataNetManager();
        if (dataNetManager == null)
        {
            return;
        }

        dataNetManager.ClientConnected += HandleClientConnected;
        dataNetManager.ClientDisconnected += HandleClientDisconnected;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || dataNetManager == null)
        {
            return;
        }

        dataNetManager.ClientConnected -= HandleClientConnected;
        dataNetManager.ClientDisconnected -= HandleClientDisconnected;
        _subscribed = false;
    }

    private void TrySubscribeToNetworkEvents()
    {
        if (_networkSubscribed)
        {
            return;
        }

        var networkManager = NetworkManager.Singleton;
        if (networkManager == null)
        {
            return;
        }

        networkManager.OnServerStopped += HandleServerStopped;
        _networkSubscribed = true;
    }

    private void UnsubscribeFromNetworkEvents()
    {
        if (!_networkSubscribed)
        {
            return;
        }

        var networkManager = NetworkManager.Singleton;
        if (networkManager != null)
        {
            networkManager.OnServerStopped -= HandleServerStopped;
        }

        _networkSubscribed = false;
    }

    private void HandleServerStopped(bool wasHost)
    {
        ClearAllButtons();
    }

    private void ClearAllButtons()
    {
        foreach (var clientButton in _spawnedButtons.Values)
        {
            if (clientButton != null)
            {
                Destroy(clientButton.gameObject);
            }
        }

        _spawnedButtons.Clear();
    }

    private void ResolveDataNetManager()
    {
        if (dataNetManager != null)
        {
            return;
        }

        dataNetManager = FindFirstObjectByType<DataNetManager>();
    }

    private void SyncExistingClients()
    {
        var networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer)
        {
            return;
        }

        var clientIds = networkManager.ConnectedClientsIds;
        for (var i = 0; i < clientIds.Count; i++)
        {
            HandleClientConnected(clientIds[i]);
        }
    }

    private void HandleClientConnected(ulong clientId)
    {
        if (_spawnedButtons.ContainsKey(clientId))
        {
            return;
        }

        if (clientButtonPrefab == null)
        {
            Debug.LogError($"{LogPrefix} clientButtonPrefab is not assigned.");
            return;
        }

        if (playerListContent == null)
        {
            Debug.LogError($"{LogPrefix} playerListContent is not assigned.");
            return;
        }

        var clientButton = Instantiate(clientButtonPrefab, playerListContent);
        clientButton.Initialize(clientId);
        _spawnedButtons.Add(clientId, clientButton);
        Debug.Log($"{LogPrefix} Added client button for ClientId={clientId}.");
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        if (!_spawnedButtons.TryGetValue(clientId, out var clientButton))
        {
            return;
        }

        _spawnedButtons.Remove(clientId);
        if (clientButton != null)
        {
            Destroy(clientButton.gameObject);
        }

        Debug.Log($"{LogPrefix} Removed client button for ClientId={clientId}.");
    }
}
