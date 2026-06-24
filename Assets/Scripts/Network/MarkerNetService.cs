using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Routes paneLine_Volume drag previews and committed markers between server and clients.
/// Server: validate, persist (MarkerServerStore), rebroadcast.
/// Client: send local drag events, render remote previews, never persist.
/// </summary>
public class MarkerNetService : MonoBehaviour
{
    private const string LogPrefix = "[MarkerNet]";

    [SerializeField] private MarkerManager markerManager;
    [SerializeField] private MonoBehaviour paneContextResolverBehaviour;
    [SerializeField, Range(0.02f, 0.25f)] private float dragPointSendIntervalSeconds = 0.08f;

    [Header("Broadcast Permission")]
    [SerializeField] private bool defaultLocalBroadcastEnabled = true;
    [SerializeField] private bool defaultClientBroadcastAllowed = true;
    [SerializeField] private Toggle broadcastToggle;

    public bool LocalBroadcastEnabled { get; private set; }
    public bool ServerBroadcastAllowed { get; private set; } = true;
    public bool CanBroadcastMarkers => IsNetworkActive() && LocalBroadcastEnabled && ServerBroadcastAllowed;

    public event Action<bool> LocalBroadcastEnabledChanged;
    public event Action<bool> ServerBroadcastAllowedChanged;

    private readonly MarkerServerStore _serverStore = new MarkerServerStore();
    private readonly Dictionary<PaneLineDragSessionId, float> _lastDragPointSentAt = new Dictionary<PaneLineDragSessionId, float>();
    private readonly Dictionary<PaneLineDragSessionId, PaneLineDragBeginData> _activeRemoteSessions =
        new Dictionary<PaneLineDragSessionId, PaneLineDragBeginData>();

    private uint _localDragIdCounter = 1;
    private PaneLineDragSessionId _activeLocalDragSession;
    private bool _hasActiveLocalDragSession;
    private bool _handlersRegistered;
    private bool _markerEventsBound;
    private bool _networkSubscribed;
    private bool _broadcastToggleBound;
    private IMarkerPaneContextResolver _paneContextResolver;
    private readonly Dictionary<ulong, bool> _clientBroadcastPermissions = new Dictionary<ulong, bool>();

    private void OnEnable() => TryInitialize();
    private void Start() => TryInitialize();
    private void OnDestroy() => Shutdown();

    private void TryInitialize()
    {
        ResolveDependencies();
        TrySubscribeNetworkLifecycle();
        TryBindMarkerEvents();
        TryBindBroadcastToggle();
        TryRegisterHandlers();
        UpdateNetworkCommitMode();
    }

    private void Shutdown()
    {
        UnsubscribeNetworkLifecycle();
        UnbindBroadcastToggle();
        UnbindMarkerEvents();
        UnregisterHandlers();
        markerManager?.SetNetworkCommitEnabled(false);
    }

    private void TrySubscribeNetworkLifecycle()
    {
        if (_networkSubscribed)
        {
            return;
        }

        var nm = NetworkManager.Singleton;
        if (nm == null)
        {
            return;
        }

        nm.OnClientStarted += HandleNetworkStarted;
        nm.OnServerStarted += HandleNetworkStarted;
        nm.OnClientStopped += HandleNetworkStopped;
        nm.OnServerStopped += HandleNetworkStopped;
        _networkSubscribed = true;
    }

    private void UnsubscribeNetworkLifecycle()
    {
        if (!_networkSubscribed)
        {
            return;
        }

        var nm = NetworkManager.Singleton;
        if (nm != null)
        {
            nm.OnClientStarted -= HandleNetworkStarted;
            nm.OnServerStarted -= HandleNetworkStarted;
            nm.OnClientStopped -= HandleNetworkStopped;
            nm.OnServerStopped -= HandleNetworkStopped;
        }

        _networkSubscribed = false;
    }

    private void HandleNetworkStarted()
    {
        UnregisterHandlers();
        TryRegisterHandlers();
        UpdateNetworkCommitMode();
    }

    private void HandleNetworkStopped(bool _)
    {
        UnregisterHandlers();
        UpdateNetworkCommitMode();
    }

    private void UpdateNetworkCommitMode()
    {
        markerManager?.SetNetworkCommitEnabled(CanBroadcastMarkers);
    }

    public void SetLocalBroadcastEnabled(bool enabled)
    {
        if (LocalBroadcastEnabled == enabled)
        {
            return;
        }

        LocalBroadcastEnabled = enabled;
        if (broadcastToggle != null)
        {
            broadcastToggle.SetIsOnWithoutNotify(enabled);
        }

        UpdateNetworkCommitMode();
        LocalBroadcastEnabledChanged?.Invoke(enabled);
    }

    /// <summary>
    /// Server-only: grant or revoke a client's marker broadcast permission.
    /// </summary>
    public void SetClientBroadcastPermission(ulong clientId, bool allowed)
    {
        var networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer)
        {
            return;
        }

        _clientBroadcastPermissions[clientId] = allowed;
        SendBroadcastPermissionSync(clientId, allowed);
    }

    private void TryBindBroadcastToggle()
    {
        if (!_broadcastToggleBound)
        {
            LocalBroadcastEnabled = defaultLocalBroadcastEnabled;
        }

        if (broadcastToggle == null || _broadcastToggleBound)
        {
            return;
        }

        broadcastToggle.SetIsOnWithoutNotify(LocalBroadcastEnabled);
        broadcastToggle.onValueChanged.AddListener(OnBroadcastToggleChanged);
        _broadcastToggleBound = true;
    }

    private void UnbindBroadcastToggle()
    {
        if (!_broadcastToggleBound || broadcastToggle == null)
        {
            return;
        }

        broadcastToggle.onValueChanged.RemoveListener(OnBroadcastToggleChanged);
        _broadcastToggleBound = false;
    }

    private void OnBroadcastToggleChanged(bool isOn)
    {
        SetLocalBroadcastEnabled(isOn);
    }

    private void ApplyServerBroadcastPermission(bool allowed)
    {
        if (ServerBroadcastAllowed == allowed)
        {
            return;
        }

        ServerBroadcastAllowed = allowed;
        UpdateNetworkCommitMode();
        ServerBroadcastAllowedChanged?.Invoke(allowed);
    }

    private void ResolveDependencies()
    {
        if (markerManager == null)
        {
            markerManager = FindFirstObjectByType<MarkerManager>();
        }

        if (_paneContextResolver == null && paneContextResolverBehaviour is IMarkerPaneContextResolver resolver)
        {
            _paneContextResolver = resolver;
        }

        if (_paneContextResolver == null)
        {
            foreach (var behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (behaviour is IMarkerPaneContextResolver candidate)
                {
                    _paneContextResolver = candidate;
                    paneContextResolverBehaviour = behaviour;
                    break;
                }
            }
        }
    }

    private void TryBindMarkerEvents()
    {
        if (_markerEventsBound || markerManager == null)
        {
            return;
        }

        markerManager.LocalDragBegun += HandleLocalDragBegun;
        markerManager.LocalDragPointAppended += HandleLocalDragPointAppended;
        markerManager.LocalDragEnded += HandleLocalDragEnded;
        _markerEventsBound = true;
    }

    private void UnbindMarkerEvents()
    {
        if (!_markerEventsBound || markerManager == null)
        {
            return;
        }

        markerManager.LocalDragBegun -= HandleLocalDragBegun;
        markerManager.LocalDragPointAppended -= HandleLocalDragPointAppended;
        markerManager.LocalDragEnded -= HandleLocalDragEnded;
        _markerEventsBound = false;
    }

    private void TryRegisterHandlers()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening || _handlersRegistered)
        {
            return;
        }

        var messaging = nm.CustomMessagingManager;
        if (messaging == null)
        {
            return;
        }

        if (nm.IsServer)
        {
            messaging.RegisterNamedMessageHandler(MarkerNetworkProtocol.DragBeginMessageName, HandleServerDragBegin);
            messaging.RegisterNamedMessageHandler(MarkerNetworkProtocol.DragPointMessageName, HandleServerDragPoint);
            messaging.RegisterNamedMessageHandler(MarkerNetworkProtocol.DragEndMessageName, HandleServerDragEnd);
            nm.OnClientConnectedCallback += HandleServerClientConnected;
        }

        if (nm.IsClient)
        {
            messaging.RegisterNamedMessageHandler(MarkerNetworkProtocol.DragBeginMessageName, HandleClientDragBeginPreview);
            messaging.RegisterNamedMessageHandler(MarkerNetworkProtocol.DragPointMessageName, HandleClientDragPointPreview);
            messaging.RegisterNamedMessageHandler(MarkerNetworkProtocol.DragEndMessageName, HandleClientDragEndPreview);
            messaging.RegisterNamedMessageHandler(MarkerNetworkProtocol.MarkerCreatedMessageName, HandleClientMarkerCreated);
            messaging.RegisterNamedMessageHandler(MarkerNetworkProtocol.MarkerErasedMessageName, HandleClientMarkerErased);
            messaging.RegisterNamedMessageHandler(MarkerNetworkProtocol.MarkerSnapshotMessageName, HandleClientMarkerSnapshot);
            messaging.RegisterNamedMessageHandler(
                MarkerNetworkProtocol.BroadcastPermissionSyncMessageName,
                HandleClientBroadcastPermissionSync);
        }

        _handlersRegistered = true;
    }

    private void UnregisterHandlers()
    {
        var nm = NetworkManager.Singleton;
        var messaging = nm?.CustomMessagingManager;
        if (messaging == null)
        {
            _handlersRegistered = false;
            return;
        }

        if (nm.IsServer)
        {
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.DragBeginMessageName);
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.DragPointMessageName);
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.DragEndMessageName);
            nm.OnClientConnectedCallback -= HandleServerClientConnected;
        }

        if (nm.IsClient)
        {
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.DragBeginMessageName);
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.DragPointMessageName);
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.DragEndMessageName);
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.MarkerCreatedMessageName);
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.MarkerErasedMessageName);
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.MarkerSnapshotMessageName);
            messaging.UnregisterNamedMessageHandler(MarkerNetworkProtocol.BroadcastPermissionSyncMessageName);
        }

        _handlersRegistered = false;
    }

    // ── Local drag → network ──────────────────────────────────────────────

    private void HandleLocalDragBegun(PaneLineDragBeginData data)
    {
        if (!IsNetworkActive() || !CanBroadcastMarkers)
        {
            return;
        }

        var nm = NetworkManager.Singleton;
        data.SessionId = new PaneLineDragSessionId
        {
            OwnerClientId = nm.LocalClientId,
            LocalDragId = _localDragIdCounter++,
        };
        _activeLocalDragSession = data.SessionId;
        _hasActiveLocalDragSession = true;

        if (nm.IsServer)
        {
            AcceptAndBroadcastDragBegin(data, excludeClientId: nm.LocalClientId);
            return;
        }

        SendToServer(
            MarkerNetworkProtocol.DragBeginMessageName,
            writer => MarkerNetworkProtocol.WriteDragBegin(writer, data),
            MarkerNetworkProtocol.DragPreviewDelivery,
            MarkerNetworkProtocol.EstimateDragBeginSize(data));
    }

    private void HandleLocalDragPointAppended(PaneLineDragPointData data)
    {
        if (!IsNetworkActive() || !CanBroadcastMarkers || !_hasActiveLocalDragSession)
        {
            return;
        }

        data.SessionId = _activeLocalDragSession;
        var now = Time.unscaledTime;
        if (_lastDragPointSentAt.TryGetValue(data.SessionId, out var lastSentAt) &&
            now - lastSentAt < dragPointSendIntervalSeconds)
        {
            return;
        }

        _lastDragPointSentAt[data.SessionId] = now;

        var nm = NetworkManager.Singleton;
        if (nm.IsServer)
        {
            AcceptAndBroadcastDragPoint(data, excludeClientId: nm.LocalClientId);
            return;
        }

        SendToServer(
            MarkerNetworkProtocol.DragPointMessageName,
            writer => MarkerNetworkProtocol.WriteDragPoint(writer, data),
            MarkerNetworkProtocol.DragPreviewDelivery,
            MarkerNetworkProtocol.EstimateDragPointSize(data));
    }

    private void HandleLocalDragEnded(PaneLineDragEndData data)
    {
        if (!IsNetworkActive() || !CanBroadcastMarkers)
        {
            return;
        }

        var nm = NetworkManager.Singleton;
        data.SessionId = _hasActiveLocalDragSession ? _activeLocalDragSession : data.SessionId;
        _hasActiveLocalDragSession = false;
        _lastDragPointSentAt.Remove(data.SessionId);

        if (nm.IsServer)
        {
            CommitMarkerOnServer(data, senderClientId: nm.LocalClientId);
            return;
        }

        SendToServer(
            MarkerNetworkProtocol.DragEndMessageName,
            writer => MarkerNetworkProtocol.WriteDragEnd(writer, data),
            MarkerNetworkProtocol.MarkerCommitDelivery,
            MarkerNetworkProtocol.EstimateDragEndSize(data));
    }

    // ── Server ingress ──────────────────────────────────────────────────

    private void HandleServerDragBegin(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsServer)
        {
            return;
        }

        if (!IsClientBroadcastAllowed(senderClientId))
        {
            Debug.LogWarning($"{LogPrefix} Rejected drag begin from client {senderClientId} (broadcast not allowed).");
            return;
        }

        MarkerNetworkProtocol.ReadDragBegin(reader, out var data);
        data.SessionId.OwnerClientId = senderClientId;
        AcceptAndBroadcastDragBegin(data, excludeClientId: senderClientId);
    }

    private void HandleServerDragPoint(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsServer)
        {
            return;
        }

        if (!IsClientBroadcastAllowed(senderClientId))
        {
            return;
        }

        MarkerNetworkProtocol.ReadDragPoint(reader, out var data);
        data.SessionId.OwnerClientId = senderClientId;
        AcceptAndBroadcastDragPoint(data, excludeClientId: senderClientId);
    }

    private void HandleServerDragEnd(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsServer)
        {
            return;
        }

        if (!IsClientBroadcastAllowed(senderClientId))
        {
            Debug.LogWarning($"{LogPrefix} Rejected drag end from client {senderClientId} (broadcast not allowed).");
            return;
        }

        MarkerNetworkProtocol.ReadDragEnd(reader, out var data);
        data.SessionId.OwnerClientId = senderClientId;
        CommitMarkerOnServer(data, senderClientId);
    }

    private void AcceptAndBroadcastDragBegin(PaneLineDragBeginData data, ulong excludeClientId)
    {
        _activeRemoteSessions[data.SessionId] = data;
        BroadcastToClients(
            MarkerNetworkProtocol.DragBeginMessageName,
            writer => MarkerNetworkProtocol.WriteDragBegin(writer, data),
            MarkerNetworkProtocol.DragPreviewDelivery,
            MarkerNetworkProtocol.EstimateDragBeginSize(data),
            excludeClientId);
    }

    private void AcceptAndBroadcastDragPoint(PaneLineDragPointData data, ulong excludeClientId)
    {
        BroadcastToClients(
            MarkerNetworkProtocol.DragPointMessageName,
            writer => MarkerNetworkProtocol.WriteDragPoint(writer, data),
            MarkerNetworkProtocol.DragPreviewDelivery,
            MarkerNetworkProtocol.EstimateDragPointSize(data),
            excludeClientId);
    }

    private void CommitMarkerOnServer(PaneLineDragEndData dragEnd, ulong senderClientId)
    {
        var metadata = dragEnd.DraftMetadata;
        metadata.MarkerId = _serverStore.AllocateMarkerId();
        metadata.OwnerClientId = senderClientId;

        if (!metadata.IsValid)
        {
            Debug.LogWarning($"{LogPrefix} Rejected invalid marker from client {senderClientId}.");
            markerManager?.CancelRemoteDragPreview(dragEnd.SessionId);
            return;
        }

        _serverStore.Save(metadata);
        _activeRemoteSessions.Remove(dragEnd.SessionId);

        markerManager?.EndRemoteDragPreview(dragEnd.SessionId);

        BroadcastToClients(
            MarkerNetworkProtocol.MarkerCreatedMessageName,
            writer => MarkerNetworkProtocol.WriteMetadata(writer, metadata),
            MarkerNetworkProtocol.MarkerCommitDelivery,
            MarkerNetworkProtocol.EstimateMetadataSize(metadata),
            excludeClientId: ulong.MaxValue);
    }

    private void HandleServerClientConnected(ulong clientId)
    {
        if (!NetworkManager.Singleton.IsServer || clientId == NetworkManager.ServerClientId)
        {
            return;
        }

        var snapshot = _serverStore.GetAll();
        var snapshotArray = snapshot is IList<PaneLineVolumeMetadata> list
            ? ToArray(list)
            : CopyToArray(snapshot);
        var capacity = MarkerNetworkProtocol.ClampMessageCapacity(
            MarkerNetworkProtocol.EstimateMetadataListSize(snapshotArray));
        using var writer = CreateWriter(capacity);
        MarkerNetworkProtocol.WriteMetadataList(writer, snapshotArray);

        NetworkManager.Singleton.CustomMessagingManager.SendNamedMessage(
            MarkerNetworkProtocol.MarkerSnapshotMessageName,
            clientId,
            writer,
            MarkerNetworkProtocol.MarkerCommitDelivery);

        SendBroadcastPermissionSync(clientId, IsClientBroadcastAllowed(clientId));
    }

    // ── Client ingress ──────────────────────────────────────────────────

    private void HandleClientDragBeginPreview(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsClient)
        {
            return;
        }

        MarkerNetworkProtocol.ReadDragBegin(reader, out var data);
        if (IsLocalSession(data.SessionId))
        {
            return;
        }

        _activeRemoteSessions[data.SessionId] = data;
        markerManager?.BeginRemoteDragPreview(data);
    }

    private void HandleClientDragPointPreview(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsClient)
        {
            return;
        }

        MarkerNetworkProtocol.ReadDragPoint(reader, out var data);
        if (IsLocalSession(data.SessionId))
        {
            return;
        }

        markerManager?.UpdateRemoteDragPreview(data);
    }

    private void HandleClientDragEndPreview(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsClient)
        {
            return;
        }

        MarkerNetworkProtocol.ReadDragEnd(reader, out var data);
        if (IsLocalSession(data.SessionId))
        {
            return;
        }

        _activeRemoteSessions.Remove(data.SessionId);
        markerManager?.EndRemoteDragPreview(data.SessionId);
    }

    private void HandleClientMarkerCreated(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsClient)
        {
            return;
        }

        MarkerNetworkProtocol.ReadMetadata(reader, out var metadata);
        markerManager?.ApplyRemoteMarker(metadata);
    }

    private void HandleClientMarkerErased(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsClient)
        {
            return;
        }

        MarkerNetworkProtocol.ReadMarkerId(reader, out var markerId);
        markerManager?.RemoveRemoteMarker(markerId);
    }

    private void HandleClientMarkerSnapshot(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsClient)
        {
            return;
        }

        MarkerNetworkProtocol.ReadMetadataList(reader, out var markers);
        markerManager?.ClearCommittedMarkers();
        for (var i = 0; i < markers.Length; ++i)
        {
            markerManager?.ApplyRemoteMarker(markers[i]);
        }
    }

    private void HandleClientBroadcastPermissionSync(ulong senderClientId, FastBufferReader reader)
    {
        if (!NetworkManager.Singleton.IsClient)
        {
            return;
        }

        MarkerNetworkProtocol.ReadBool(reader, out var allowed);
        ApplyServerBroadcastPermission(allowed);
    }

    private bool IsClientBroadcastAllowed(ulong clientId)
    {
        if (_clientBroadcastPermissions.TryGetValue(clientId, out var allowed))
        {
            return allowed;
        }

        return defaultClientBroadcastAllowed;
    }

    private void SendBroadcastPermissionSync(ulong clientId, bool allowed)
    {
        var networkManager = NetworkManager.Singleton;
        var messaging = networkManager?.CustomMessagingManager;
        if (networkManager == null || messaging == null || !networkManager.IsServer)
        {
            return;
        }

        using var writer = CreateWriter(sizeof(byte));
        MarkerNetworkProtocol.WriteBool(writer, allowed);
        messaging.SendNamedMessage(
            MarkerNetworkProtocol.BroadcastPermissionSyncMessageName,
            clientId,
            writer,
            MarkerNetworkProtocol.PermissionDelivery);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static bool IsNetworkActive()
    {
        var nm = NetworkManager.Singleton;
        return nm != null && nm.IsListening && (nm.IsClient || nm.IsServer);
    }

    private bool IsLocalSession(PaneLineDragSessionId sessionId)
    {
        var nm = NetworkManager.Singleton;
        return nm != null && sessionId.OwnerClientId == nm.LocalClientId;
    }

    private static void SendToServer(
        string messageName,
        Action<FastBufferWriter> writePayload,
        NetworkDelivery delivery,
        int estimatedCapacity)
    {
        var nm = NetworkManager.Singleton;
        var messaging = nm?.CustomMessagingManager;
        if (nm == null || messaging == null || !nm.IsClient)
        {
            return;
        }

        using var writer = CreateWriter(estimatedCapacity);
        writePayload(writer);
        messaging.SendNamedMessage(messageName, NetworkManager.ServerClientId, writer, delivery);
    }

    private static void BroadcastToClients(
        string messageName,
        Action<FastBufferWriter> writePayload,
        NetworkDelivery delivery,
        int estimatedCapacity,
        ulong excludeClientId)
    {
        var nm = NetworkManager.Singleton;
        var messaging = nm?.CustomMessagingManager;
        if (nm == null || messaging == null || !nm.IsServer)
        {
            return;
        }

        using var writer = CreateWriter(estimatedCapacity);
        writePayload(writer);

        var clientIds = nm.ConnectedClientsIds;
        for (var i = 0; i < clientIds.Count; ++i)
        {
            var clientId = clientIds[i];
            if (clientId == excludeClientId)
            {
                continue;
            }

            messaging.SendNamedMessage(messageName, clientId, writer, delivery);
        }
    }

    private static FastBufferWriter CreateWriter(int estimatedCapacity)
    {
        var initialCapacity = MarkerNetworkProtocol.ClampMessageCapacity(estimatedCapacity);
        var maxSize = MarkerNetworkProtocol.ResolveWriterMaxSize(initialCapacity);
        return new FastBufferWriter(initialCapacity, Allocator.Temp, maxSize);
    }

    private static PaneLineVolumeMetadata[] ToArray(IList<PaneLineVolumeMetadata> list)
    {
        var array = new PaneLineVolumeMetadata[list.Count];
        for (var i = 0; i < list.Count; ++i)
        {
            array[i] = list[i];
        }

        return array;
    }

    private static PaneLineVolumeMetadata[] CopyToArray(IReadOnlyList<PaneLineVolumeMetadata> list)
    {
        var array = new PaneLineVolumeMetadata[list.Count];
        for (var i = 0; i < list.Count; ++i)
        {
            array[i] = list[i];
        }

        return array;
    }
}
