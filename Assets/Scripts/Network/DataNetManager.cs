using System.Collections.Generic;
using SimpleITKUnity;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class DataNetManager : MonoBehaviour
{
    public const string SampleListMessageName = "SampleList";
    public const string SampleSelectMessageName = "SampleSelect";
    public const string SampleSelectSyncMessageName = "SampleSelectSync";

    private const string LogPrefix = "[DataNet]";

    private readonly List<string> _sampleEntries = new();
    private bool _subscribed;
    private bool _sampleSelectHandlerRegistered;
    private int _selectedSampleIndex = -1;

    [SerializeField] private FileBtnSpawner fileBtnSpawner;

    public IReadOnlyList<string> SampleEntries => _sampleEntries;
    public int SelectedSampleIndex => _selectedSampleIndex;

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void Start()
    {
        TrySubscribe();
    }

    private void OnDestroy()
    {
        Unsubscribe();
    }

    private void TrySubscribe()
    {
        if (_subscribed)
        {
            return;
        }

        var networkManager = NetworkManager.Singleton;
        if (networkManager == null)
        {
            return;
        }

        networkManager.OnServerStarted += HandleServerStarted;
        networkManager.OnServerStopped += HandleServerStopped;

        if (networkManager.IsServer)
        {
            HandleServerStarted();
        }

        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        var networkManager = NetworkManager.Singleton;
        if (networkManager != null)
        {
            networkManager.OnServerStarted -= HandleServerStarted;
            networkManager.OnServerStopped -= HandleServerStopped;
            networkManager.OnClientConnectedCallback -= OnClientConnected;
        }

        TryUnregisterSampleSelectHandler();
        _subscribed = false;
    }

    private void HandleServerStarted()
    {
        RefreshSampleEntries();

        var networkManager = NetworkManager.Singleton;
        if (networkManager == null)
        {
            return;
        }

        networkManager.OnClientConnectedCallback -= OnClientConnected;
        networkManager.OnClientConnectedCallback += OnClientConnected;

        TryRegisterSampleSelectHandler(networkManager);
    }

    private void HandleServerStopped(bool wasHost)
    {
        var networkManager = NetworkManager.Singleton;
        if (networkManager != null)
        {
            networkManager.OnClientConnectedCallback -= OnClientConnected;
        }

        TryUnregisterSampleSelectHandler();
    }

    private void TryRegisterSampleSelectHandler(NetworkManager networkManager)
    {
        if (_sampleSelectHandlerRegistered || networkManager == null || !networkManager.IsServer)
        {
            return;
        }

        var messaging = networkManager.CustomMessagingManager;
        if (messaging == null)
        {
            return;
        }

        messaging.RegisterNamedMessageHandler(SampleSelectMessageName, HandleSampleSelectMessage);
        _sampleSelectHandlerRegistered = true;
    }

    private void TryUnregisterSampleSelectHandler()
    {
        if (!_sampleSelectHandlerRegistered)
        {
            return;
        }

        var messaging = NetworkManager.Singleton?.CustomMessagingManager;
        if (messaging != null)
        {
            messaging.UnregisterNamedMessageHandler(SampleSelectMessageName);
        }

        _sampleSelectHandlerRegistered = false;
    }

    private void HandleSampleSelectMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out int index);
        ApplySampleSelection(index, $"Client {senderClientId}");
    }

    public void BroadcastLocalSampleSelection(int index)
    {
        if (!TryGetSampleFileName(index, out var fileName))
        {
            Debug.LogWarning($"{LogPrefix} Server selected invalid file index: {index}");
            return;
        }

        _selectedSampleIndex = index;
        Debug.Log($"{LogPrefix} Server selected file index: {index} ({fileName})");
        BroadcastSampleSelection(index);
    }

    private void ApplySampleSelection(int index, string sourceLabel)
    {
        if (!TryGetSampleFileName(index, out var fileName))
        {
            Debug.LogWarning($"{LogPrefix} {sourceLabel} selected invalid file index: {index}");
            return;
        }

        _selectedSampleIndex = index;
        Debug.Log($"{LogPrefix} {sourceLabel} selected file index: {index} ({fileName})");

        ResolveFileBtnSpawner();
        fileBtnSpawner?.SelectByManifestIndex(index);

        BroadcastSampleSelection(index);
    }

    private void ResolveFileBtnSpawner()
    {
        if (fileBtnSpawner != null)
        {
            return;
        }

        fileBtnSpawner = FindFirstObjectByType<FileBtnSpawner>();
    }

    private void BroadcastSampleSelection(int index)
    {
        var networkManager = NetworkManager.Singleton;
        var messaging = networkManager?.CustomMessagingManager;
        if (networkManager == null || messaging == null || !networkManager.IsServer)
        {
            return;
        }

        var clientIds = networkManager.ConnectedClientsIds;
        if (clientIds.Count == 0)
        {
            return;
        }

        for (var i = 0; i < clientIds.Count; i++)
        {
            using var writer = new FastBufferWriter(sizeof(int), Allocator.Temp);
            writer.WriteValueSafe(index);
            messaging.SendNamedMessage(
                SampleSelectSyncMessageName,
                clientIds[i],
                writer);
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        var networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer)
        {
            return;
        }

        SendSampleListToClient(clientId);
    }

    public void RefreshSampleEntries()
    {
        _sampleEntries.Clear();

        var entries = FileBtnSpawner.GetSampleEntriesFromManifest();
        for (var i = 0; i < entries.Count; i++)
        {
            _sampleEntries.Add(entries[i]);
        }

        Debug.Log($"{LogPrefix} Loaded {_sampleEntries.Count} sample entries from manifest.txt.");
    }

    public bool TryGetSampleFileName(int index, out string fileName)
    {
        if (index < 0 || index >= _sampleEntries.Count)
        {
            fileName = string.Empty;
            return false;
        }

        fileName = _sampleEntries[index];
        return true;
    }

    private void SendSampleListToClient(ulong clientId)
    {
        var networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer)
        {
            return;
        }

        if (_sampleEntries.Count == 0)
        {
            RefreshSampleEntries();
        }

        var count = _sampleEntries.Count;
        using var writer = new FastBufferWriter(256 + count * 64, Allocator.Temp);
        writer.WriteValueSafe(count);

        for (var i = 0; i < count; i++)
        {
            writer.WriteValueSafe(i);
            writer.WriteValueSafe(_sampleEntries[i]);
        }

        networkManager.CustomMessagingManager.SendNamedMessage(
            SampleListMessageName,
            clientId,
            writer);

        Debug.Log($"{LogPrefix} Sent {count} sample entries to client {clientId}.");
    }
}
