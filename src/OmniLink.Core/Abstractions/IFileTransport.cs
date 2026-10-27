using System;
using System.Threading;
using System.Threading.Tasks;
using OmniLink.Core.Events;
using OmniLink.Core.FileExchange;

namespace OmniLink.Core.Abstractions;

/// <summary>
/// Abstraction สำหรับการควบคุมการโอนถ่ายไฟล์ข้ามเครื่อง (M4 / BATCH 5)
/// </summary>
public interface IFileTransport
{
    event Action<FileTransferProgressEvent>? OnProgress;
    event Action<FileTransferCompletedEvent>? OnCompleted;

    Task<bool> SendOfferAsync(string targetNodeId, FileManifest manifest, CancellationToken ct = default);
    Task<bool> StartStreamingOutboundAsync(string targetNodeId, string transferId, CancellationToken ct = default);
    void CancelTransfer(string transferId);
}
