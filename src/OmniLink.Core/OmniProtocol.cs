using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniLink.Core;

// godclass-K7d: แยกจาก OmniConfig.cs เดิม (649 บรรทัดรวม 3 ประเภท) แบบ move-verbatim
public static partial class OmniProtocol
{
    public const int DiscoveryPort = 24800; // UDP Broadcast Beacon สำหรับค้นหาอุปกรณ์ในวง LAN
    public const int KvmTcpPort = 24801;
    public const int KvmUdpPort = 24802;

    public const int PlaybackRtpPort = 50044;
    public const int PlaybackRtcpSrPort = 50045;
    public const int PlaybackRtcpRrPort = 50046;

    public const int MicRtpPort = 50047;
    public const int MicRtcpSrPort = 50048;
    public const int MicRtcpRrPort = 50049;

    public const byte PktMouseMoveRel = 0x01;
    public const byte PktMouseMoveAbs = 0x02;
    public const byte PktMouseButton  = 0x03;
    public const byte PktMouseWheel   = 0x04;
    public const byte PktKeyEvent     = 0x05;
    public const byte PktSetLang      = 0x06;
    public const byte PktReleaseAll   = 0x07;

    public const byte ModLShift = 1 << 0;
    public const byte ModRShift = 1 << 1;
    public const byte ModLCtrl  = 1 << 2;
    public const byte ModRCtrl  = 1 << 3;
    public const byte ModLAlt   = 1 << 4;
    public const byte ModRAlt   = 1 << 5;
    public const byte ModLSuper = 1 << 6;
    public const byte ModRSuper = 1 << 7;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static int WriteMouseRel(Span<byte> destination, int dx, int dy, byte modMask)
    {
        destination[0] = PktMouseMoveRel;
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(1, 2), (short)Math.Clamp(dx, -32767, 32767));
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(3, 2), (short)Math.Clamp(dy, -32767, 32767));
        destination[5] = modMask;
        return 6;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static int WriteMouseButton(Span<byte> destination, byte button, bool down, byte modMask)
    {
        destination[0] = PktMouseButton;
        destination[1] = button;
        destination[2] = (byte)(down ? 1 : 0);
        destination[3] = modMask;
        return 4;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static int WriteMouseWheel(Span<byte> destination, int wx, int wy, byte modMask)
    {
        destination[0] = PktMouseWheel;
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(1, 2), (short)Math.Clamp(wx, -32767, 32767));
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(3, 2), (short)Math.Clamp(wy, -32767, 32767));
        destination[5] = modMask;
        return 6;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static int WriteKeyEvent(Span<byte> destination, int x11Keycode, bool down, byte modMask)
    {
        destination[0] = PktKeyEvent;
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(1, 2), (ushort)Math.Clamp(x11Keycode, 0, 65535));
        destination[3] = (byte)(down ? 1 : 0);
        destination[4] = modMask;
        return 5;
    }

    public static byte[] PackMouseRel(int dx, int dy, byte modMask)
    {
        var buf = new byte[6];
        WriteMouseRel(buf, dx, dy, modMask);
        return buf;
    }

    public static byte[] PackMouseButton(byte button, bool down, byte modMask)
    {
        var buf = new byte[4];
        WriteMouseButton(buf, button, down, modMask);
        return buf;
    }

    public static byte[] PackMouseWheel(int wx, int wy, byte modMask)
    {
        var buf = new byte[6];
        WriteMouseWheel(buf, wx, wy, modMask);
        return buf;
    }

    public static byte[] PackKeyEvent(int x11Keycode, bool down, byte modMask)
    {
        var buf = new byte[5];
        WriteKeyEvent(buf, x11Keycode, down, modMask);
        return buf;
    }

    public static byte[] PackSetLang(int group, bool calibrate = true)
    {
        return new byte[] { PktSetLang, (byte)(group == 1 ? 1 : 0), (byte)(calibrate ? 1 : 0) };
    }

    public static byte[] PackReleaseAll()
    {
        return new byte[] { PktReleaseAll, 0 };
    }

    public static byte[] EncodeTcpJson(object obj)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(obj);
        var buf = new byte[4 + jsonBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)jsonBytes.Length);
        Buffer.BlockCopy(jsonBytes, 0, buf, 4, jsonBytes.Length);
        return buf;
    }

}
