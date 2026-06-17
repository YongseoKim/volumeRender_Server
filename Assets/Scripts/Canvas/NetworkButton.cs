using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

public class NetworkButton : MonoBehaviour
{
    private const string LogPrefix = "[Network]";

    [SerializeField] private Button clientButton;

    private NetworkManager networkManager;
    private bool subscribed;

    private void Awake()
    {
        clientButton.onClick.AddListener(OnClientButtonClicked);
    }

    private void Start()
    {
        TrySubscribeToNetworkEvents();
    }

    private void OnDestroy()
    {
        UnsubscribeFromNetworkEvents();
    }

    private void OnClientButtonClicked()
    {
        if (!TryGetNetworkManager(out var manager))
            return;

        if (manager.IsClient || manager.IsServer)
        {
            Debug.LogWarning($"{LogPrefix} 이미 네트워크 세션이 실행 중입니다. (IsClient={manager.IsClient}, IsServer={manager.IsServer}, IsHost={manager.IsHost})");
            return;
        }

        if (manager.NetworkConfig.NetworkTransport is not UnityTransport transport)
        {
            Debug.LogError($"{LogPrefix} Unity Transport가 NetworkManager에 할당되지 않았습니다.");
            return;
        }

        var address = transport.ConnectionData.Address;
        var port = transport.ConnectionData.Port;
        Debug.Log($"{LogPrefix} Dedicated server 연결 시도: {address}:{port}");

        if (!manager.StartClient())
        {
            LogConnectionFailure(manager, "StartClient()가 false를 반환했습니다.");
        }
    }

    private void TrySubscribeToNetworkEvents()
    {
        if (subscribed || !TryGetNetworkManager(out networkManager))
            return;

        networkManager.OnClientConnectedCallback += OnClientConnected;
        networkManager.OnClientDisconnectCallback += OnClientDisconnected;
        networkManager.OnTransportFailure += OnTransportFailure;
        networkManager.OnConnectionEvent += OnConnectionEvent;
        networkManager.OnClientStopped += OnClientStopped;
        subscribed = true;
    }

    private void UnsubscribeFromNetworkEvents()
    {
        if (!subscribed || networkManager == null)
            return;

        networkManager.OnClientConnectedCallback -= OnClientConnected;
        networkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        networkManager.OnTransportFailure -= OnTransportFailure;
        networkManager.OnConnectionEvent -= OnConnectionEvent;
        networkManager.OnClientStopped -= OnClientStopped;
        subscribed = false;
    }

    private bool TryGetNetworkManager(out NetworkManager manager)
    {
        manager = NetworkManager.Singleton;
        if (manager != null)
            return true;

        Debug.LogError($"{LogPrefix} NetworkManager.Singleton이 null입니다. 씬에 NetworkManager가 있는지 확인하세요.");
        return false;
    }

    private void OnClientConnected(ulong clientId)
    {
        if (networkManager == null || clientId != networkManager.LocalClientId)
            return;

        if (networkManager.IsClient && !networkManager.IsHost)
        {
            Debug.Log($"{LogPrefix} Dedicated server에 연결되었습니다. (ClientId={clientId})");
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (networkManager == null || clientId != networkManager.LocalClientId)
            return;

        if (!networkManager.IsClient && !networkManager.IsHost)
            return;

        LogConnectionFailure(networkManager, "서버와의 연결이 끊어졌습니다.");
    }

    private void OnTransportFailure()
    {
        if (networkManager == null)
            return;

        LogConnectionFailure(networkManager, "Transport 계층에서 연결에 실패했습니다.");
    }

    private void OnConnectionEvent(NetworkManager manager, ConnectionEventData eventData)
    {
        if (eventData.EventType != ConnectionEvent.ClientDisconnected || eventData.ClientId != manager.LocalClientId)
            return;

        LogConnectionFailure(manager, "ConnectionEvent.ClientDisconnected가 발생했습니다.");
    }

    private void OnClientStopped(bool wasHost)
    {
        if (wasHost)
            return;

        Debug.LogWarning($"{LogPrefix} 클라이언트 세션이 종료되었습니다.");
    }

    private static void LogConnectionFailure(NetworkManager manager, string context)
    {
        var reason = string.IsNullOrEmpty(manager.DisconnectReason) ? "(사유 없음)" : manager.DisconnectReason;
        var disconnectEvent = manager.DisconnectEvent;

        Debug.LogError(
            $"{LogPrefix} 연결 실패: {context}\n" +
            $"- DisconnectReason: {reason}\n" +
            $"- DisconnectEvent: {disconnectEvent}\n" +
            $"- IsListening: {manager.IsListening}\n" +
            $"- 확인 사항: 서버가 먼저 StartServer()로 실행 중인지, Address/Port(127.0.0.1:7777)가 서버와 일치하는지, Host 버튼으로 포트를 중복 사용하지 않았는지 확인하세요."
        );
    }
}
