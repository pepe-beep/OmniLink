using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using OmniLink.Core;
using OmniLink.Interop;
using OmniLink.Views;

namespace OmniLink;

internal static class Program
{
    private const string MutexName = @"Local\OmniLink_SingleInstance_Mutex";
    private const string PipeName = @"OmniLink_SingleInstance_Pipe";
    private static Mutex? _singleInstanceMutex;
    private static int _dispatcherCrashCount;

    /// <summary>
    /// ส่งสัญญาณ "SHOW" ไปยัง instance ที่ทำงานอยู่ (Trap #2: ผู้เรียกคือผู้ถือ foreground right
    /// จึงต้อง AllowSetForegroundWindow ให้ server ก่อน) — คืน true เมื่อสื่อสารสำเร็จ
    /// </summary>
    private static bool TrySignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1000);
            if (NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var serverPid))
            {
                NativeMethods.AllowSetForegroundWindow((int)serverPid);
            }
            else
            {
                NativeMethods.AllowSetForegroundWindow(-1);
            }
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("SHOW");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>บันทึก crash ลง logs/crash.log (SSOT เดียวของโปรแกรม) ห้ามโยน exception</summary>
    internal static void LogCrash(string kind, object ex)
    {
        try { File.AppendAllText(OmniPaths.CrashLogPath, $"[{DateTime.Now:O}] [{kind}] {ex}\n---\n"); } catch { }
        try { OmniLog.Error($"crash/{kind}: {ex}"); } catch { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("Unhandled", e.ExceptionObject);

        // v2.0: เสียบตัวค้นหา gst ของ Windows ก่อนโหลด config ครั้งแรก (แกนกลาง default = ชื่อไฟล์จาก PATH)
        OmniLink.Core.OmniPaths.DiscoverGstExe = OmniLink.Platform.Windows.GstLocator.DiscoverGstreamerExe;

        // Trap #1: Set AUMID on the very first line of Main() before any window, COM, or UI is initialized
        try
        {
            NativeMethods.SetCurrentProcessExplicitAppUserModelID("PepeBeep.OmniLink");
        }
        catch
        {
        }

        // Trap #3 (แก้กลไกให้ถูกต้อง): AME เกิดจาก WaitOne เท่านั้น — ถ้าเจ้าของเดิมตาย
        // WaitOne จะโยน AbandonedMutexException พร้อมส่งสิทธิ์ให้เราทันที หรือ mutex ถูกทำลายไปเลย
        _singleInstanceMutex = new Mutex(initiallyOwned: false, MutexName);
        bool ownsMutex;
        try
        {
            ownsMutex = _singleInstanceMutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true; // เจ้าของเดิมแครชตาย — เรายึดครองต่อได้เลย
        }

        if (!ownsMutex)
        {
            // มี instance ที่มีชีวิตอยู่จริง — ส่งสัญญาณให้เด้งหน้าต่างขึ้นมา
            var woke = TrySignalExistingInstance();
            if (!woke)
            {
                // Mutex ถูกถือแต่ pipe ไม่ตอบ = เจ้าของ hung — ต้องบอกผู้ใช้ ห้ามออกเงียบๆ
                try
                {
                    MessageBox.Show(
                        OmniLink.Core.Loc.T("crash.hung.body",
                        "OmniLink กำลังทำงานอยู่แต่ไม่ตอบสนอง\n\nโปรดปิดโปรเซสเดิมจาก Task Manager แล้วเปิดโปรแกรมใหม่อีกครั้ง"),
                        "OmniLink",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                catch
                {
                }
            }
            return 0;
        }

        try
        {
            OmniLog.Configure();
            var gcInfo = GC.GetGCMemoryInfo();
            var appVer = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?";
            OmniLog.Write("boot", $"OmniLink v{appVer} starting pid={Environment.ProcessId} dpi_pmv2={NativeMethods.IsPerMonitorV2()} GC_Managed={GC.GetTotalMemory(false) / 1024 / 1024}MB Committed={gcInfo.TotalCommittedBytes / 1024 / 1024}MB Heap={gcInfo.HeapSizeBytes / 1024 / 1024}MB");

            // Low-Memory Profile Check (Acer E5-411 / 2GB RAM / Low-spec PC)
            try
            {
                var totalRam = NativeMethods.GetTotalPhysicalMemoryBytes();
                var cfg = OmniConfig.Load();
                var isLowRam = totalRam > 0 && totalRam <= 4_294_967_296UL; // <= 4GB RAM (e.g. 2GB Celeron Acer E5-411)
                if (isLowRam || cfg.LowMemoryMode)
                {
                    // v1.8 Low-Mem v2: RAM <= 4GB = เปิดเอง "ปิดไม่ได้" (PO decision) · RAM > 4GB = opt-in
                    // แต่ละ sub-toggle (ปรับใน Advanced) คุมส่วนย่อยแยกกันได้
                    var applied = new List<string>();
                    var mode = isLowRam ? "auto(<=4GB)" : "manual";

                    // 1. Force WPF Software Rendering to save 35-50MB D3D surface allocation on shared Intel HD VRAM
                    if (cfg.LowMemSoftwareRender)
                    {
                        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                        applied.Add("SoftwareOnly");
                    }

                    // 2. Cap ThreadPool to avoid over-reserving thread stacks (1MB per stack) on 2GB RAM machines
                    if (cfg.LowMemThreadPoolCap)
                    {
                        ThreadPool.SetMinThreads(2, 2);
                        ThreadPool.SetMaxThreads(8, 8);
                        applied.Add("ThreadPool 2-8");
                    }

                    // 3. Optimize GC latency mode for non-interactive background throughput
                    if (cfg.LowMemGcBatch)
                    {
                        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Batch;
                        applied.Add("GCBatch");
                    }

                    OmniLog.Write("boot", $"[LowMem] profile ACTIVE mode={mode} (RAM: {totalRam / 1024 / 1024}MB, LowMemMode={cfg.LowMemoryMode}) applied=[{string.Join(", ", applied)}]");
                }
                else
                {
                    OmniLog.Write("boot", "[LowMem] profile OFF (RAM > 4GB, LowMemoryMode=false)");
                }
            }
            catch (Exception ex)
            {
                OmniLog.Warn($"[LowMem] Failed to configure low-memory profile: {ex.Message}");
            }

            // Trap #5: Check elevation / UIPI restrictions
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    OmniLog.Warn("[UIPI] Running as Administrator is not recommended; Windows will block standard UIPI messages.");
                }
            }
            catch
            {
            }

            // v2.0: เสียบ codec ภาพของ Windows (WPF imaging) ให้แกนกลาง — ต้องก่อนสร้าง window แรก
            OmniLink.Core.Abstractions.OmniImage.Codec = new OmniLink.Platform.Windows.WindowsImageCodec();

            var app = new App();
            app.InitializeComponent();

            // Trap #6: Show window in < 50ms (Apple-grade perceived performance)
            var mainWin = new MainWindow();
            app.MainWindow = mainWin;
            app.DispatcherUnhandledException += (_, e) =>
            {
                LogCrash("Dispatcher", e.Exception);
                // C9: กลืนได้แต่มีวงจร้ำ — ถ้าพังซ้ำมากเกินไปแปลว่า state เสียหาย ปล่อยตายดีกว่าเป็น zombie
                if (Interlocked.Increment(ref _dispatcherCrashCount) > 20)
                {
                    OmniLog.Error("[crash] Dispatcher exception เกิน 20 ครั้ง — ยอมให้โปรแกรมปิดตัว");
                    e.Handled = false;
                    return;
                }
                try { mainWin.ShowBannerNow(OmniLink.Core.Loc.T("banner.internalerror", "⚠ เกิดข้อผิดพลาดภายใน (ดูรายละเอียดใน log): {0}", e.Exception.Message)); } catch { }
                e.Handled = true;
            };

            StartSingleInstancePipeServer(mainWin);

            mainWin.Show();
            return app.Run();
        }
        catch (Exception ex)
        {
            LogCrash("Main", ex);
            return 1;
        }
    }

    private static void StartSingleInstancePipeServer(MainWindow mainWin)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var cmd = reader.ReadLine();
                    if (string.Equals(cmd, "SHOW", StringComparison.OrdinalIgnoreCase))
                    {
                        mainWin.ShowAndActivateWindow();
                    }
                }
                catch
                {
                    Thread.Sleep(400);
                }
            }
        })
        {
            IsBackground = true,
            Name = "OmniLink_PipeServer",
        };
        t.Start();
    }
}
