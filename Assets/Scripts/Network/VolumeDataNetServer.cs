using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SimpleITKUnity;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Server-side chunked volume transmitter.
/// Reads and preprocesses a sample on the server, then streams a compressed R16 payload to clients.
/// </summary>
public class VolumeDataNetServer : MonoBehaviour
{
    private const string LogPrefix = "[VolumeNetServer]";

    [Header("Dependencies")]
    [SerializeField] private SimpleITKVolumeLoader volumeLoader;
    [SerializeField] private SimpleITKProcessingController processingController;

    [Header("Transfer")]
    [SerializeField, Min(1024)] private int chunkPayloadBytes = 32768;
    [SerializeField, Min(0)] private int yieldEveryNChunks = 4;

    private CancellationTokenSource _transferCts;
    private uint _nextTransferId = 1;
    private uint _activeTransferId;
    private int _activeSampleIndex = -1;
    private bool _isTransferring;

    public bool IsTransferring => _isTransferring;
    public uint ActiveTransferId => _activeTransferId;
    public int ActiveSampleIndex => _activeSampleIndex;

    private void Awake()
    {
        ResolveDependencies();
    }

    private void OnDestroy()
    {
        CancelActiveTransfer();
    }

    /// <summary>
    /// Starts (or restarts) a chunked transfer for the given manifest sample index.
    /// When <paramref name="targetClientId"/> is null, all connected clients receive the stream.
    /// </summary>
    public void SendVolumeForSampleIndex(int sampleIndex, ulong? targetClientId = null)
    {
        if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsServer)
        {
            Debug.LogWarning($"{LogPrefix} SendVolumeForSampleIndex called while not running as server.");
            return;
        }

        ResolveDependencies();
        _ = SendVolumeForSampleIndexAsync(sampleIndex, targetClientId);
    }

    public void CancelActiveTransfer()
    {
        if (_transferCts == null)
        {
            return;
        }

        var abortedTransferId = _activeTransferId;
        _transferCts.Cancel();
        _transferCts.Dispose();
        _transferCts = null;
        _isTransferring = false;

        if (abortedTransferId != 0)
        {
            BroadcastAbort(abortedTransferId);
        }
    }

    private async Task SendVolumeForSampleIndexAsync(int sampleIndex, ulong? targetClientId)
    {
        CancelActiveTransfer();

        var networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer)
        {
            return;
        }

        if (!TryResolveRecipients(targetClientId, out var recipientIds))
        {
            Debug.LogWarning($"{LogPrefix} No connected clients to receive sample index {sampleIndex}.");
            return;
        }

        if (!TryResolveSampleFileName(sampleIndex, out var sampleFileName))
        {
            Debug.LogWarning($"{LogPrefix} Invalid sample index: {sampleIndex}");
            return;
        }

        if (!TryResolveReadableVolumePath(sampleFileName, out var resolvedPath, out var resolveError))
        {
            Debug.LogError($"{LogPrefix} Failed to resolve volume path for '{sampleFileName}': {resolveError}");
            return;
        }

        var transferId = _nextTransferId++;
        _activeTransferId = transferId;
        _activeSampleIndex = sampleIndex;
        _transferCts = new CancellationTokenSource();
        var token = _transferCts.Token;
        _isTransferring = true;

        try
        {
            Debug.Log($"{LogPrefix} Preparing volume transfer {transferId} for sample {sampleIndex} ({sampleFileName}).");

            var processingOptions = ResolveProcessingOptions();
            var (success, volumeData, error) = await SimpleITKNative.TryReadVolumeFloat32NormalizedAsync(
                resolvedPath,
                processingOptions,
                token);

            token.ThrowIfCancellationRequested();

            if (!success)
            {
                Debug.LogError($"{LogPrefix} Failed to read volume '{resolvedPath}': {error}");
                BroadcastAbort(transferId, recipientIds);
                return;
            }

            var payload = await VolumePayloadBuilder.BuildAsync(volumeData, chunkPayloadBytes, token);
            token.ThrowIfCancellationRequested();

            var header = VolumeNetworkProtocol.VolumeHeader.FromImageInfo(
                transferId,
                sampleIndex,
                volumeData.Width,
                volumeData.Height,
                volumeData.Depth,
                volumeData.ImageInfo,
                volumeData.SourceMinimum,
                volumeData.SourceMaximum,
                VolumeNetworkProtocol.VoxelFormat.R16,
                VolumeNetworkProtocol.CompressionKind.Deflate,
                payload.UncompressedBytes,
                payload.CompressedBytesLength,
                payload.TotalChunks,
                payload.PayloadCrc32);

            volumeData.ConsumeVoxels();

            SendBegin(header, recipientIds);
            await SendChunksAsync(transferId, payload, header.TotalChunks, recipientIds, token);
            token.ThrowIfCancellationRequested();
            SendEnd(transferId, header.PayloadCrc32, recipientIds);

            Debug.Log(
                $"{LogPrefix} Completed transfer {transferId}: " +
                $"{header.Width}x{header.Height}x{header.Depth}, " +
                $"compressed={payload.CompressedBytesLength} bytes, chunks={header.TotalChunks}, clients={recipientIds.Count}.");
        }
        catch (OperationCanceledException)
        {
            Debug.Log($"{LogPrefix} Transfer {transferId} cancelled.");
            BroadcastAbort(transferId, recipientIds);
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} Transfer {transferId} failed: {ex.Message}");
            BroadcastAbort(transferId, recipientIds);
        }
        finally
        {
            if (_activeTransferId == transferId)
            {
                _isTransferring = false;
                _activeTransferId = 0;
                _activeSampleIndex = -1;
            }

            if (_transferCts != null)
            {
                _transferCts.Dispose();
                _transferCts = null;
            }
        }
    }

    private SimpleITKVolumeProcessingOptions ResolveProcessingOptions()
    {
        if (processingController != null && processingController.ShouldProcessOnLoad())
        {
            return processingController.BuildProcessingOptions();
        }

        if (volumeLoader != null &&
            volumeLoader.TryGetAssignedProcessingController(out var assignedController) &&
            assignedController != null &&
            assignedController.ShouldProcessOnLoad())
        {
            return assignedController.BuildProcessingOptions();
        }

        return SimpleITKVolumeProcessingOptions.Disabled;
    }

    private async Task SendChunksAsync(
        uint transferId,
        VolumePayloadBuilder.BuiltPayload payload,
        ushort totalChunks,
        IReadOnlyList<ulong> recipientIds,
        CancellationToken token)
    {
        var compressed = payload.CompressedBytes;
        var chunkSize = Mathf.Max(1024, chunkPayloadBytes);
        var chunkBuffer = new byte[chunkSize];
        var sentChunks = 0;
        var effectiveYieldEveryNChunks = recipientIds.Count >= 2 ? 1 : yieldEveryNChunks;

        for (ushort chunkIndex = 0; chunkIndex < totalChunks; ++chunkIndex)
        {
            token.ThrowIfCancellationRequested();

            var sourceOffset = chunkIndex * chunkSize;
            var remaining = compressed.Length - sourceOffset;
            var copyLength = Mathf.Min(chunkSize, remaining);
            Buffer.BlockCopy(compressed, sourceOffset, chunkBuffer, 0, copyLength);

            var chunkBytes = copyLength == chunkBuffer.Length
                ? chunkBuffer
                : CreateTrimmedChunk(chunkBuffer, copyLength);

            SendChunk(transferId, chunkIndex, totalChunks, chunkBytes, recipientIds);
            sentChunks++;

            if (effectiveYieldEveryNChunks > 0 && sentChunks % effectiveYieldEveryNChunks == 0)
            {
                await Task.Yield();
            }
        }
    }

    private static byte[] CreateTrimmedChunk(byte[] source, int length)
    {
        var trimmed = new byte[length];
        Buffer.BlockCopy(source, 0, trimmed, 0, length);
        return trimmed;
    }

    private void SendBegin(VolumeNetworkProtocol.VolumeHeader header, IReadOnlyList<ulong> recipientIds)
    {
        var messaging = NetworkManager.Singleton?.CustomMessagingManager;
        if (messaging == null)
        {
            return;
        }

        for (var i = 0; i < recipientIds.Count; ++i)
        {
            using var writer = new FastBufferWriter(
                VolumeNetworkProtocol.EstimateBeginMessageSize(),
                Allocator.Temp);
            VolumeNetworkProtocol.WriteHeader(writer, header);
            SendVolumeMessage(
                messaging,
                VolumeNetworkProtocol.VolumeBeginMessageName,
                recipientIds[i],
                writer);
        }
    }

    private void SendChunk(
        uint transferId,
        ushort chunkIndex,
        ushort totalChunks,
        byte[] chunkBytes,
        IReadOnlyList<ulong> recipientIds)
    {
        var messaging = NetworkManager.Singleton?.CustomMessagingManager;
        if (messaging == null)
        {
            return;
        }

        using var writer = new FastBufferWriter(
            VolumeNetworkProtocol.EstimateChunkMessageSize(chunkBytes.Length),
            Allocator.Temp);
        VolumeNetworkProtocol.WriteChunk(writer, transferId, chunkIndex, totalChunks, chunkBytes);

        for (var i = 0; i < recipientIds.Count; ++i)
        {
            SendVolumeMessage(
                messaging,
                VolumeNetworkProtocol.VolumeChunkMessageName,
                recipientIds[i],
                writer);
        }
    }

    private void SendEnd(uint transferId, uint payloadCrc32, IReadOnlyList<ulong> recipientIds)
    {
        var messaging = NetworkManager.Singleton?.CustomMessagingManager;
        if (messaging == null)
        {
            return;
        }

        for (var i = 0; i < recipientIds.Count; ++i)
        {
            using var writer = new FastBufferWriter(sizeof(uint) * 2, Allocator.Temp);
            VolumeNetworkProtocol.WriteTransferId(writer, transferId);
            writer.WriteValueSafe(payloadCrc32);
            SendVolumeMessage(
                messaging,
                VolumeNetworkProtocol.VolumeEndMessageName,
                recipientIds[i],
                writer);
        }
    }

    private void BroadcastAbort(uint transferId)
    {
        if (!TryResolveRecipients(null, out var recipientIds))
        {
            return;
        }

        BroadcastAbort(transferId, recipientIds);
    }

    private void BroadcastAbort(uint transferId, IReadOnlyList<ulong> recipientIds)
    {
        var messaging = NetworkManager.Singleton?.CustomMessagingManager;
        if (messaging == null)
        {
            return;
        }

        for (var i = 0; i < recipientIds.Count; ++i)
        {
            using var writer = new FastBufferWriter(sizeof(uint), Allocator.Temp);
            VolumeNetworkProtocol.WriteTransferId(writer, transferId);
            SendVolumeMessage(
                messaging,
                VolumeNetworkProtocol.VolumeAbortMessageName,
                recipientIds[i],
                writer);
        }
    }

    private static void SendVolumeMessage(
        CustomMessagingManager messaging,
        string messageName,
        ulong clientId,
        FastBufferWriter writer)
    {
        messaging.SendNamedMessage(
            messageName,
            clientId,
            writer,
            VolumeNetworkProtocol.VolumeMessageDelivery);
    }

    private static bool TryResolveRecipients(ulong? targetClientId, out List<ulong> recipientIds)
    {
        recipientIds = new List<ulong>();
        var networkManager = NetworkManager.Singleton;
        if (networkManager == null || !networkManager.IsServer)
        {
            return false;
        }

        if (targetClientId.HasValue)
        {
            for (var i = 0; i < networkManager.ConnectedClientsIds.Count; ++i)
            {
                if (networkManager.ConnectedClientsIds[i] == targetClientId.Value)
                {
                    recipientIds.Add(targetClientId.Value);
                    break;
                }
            }

            return recipientIds.Count > 0;
        }

        recipientIds.AddRange(networkManager.ConnectedClientsIds);
        return recipientIds.Count > 0;
    }

    private static bool TryResolveSampleFileName(int sampleIndex, out string fileName)
    {
        fileName = string.Empty;
        var entries = FileBtnSpawner.GetSampleEntriesFromManifest();
        if (sampleIndex < 0 || sampleIndex >= entries.Count)
        {
            return false;
        }

        fileName = entries[sampleIndex];
        return !string.IsNullOrWhiteSpace(fileName);
    }

    private bool TryResolveReadableVolumePath(string sampleFileName, out string resolvedPath, out string error)
    {
        resolvedPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sampleFileName))
        {
            error = "sampleFileName is empty.";
            return false;
        }

        var normalizedEntry = sampleFileName.Replace('\\', '/').Trim().TrimStart('/');
        var sampleDirectory = Path.Combine(Application.streamingAssetsPath, "Sample");
        var candidatePath = Path.GetFullPath(Path.Combine(sampleDirectory, normalizedEntry));
        if (File.Exists(candidatePath) || Directory.Exists(candidatePath))
        {
            resolvedPath = candidatePath;
            return true;
        }

        error = $"volume path does not exist: {candidatePath}";
        return false;
    }

    private void ResolveDependencies()
    {
        if (volumeLoader == null)
        {
            volumeLoader = FindFirstObjectByType<SimpleITKVolumeLoader>();
        }

        if (processingController == null && volumeLoader != null)
        {
            volumeLoader.TryGetAssignedProcessingController(out processingController);
        }

        processingController ??= FindFirstObjectByType<SimpleITKProcessingController>();
    }
}
