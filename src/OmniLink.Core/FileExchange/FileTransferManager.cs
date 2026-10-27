using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Events;

namespace OmniLink.Core.FileExchange;

/// <summary>
/// ตัวควบคุมการโอนถ่ายไฟล์ระดับสูง เชื่อมโยง Network Transports, Clipboard และ EventHub
/// </summary>
public sealed class FileTransferManager : IFileTransport
{
    private readonly ChunkedFileStreamer _streamer;
    private readonly IOmniEventHub _eventHub;
    private readonly ConcurrentDictionary<string, Func<object, bool>> _nodeSenders = new();

    public event Action<FileTransferProgressEvent>? OnProgress;
    public event Action<FileTransferCompletedEvent>? OnCompleted;

    public ChunkedFileStreamer Streamer => _streamer;

    public FileTransferManager(IOmniEventHub eventHub)
    {
        _eventHub = eventHub;
        _streamer = new ChunkedFileStreamer(eventHub);

        _eventHub.Subscribe<FileTransferProgressEvent>(e => OnProgress?.Invoke(e));
        _eventHub.Subscribe<FileTransferCompletedEvent>(e => OnCompleted?.Invoke(e));
    }

    public void RegisterNodeSender(string nodeId, Func<object, bool> sendTcpFunc)
    {
        _nodeSenders[nodeId] = sendTcpFunc;
    }

    public void UnregisterNodeSender(string nodeId)
    {
        _nodeSenders.TryRemove(nodeId, out _);
    }

    public Task<bool> SendOfferAsync(string targetNodeId, FileManifest manifest, CancellationToken ct = default)
    {
        if (_nodeSenders.TryGetValue(targetNodeId, out var sendFunc))
        {
            var sent = sendFunc(new
            {
                type = "file_offer",
                manifest = manifest.ToJson()
            });
            return Task.FromResult(sent);
        }
        return Task.FromResult(false);
    }

    public async Task<bool> StartStreamingOutboundAsync(string targetNodeId, string transferId, CancellationToken ct = default)
    {
        if (_nodeSenders.TryGetValue(targetNodeId, out var sendFunc))
        {
            return await _streamer.StreamOutboundAsync(transferId, sendFunc, ct);
        }
        return false;
    }

    public void CancelTransfer(string transferId)
    {
        _streamer.CancelTransfer(transferId);
    }
}
