namespace OmniLink.Core.Abstractions;

/// <summary>
/// Callback Interface สำหรับการสื่อสารระหว่าง RemoteNodeClient และ KvmHostService (ISP / V4 Refactor)
/// รวม delegate สัญญาย่อยๆ เข้าเป็น Interface สัญญาเดียวเพื่อลด Coupling
/// </summary>
public interface IHostLinkCallbacks
{
    void OnEdgeReturn(string nodeId, string edge, double normY, double normX = 0.5);

    /// <summary>v1.9.1: agent ขอสลับไป node อื่น (chain topology) — edge/norm เอาไว้วางตำแหน่งเคอร์เซอร์บนเครื่องปลายทาง</summary>
    void OnSwitchRequestReceived(string nodeId, string target, string edge, double normX, double normY) { }
    void OnConnected(string nodeId);
    void OnDisconnected(string nodeId);
    void OnClipboardReceived(string mimeType, byte[] data);
    void OnStateChanged(string nodeId, string state);
    void OnHostnameDiscovered(string nodeId, string hostname) { }
    void OnLegacyAgent(string nodeId, string agentVersion) { }
    void OnFocusedReported(string nodeId, bool focused) { }
    bool TryGetPendingClip(string token, out (byte[] data, string mime) clip)
    {
        clip = default;
        return false;
    }

    void OnFileOfferReceived(string nodeId, string manifestJson) { }

    /// <summary>C2d: node ขอรายการ metadata ของ clipboard history (host/sub ตอบ history_list)</summary>
    void OnHistoryRequestReceived(string nodeId) { }

    /// <summary>C2d: node ขอเนื้อหาเต็มรายการเดียว (host/sub ตอบ history_entry)</summary>
    void OnHistoryGetReceived(string nodeId, string id) { }
    void OnFileAcceptReceived(string nodeId, string transferId) { }
    void OnFileChunkReceived(string nodeId, string transferId, int fileIndex, long offset, string base64Data) { }
    void OnFileDoneReceived(string nodeId, string transferId, int fileIndex) { }
    void OnFileResultReceived(string nodeId, string transferId, int fileIndex, string status) { }
    void OnFileCompleteReceived(string nodeId, string transferId, bool success) { }

    int GetAdaptivePingIntervalMs(string nodeId) => 1500;
    void OnAudioActivityReported(string nodeId, bool active) { }
}
