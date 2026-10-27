using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace OmniLink.Core;

/// <summary>
/// ย้ายออกจาก KvmHostService (godclass-K2): กลุ่ม KNOCK — UDP reverse-knock listener
/// (agent เครื่องลูกเคาะมาให้ host ปลุก session ที่ถูก freeze/cold กลับมาเชื่อมทันที)
/// โค้ดย้ายแบบ move-verbatim จาก KvmHostService.cs เดิม; _running ถูกแทนด้วย isRunning delegate
/// </summary>
internal sealed class ReverseKnockListener : IDisposable
{
    private readonly ConcurrentDictionary<string, RemoteNodeClient> _sessions;
    private readonly Func<bool> _isRunning;
    private UdpClient? _listener;

    public ReverseKnockListener(ConcurrentDictionary<string, RemoteNodeClient> sessions, Func<bool> isRunning)
    {
        _sessions = sessions;
        _isRunning = isRunning;
    }

    public void Start()
    {
        try
        {
            var listener = new UdpClient();
            listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Client.ExclusiveAddressUse = false;
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, OmniProtocol.DiscoveryPort));
            _listener = listener;

            _ = Task.Run(LoopAsync);
            OmniLog.Write("knock", $"Reverse Knock UDP listener started on port {OmniProtocol.DiscoveryPort}");
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[knock] Cannot bind Reverse Knock listener on UDP {OmniProtocol.DiscoveryPort}: {ex.Message}");
        }
    }

    private async Task LoopAsync()
    {
        while (_isRunning() && _listener != null)
        {
            try
            {
                var result = await _listener.ReceiveAsync().ConfigureAwait(false);
                var data = result.Buffer;
                if (data == null || data.Length == 0) continue;

                var text = Encoding.UTF8.GetString(data);
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("cmd", out var cmdProp) && cmdProp.GetString() == "KNOCK_KNOCK")
                {
                    string? nodeId = doc.RootElement.TryGetProperty("node_id", out var np) ? np.GetString() : null;
                    OmniLog.Write("knock", $"Received KNOCK_KNOCK from {result.RemoteEndPoint.Address} node={nodeId}");

                    bool woken = false;
                    if (!string.IsNullOrEmpty(nodeId) && _sessions.TryGetValue(nodeId, out var sess))
                    {
                        sess.WakeUp();
                        woken = true;
                    }
                    else
                    {
                        var ipStr = result.RemoteEndPoint.Address.ToString();
                        foreach (var s in _sessions.Values)
                        {
                            if (s.Config.Ip == ipStr || s.Config.NodeId == nodeId)
                            {
                                s.WakeUp();
                                woken = true;
                            }
                        }
                    }

                    if (!woken)
                    {
                        foreach (var s in _sessions.Values)
                        {
                            if (s.LifecycleState == ConnectionLifecycleState.Freeze)
                            {
                                s.WakeUp();
                            }
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (!_isRunning())
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_isRunning()) break;
                OmniLog.Warn($"[knock] Receive error: {ex.Message}");
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        try { _listener?.Close(); } catch { }
        _listener = null;
    }
}
