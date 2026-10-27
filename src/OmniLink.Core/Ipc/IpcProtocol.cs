using System.IO;
using System.Text;
using System.Text.Json;

namespace OmniLink.Core.Ipc;

/// <summary>
/// S2 (Engine/UI split): โปรโตคอลท่อ IPC ระหว่าง engine (hostd) ↔ dashboard (WPF)
/// สรุปสัญญา: _lessons/war-room/IPC-PROTOCOL-CONTRACT-S0.md — framing [4-byte LE length][UTF-8 JSON]
/// envelope เดียวทั้ง request/response/event · handshake v1 ก่อนใช้งาน · cap 1MB ต่อ frame
/// </summary>
public static class IpcProtocol
{
    public const int Version = 1;
    public const string DefaultPipeName = "omnilink-engine-ui-v1";
    public const string AppEngine = "omnilink-hostd";
    public const string AppUi = "omnilink-ui";
    public const int MaxFrameBytes = 1_048_576;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = false,
    };

    public static void WriteFrame(Stream stream, IpcMessage msg)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(msg, JsonOptions);
        if (body.Length > MaxFrameBytes)
        {
            throw new IOException($"ipc frame too large: {body.Length}");
        }
        var len = BitConverter.GetBytes(body.Length);
        stream.Write(len, 0, 4);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    /// <summary>อ่าน 1 frame (block) — คืน null เมื่อปลายท่อปิด · โยน IOException เมื่อ frame ผิดรูป</summary>
    public static IpcMessage? ReadFrame(Stream stream)
    {
        var lenBuf = ReadExact(stream, 4);
        if (lenBuf is null) return null;
        var len = BitConverter.ToInt32(lenBuf, 0);
        if (len <= 0 || len > MaxFrameBytes)
        {
            throw new IOException($"ipc frame length ผิดปกติ: {len}");
        }
        var body = ReadExact(stream, len);
        if (body is null) return null;
        return JsonSerializer.Deserialize<IpcMessage>(body, JsonOptions);
    }

    private static byte[]? ReadExact(Stream stream, int count)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buf, read, count - read);
            if (n <= 0)
            {
                return read == 0 ? null : throw new IOException("ipc stream จบกลาง frame");
            }
            read += n;
        }
        return buf;
    }

    public static IpcMessage Ok(object? data = null) => new() { Ok = true, Data = ToElement(data) };
    public static IpcMessage Fail(string err) => new() { Ok = false, Err = err };

    public static JsonElement? ToElement(object? payload)
    {
        if (payload is null) return null;
        return JsonSerializer.SerializeToElement(payload, JsonOptions);
    }
}

/// <summary>envelope เดียวของทุกทิศ: request = {id,op,args} · response = {id,ok,data,err} · event = {ev,data} · handshake = {v,role,app,ver}</summary>
public sealed class IpcMessage
{
    public int? V { get; set; }
    public string? Role { get; set; }
    public string? App { get; set; }
    public string? Ver { get; set; }
    public int? Id { get; set; }
    public string? Op { get; set; }
    public JsonElement? Args { get; set; }
    public bool? Ok { get; set; }
    public string? Err { get; set; }
    public JsonElement? Data { get; set; }
    public string? Ev { get; set; }
}

// ── wire DTO (แยกจาก in-memory type จริง — เลี่ยง ValueTuple/enum-key บน JSON) ──

public sealed class IpcWireCell
{
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class IpcWireTopology
{
    public Dictionary<string, IpcWireCell> Cells { get; set; } = new();
    /// <summary>nodeId → (edge เป็นชื่อ enum) → neighbor id</summary>
    public Dictionary<string, Dictionary<string, string>> Edges { get; set; } = new();
}

public sealed class IpcWireAudio
{
    public bool StreamRunning { get; set; }
    public bool MicRunning { get; set; }
    public List<AudioMicInfo> Mics { get; set; } = new();
}

public sealed class AudioMicInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class IpcWireRect
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

public sealed class IpcWireState
{
    public string? ActiveNode { get; set; }
    public int MasterLang { get; set; }
    public IpcWireRect Virtual { get; set; } = new() { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
    public IpcWireTopology? Topology { get; set; }
    public List<NodeSessionView> Nodes { get; set; } = new();
    public IpcWireAudio? Audio { get; set; }
    public string? AppVersion { get; set; }
    public int Pid { get; set; }
}
