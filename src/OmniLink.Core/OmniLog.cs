using System.Globalization;
using System.IO;
using System.Text;

namespace OmniLink.Core;

/// <summary>
/// ระบบบันทึก Log ระดับโปรเซสตามกฎเหล็กข้อ 15 และ TRAPS.md ข้อ 54 ของ AppGroupLauncher:
/// - ใช้ CultureInfo.InvariantCulture เสมอ ป้องกันปี พ.ศ. 2569 ทำชื่อไฟล์เรียงลำดับพัง
/// - มี PID + Thread ID + Build Stamp (UTC Ticks)
/// - ไล่ InnerException chain ครบทุกชั้น และห้ามโยน exception ออกไปทำโปรแกรมพังเด็ดขาด
/// </summary>
public static class OmniLog
{
    private static readonly object Gate = new();
    private static string _logDir = OmniPaths.LogsDir;
    private static long _buildStampTicks;
    private static volatile bool _diagnostics = true;
    private static volatile bool _silent; // A4: ใช้ใน unit tests กันปน production log

    /// <summary>A4: เรียกจาก unit tests — ปิดการเขียน log ทั้งหมดชั่วคราว</summary>
    public static void Silence() => _silent = true;

    public static long ExeUtcTicks(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return 0;
        try
        {
            if (!File.Exists(exePath)) return 0;
            return File.GetLastWriteTimeUtc(exePath).Ticks;
        }
        catch
        {
            return 0;
        }
    }

    public static void Configure(string? logDir = null)
    {
        lock (Gate)
        {
            _buildStampTicks = ExeUtcTicks(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(logDir))
            {
                _logDir = logDir;
            }
        }

        PruneOldLogs(30);
        var stampText = _buildStampTicks > 0
            ? new DateTime(_buildStampTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "?";
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        var verText = ver is not null ? $"v{ver.ToString(3)} " : "";
        Info($"=== start OmniLink {verText}pid={Environment.ProcessId} build={_buildStampTicks} ({stampText} UTC) clr={Environment.Version} os={Environment.OSVersion.VersionString} ===");
    }

    /// <summary>A4: ลบ log รายวันที่เก่ากว่า N วัน — กันโฟลเดอร์โตไม่จำกัด</summary>
    private static void PruneOldLogs(int keepDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-keepDays);
            foreach (var f in Directory.GetFiles(_logDir, "omnilink-*.log*"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff)
                    {
                        File.Delete(f);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public static void SetDiagnostics(bool enabled) => _diagnostics = enabled;

    public static void Write(string category, string message) => Write("INF", $"[{category}] {message}", requireDiagnostics: true);

    public static void Info(string message) => Write("INF", message, requireDiagnostics: true);

    public static void Warn(string message) => Write("WRN", message, requireDiagnostics: false);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            Write("ERR", message, requireDiagnostics: false);
            return;
        }

        var chain = new StringBuilder();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (chain.Length > 0) chain.Append(" <- ");
            chain.Append(current.GetType().Name).Append(": ").Append(current.Message);
        }

        if (ex.StackTrace is not null)
        {
            chain.AppendLine().Append(ex.StackTrace);
        }

        Write("ERR", $"{message} :: {chain}", requireDiagnostics: false);
    }

    private static StreamWriter? _writer;      // SV-M11: เปิดไฟล์ค้างไว้ (เดิมเปิด/ปิดต่อ "ทุกบรรทัด" ใต้ lock เดียวของโปรเซส)
    private static string _writerPath = "";
    private static long _maxLogBytes = 5_000_000; // SV-L5: cap ต่อไฟล์ (v1.8: ตั้งค่าได้จาก Advanced)
    private static int _minLevel = 3;              // v1.9: OFF=0 / ERR=1 / WRN=2 / INF=3 (ตั้งจาก Settings > Logging)

    /// <summary>v1.9: ตั้งระดับ log ต่ำสุดที่จะเขียน — "OFF"/"ERROR"/"WARN"/"INFO"</summary>
    public static void SetLevel(string? level)
    {
        _minLevel = (level ?? "").Trim().ToUpperInvariant() switch
        {
            "OFF" => 0,
            "ERROR" or "ERR" => 1,
            "WARN" or "WRN" => 2,
            _ => 3,
        };
    }

    /// <summary>v1.8: ตั้งขนาด log สูงสุดต่อไฟล์ (เรียกจาก Settings/Advanced หลังโหลด config)</summary>
    public static void SetMaxLogBytes(long bytes)
    {
        if (bytes is >= 500_000 and <= 50_000_000)
        {
            _maxLogBytes = bytes;
        }
    }

    private static void Write(string level, string message, bool requireDiagnostics)
    {
        if (_silent) return;
        if (requireDiagnostics && !_diagnostics) return;
        var severity = level switch { "ERR" => 1, "WRN" => 2, _ => 3 };
        if (severity > _minLevel) return;

        try
        {
            var line = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} [Host] pid={Environment.ProcessId} " +
                       $"tid={Environment.CurrentManagedThreadId} build={_buildStampTicks} {level} {message}{Environment.NewLine}";

            lock (Gate)
            {
                var path = Path.Combine(_logDir, $"omnilink-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

                if (_writer is null || _writerPath != path)
                {
                    try { _writer?.Dispose(); } catch { }
                    Directory.CreateDirectory(_logDir);
                    var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerPath = path;
                }

                // SV-L5: เกิน cap = ย้ายเป็น .1 แล้วเปิดไฟล์ใหม่
                if (_writer.BaseStream.Length > _maxLogBytes)
                {
                    _writer.Dispose();
                    var rotated = path + ".1";
                    try { File.Delete(rotated); } catch { }
                    File.Move(path, rotated);
                    var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
                }

                _writer.Write(line);
            }
        }
        catch
        {
            // กฎเหล็ก: Logger ห้ามแครชโปรแกรม — ทิ้ง writer ให้รอบหน้าเปิดใหม่
            try { lock (Gate) { _writer?.Dispose(); _writer = null; } } catch { }
        }
    }
}
