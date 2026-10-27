using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmniLink.Core;

/// <summary>
/// S1 (Engine/UI split): ย้ายมาจาก OmniLink.Interop.NativeMethods (Platform.Windows) —
/// AudioHubService ย้ายลง Core แต่ยังต้องอ่าน parent PID เพื่อกำจัด gst orphan (D5/KillOrphanedAudioFromPreviousRun)
/// คัดลอกพฤติกรรมเดิม 100% · P/Invoke นี้คอมไพล์ได้ทุก TFM และถูกเรียกจริงเฉพาะ path ของ Windows เท่านั้น
/// </summary>
internal static class ProcessParentPid
{
    /// <summary>PROCESS_BASIC_INFORMATION (ทุกฟิลด์เป็น pointer-size ตาม padding จริงของทั้ง x86/x64)</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(nint processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    public static int GetParentProcessId(Process p)
    {
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(p.Handle, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
            return status == 0 ? (int)pbi.InheritedFromUniqueProcessId : -1;
        }
        catch
        {
            return -1;
        }
    }
}
