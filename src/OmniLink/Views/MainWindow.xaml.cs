using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Clipboard;
using OmniLink.Core.Events;
using OmniLink.Core.Ipc;
using OmniLink.Core.Strategies;
using OmniLink.Interop;
using OmniLink.ViewModels;
using OmniLink.Views.Settings;

namespace OmniLink.Views;

public sealed record NodeListRow(
    string NodeId,
    ImageSource Icon,
    string DisplayTitle,
    string SubTitle,
    string TelemetryLine,
    string StatusShort,
    Brush StatusBadgeBg,
    Brush StatusBadgeFg);

public sealed record SettingsSectionRow(string Symbol, string Title);

public partial class MainWindow : Window, ISettingsHost
{
    public HistoryViewModel HistoryVM { get; }

    private readonly OmniConfig _config;
    private readonly IOmniEventHub _eventHub;
    private bool _dashboard; // S3: ui_role=dashboard — UI เป็น client ของ engine (hostd) · godclass-G2: อาจถูกตั้งอัตโนมัติเมื่อ engine ถูกครอง
    private readonly IClipboardBridge _clipboardBridge;
    private readonly OmniLink.Platform.Windows.WindowsHostInput _hostInput = null!;
    private OmniLink.Core.NodeAgentServer? _subServer;
    private readonly IKvmHost _kvm;
    private readonly AudioHubService? _audio;
    private readonly OmniDiscoveryService _discovery;
    private readonly OmniTrayService? _tray;
    private readonly DispatcherTimer _uiTimer;
    private readonly System.Threading.Timer _idleTrimTimer;
    private readonly DispatcherTimer _configSaveDebounce; // E1: รวมการเซฟ config ที่ถี่ให้เป็นช่วงๆ ไม่ลงดิสก์ทุก tick
    private readonly StatusJsonReporter _statusJson = new(); // godclass-M1
    private readonly ClipboardHistoryStore _clipboardHistory; // C2: ประวัติคลิปบอร์ด persistent (แชร์กับ HistoryVM)
    private long _clipSuppressUntilTick; // C2 echo suppression: ช่วงเวลาที่ "เราตั้ง clipboard เอง" — ไม่จับกลับ
    private bool _initializingControls = true;
    private bool _allowExit;
    private bool _exitStarted;
    private string? _lastTransferFolder;

    // v1.9: หน้า Settings ย่อย 9 หน้า — popup AdvancedSettingsWindow ถูกลบไปแล้ว

    public MainWindow()
    {
        _config = OmniConfig.Load();
        // v1.9 i18n: ตั้งภาษา UI ก่อน InitializeComponent เสมอ เพื่อให้ DynamicResource resolve ตั้งแต่แรก
        LocalizationService.Initialize(_config.UiLanguage);
        // v2.0: สลับ palette ธีมก่อน InitializeComponent ให้ DynamicResource ได้สีถูกตั้งแต่เฟรมแรก
        OmniLink.Themes.ThemeService.Apply(_config.ThemeMode);
        OmniLog.SetLevel(_config.LogLevel);
        OmniLog.SetMaxLogBytes(_config.LogMaxBytes);
        OmniLog.SetDiagnostics(_config.DiagnosticsEnabled);
        if (_config.PreventSleep)
        {
            PreventSleepService.SetEnabled(true); // v1.9 C4: เธรด keeper ค้าง ES_CONTINUOUS ตลอดที่เปิดใช้
        }
        _eventHub = OmniEventHub.Default;
        _dashboard = string.Equals(_config.UiRole, "dashboard", StringComparison.OrdinalIgnoreCase);
        _clipboardBridge = new WindowsClipboardBridge();

        _clipboardHistory = new ClipboardHistoryStore(OmniPaths.RunStateDir, m => OmniLog.Warn(m));
        HistoryVM = new HistoryViewModel(_clipboardHistory);
        DataContext = this;

        // callback ของ discovery จะยิงหลังหน้า Settings ถูกสร้างแล้วเสมอ (scan เกิดหลังบูตเท่านั้น)
        _discovery = new OmniDiscoveryService(_config, node => Dispatcher.BeginInvoke(() => _securityPage?.OnLiveNodeDiscovered(node)));
        _hostInput = new OmniLink.Platform.Windows.WindowsHostInput();
        if (_dashboard)
        {
            // S3: engine อยู่ที่ hostd — UI เป็น dashboard client (ไม่ติด hook ไม่แย่งพอร์ต 24801)
            var ipc = new IpcKvmClient();
            ipc.StateChanged += () => ScheduleUiRefresh();
            ipc.ConnectedChanged += () => Dispatcher.BeginInvoke(() => SetBanner(ipc.IsConnected
                ? Loc.T("banner.dashboard.connected", "โหมด dashboard: เชื่อม engine (hostd) สำเร็จ")
                : Loc.T("banner.dashboard.offline", "โหมด dashboard: engine ไม่ตอบ — กำลังลองเชื่อมต่อใหม่...")));
            _kvm = ipc;
        }
        else
        {
            // godclass-G2: ครองสิทธิ์ engine เดียวของเครื่อง — ถ้า hostd (autostart) รันอยู่ ให้กลับเป็น dashboard อัตโนมัติ
            if (!EngineSingleton.TryAcquire())
            {
                OmniLog.Warn("[role] ui_role=engine แต่มี engine อีกตัวรันอยู่ (hostd?) → ทำงานเป็น dashboard แทน");
                _dashboard = true;
                var ipcf = new IpcKvmClient();
                ipcf.StateChanged += () => ScheduleUiRefresh();
                ipcf.ConnectedChanged += () => Dispatcher.BeginInvoke(() => SetBanner(ipcf.IsConnected
                    ? Loc.T("banner.dashboard.connected", "โหมด dashboard: เชื่อม engine (hostd) สำเร็จ")
                    : Loc.T("banner.dashboard.offline", "โหมด dashboard: engine ไม่ตอบ — กำลังลองเชื่อมต่อใหม่...")));
                _kvm = ipcf;
            }
            else
            {
                _kvm = new KvmHostService(_config, ScheduleUiRefresh, _eventHub, _hostInput);
            }
        }

        // v2.0 เฟส 3: โหมด sub ฝัง — เครื่องนี้ถูกคุมโดย host ตัวอื่นได้ (กันลูปผ่าน SubActiveFocusProbe)
        if (_config.SubModeEnabled && !_dashboard)
        {
            StartSubMode();
        }
        // B5: service แจ้งผ่าน callback — MainWindow บน UI thread เป็นผู้เขียน config คนเดียวเสมอ
        _audio = _dashboard ? null : new AudioHubService(
            _config,
            _kvm.SendNodeCommand,
            _kvm.GetLocalEndpointIpForNode,
            d => UpdateConfigOnUi(c =>
            {
                c.MicDeviceId = d.Id;
                c.MicDeviceName = d.Name;
            }));

        if (!_dashboard)
        {
            // Wire Smart Audio & Smart Focus-Aware Mic
            _kvm.OnNodeAudioActivityChanged = (nodeId, active) => _audio!.HandleNodeAudioActivity(nodeId, active);
            _kvm.OnFocusMicAction = (start) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (start)
                    {
                        if (!AudioMicRunningNow)
                        {
                            OmniLog.Write("audio", "[smart] Focus บนเครื่องลูก -> เปิดไมค์อัตโนมัติ");
                            AudioStartMic();
                        }
                    }
                    else
                    {
                        if (AudioMicRunningNow)
                        {
                            OmniLog.Write("audio", "[smart] กลับมาทำงานบน Host นานเกินกำหนด -> พักไมค์อัตโนมัติ (ประหยัดพลังงาน)");
                            AudioStopMic();
                        }
                    }
                    ScheduleUiRefresh();
                });
            };

            // v1.9 C1: hotkey ขอ toggle เสียง — engine จับปุ่มแล้วส่งต่อ (เสียงเป็นของฝั่งนี้ในโหมด engine)
            _eventHub.Subscribe<HotkeyAudioRequestedEvent>(e =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (e.ActionType == HotkeyActionConfig.ToggleStream)
                    {
                        OnToggleStreamClick(this, new RoutedEventArgs());
                    }
                    else if (e.ActionType == HotkeyActionConfig.ToggleMic)
                    {
                        OnToggleMicClick(this, new RoutedEventArgs());
                    }
                });
            });

            _eventHub.Subscribe<NodeConnectionEvent>(e =>
            {
                if (e.Connected)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_config.AutoStartStream && !AudioStreamRunningNow)
                        {
                            OmniLog.Write("audio", "node online → retry start_stream (auto)");
                            try { AudioStartStream(); } catch (Exception ex) { OmniLog.Warn("[audio] retry stream: " + ex.Message); }
                        }
                        if (_config.AutoStartMic && !AudioMicRunningNow)
                        {
                            OmniLog.Write("audio", "node online → retry start_mic (auto)");
                            try { AudioStartMic(); } catch (Exception ex) { OmniLog.Warn("[audio] retry mic: " + ex.Message); }
                        }
                    });
                }
            });

            // C2: hotkey history_picker → เปิด picker window (ตาม spec: กดที่ไหนก็ได้)
            _eventHub.Subscribe<HotkeyHistoryRequestedEvent>(_ =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var picker = new ClipboardPickerWindow(this, _kvm, _clipboardBridge, _clipboardHistory)
                        {
                            Owner = this,
                        };
                        picker.Show();
                        picker.Activate();
                    }
                    catch (Exception ex) { OmniLog.Warn("[picker] เปิดไม่ได้: " + ex.Message); }
                });
            });
        }

        _eventHub.Subscribe<StatusBannerEvent>(e =>
        {
            Dispatcher.BeginInvoke(() => SetBanner(e.Message));
        });

        _eventHub.Subscribe<ClipboardReceivedEvent>(e =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    HistoryVM.AddEntry("text/plain", e.Text, null, e.SourceNodeId);
                    var cur = _clipboardBridge.GetText();
                    if (cur != e.Text)
                    {
                        _clipboardBridge.SetText(e.Text);
                    }
                }
                catch { }
            });
        });

        // Wire File Transfer Progress & Completed Events to Fluent Progress Bar
        _eventHub.Subscribe<FileTransferProgressEvent>(e =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    FileTransferCard.Visibility = Visibility.Visible;
                    FileTransferFileNameText.Text = string.IsNullOrEmpty(e.FileName) ? Loc.T("files.transferring", "Transferring file...") : e.FileName;
                    FileTransferProgressBar.Value = Math.Clamp(e.Percentage, 0.0, 100.0);
                    FileTransferMetaText.Text = $"{e.Percentage:F1}% — {FormatByteSize(e.BytesTransferred)} / {FormatByteSize(e.TotalBytes)}";
                }
                catch { }
            });
        });

        _eventHub.Subscribe<FileTransferCompletedEvent>(e =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    FileTransferCard.Visibility = Visibility.Visible;
                    if (e.Success)
                    {
                        FileTransferFileNameText.Text = Loc.T("files.completed", "โอนไฟล์สำเร็จ ({0} ไฟล์)", e.FileCount);
                        FileTransferProgressBar.Value = 100.0;
                        FileTransferMetaText.Text = Loc.T("files.savedto", "บันทึกแล้ว {0}", FormatByteSize(e.TotalBytes));
                        _lastTransferFolder = e.DestinationPath;
                        BtnOpenTransferFolder.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        FileTransferFileNameText.Text = Loc.T("files.failed", "โอนไฟล์ไม่สำเร็จ");
                        FileTransferMetaText.Text = e.Error ?? Loc.T("files.interrupted", "การโอนถูกขัดจังหวะ");
                        BtnOpenTransferFolder.Visibility = Visibility.Collapsed;
                    }
                }
                catch { }
            });
        });

        // Clipboard Bridge (TASK-004, TASK-008, TASK-009): ใช้อ่านคลิปบอร์ดแบบ STA พร้อม retry
        _kvm.HostClipboardGetter = () => _clipboardBridge.GetText();
        _kvm.HostPngGetter = () =>
        {
            if (_clipboardBridge.TryGetPng(out var png) && png.Length > 0)
            {
                return png;
            }
            return null;
        };
        _kvm.OnClipboardFromClient = text =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var cur = _clipboardBridge.GetText();
                    if (cur != text)
                    {
                        SuppressClipboardCapture(); // C2: เราตั้งเอง — ไม่จับกลับ
                        _clipboardBridge.SetText(text);
                    }
                }
                catch { }
            });
        };
        _kvm.OnClipboardImageFromClient = png =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    HistoryVM.AddEntry("image/png", "", png, "client");
                    SuppressClipboardCapture(); // C2
                    _clipboardBridge.SetPng(png);
                }
                catch { }
            });
        };
        _kvm.HostFileDropGetter = (out bool isCut) => _clipboardBridge.GetFileDropList(out isCut);
        _kvm.OnClipboardFilesFromClient = (files, isCut) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    SuppressClipboardCapture(); // C2
                    _clipboardBridge.SetFileDropList(files, isCut);
                    HistoryVM.AddEntry("application/x-file-list", string.Join("\n", files), null, "client");
                }
                catch { }
            });
        };

        InitializeComponent();

        BuildSettingsPages();

        // v1.9.1: transform ของผัง (pan/zoom) — attach หลัง InitializeComponent
        TopologyCanvas.RenderTransform = _mapTransform;
        // v1.9.1: pan/zoom ผูกที่ Grid — pan ได้ทุกพื้นที่ว่าง (Canvas เปล่า + Grid background)
        // และซูมได้แม้เมาส์อยู่เหนือการ์ด/ghost · การ์ด/ghost มี handler ตัวเอง (discriminate ด้วย OriginalSource)
        MapHostGrid.MouseLeftButtonDown += OnMapAreaMouseDown;
        MapHostGrid.MouseMove += OnMapAreaMouseMove;
        MapHostGrid.MouseLeftButtonUp += OnMapAreaMouseUp;
        MapHostGrid.MouseWheel += OnMapAreaWheel;
        MapHostGrid.SizeChanged += (_, _) =>
        {
            if (_mapFitPending) ApplyMapFit();
        };

        LoadAppIcon();
        HeaderAppIcon.Source = OmniIconBuilder.BuildAppLogoBadge();

        // C8: tray อาจสร้างล้มเหลว (shell มีปัญหา) — ต้องไม่ทำให้เปิดโปรแกรมไม่ได้
        OmniTrayService? tray = null;
        try
        {
            tray = new OmniTrayService(
                openDashboard: ShowAndActivateWindow,
                calibrateEn: () => _kvm.CalibrateAllLanguages(0),
                calibrateTh: () => _kvm.CalibrateAllLanguages(1),
                resetAudio: OnResetBothAudioTriggered,
                exitApp: () => ExitApplication());
        }
        catch (Exception ex)
        {
            OmniLog.Error("[tray] สร้าง System Tray icon ล้มเหลว — ปิดหน้าต่างจะเป็นการออกโปรแกรม", ex);
        }
        _tray = tray;

        // C6: Windows กำลัง logoff / shutdown — เก็บกวาดแบบเร็ว (quick: ข้ามการรอ remote stop)
        Application.Current.SessionEnding += (_, _) =>
        {
            try { OmniLog.Write("boot", "session ending → quick cleanup"); } catch { }
            try { ExitApplication(quick: true); } catch { }
        };

        LocalizationService.LanguageChanged += OnUiLanguageChanged;

        ApplyTheme();
        Loaded += OnWindowLoaded;
        SourceInitialized += OnWindowSourceInitialized;

        LoadSettingsIntoControls();
        RefreshMicDropdownInitial();
        RefreshUiState();

        _uiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(900),
        };
        _uiTimer.Tick += (_, _) => RefreshUiState();
        _uiTimer.Start();

        _idleTrimTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                // อ่านสถานะหน้าต่างแบบไม่บล็อก ThreadPool + trim แบบเบา (ไม่มี blocking GC ที่จะ suspend hook thread)
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsVisible || WindowState == WindowState.Minimized)
                    {
                        Task.Run(() => { try { NativeMethods.TrimWorkingSet(fullGc: false); } catch { } });
                    }
                });
            }
            catch { }
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

        // E1: debounce การเขียน config — ลาก slider/เปลี่ยน settings จะเขียนดิสก์เฉพาะเมื่อหยุดแล้ว 400ms
        _configSaveDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _configSaveDebounce.Tick += (_, _) =>
        {
            _configSaveDebounce.Stop();
            PersistConfig(); // S3: engine mode = เขียนไฟล์ · dashboard = push ทั้งฉบับให้ hostd apply+save
        };

        // C2: seed default rule Ctrl+Alt+V → history_picker ครั้งแรก (ผู้ใช้แก้/ลบผ่าน Settings ได้)
        // (วางหลัง debounce สร้าง — ScheduleConfigSave ต้องมี timer)
        if (!_config.Hotkeys.Any(r => r.Actions.Any(a => a.Type == HotkeyActionConfig.HistoryPicker)))
        {
            _config.Hotkeys.Add(new HotkeyRuleConfig
            {
                Key = "Ctrl+Alt+V",
                Actions = new List<HotkeyActionConfig> { new(HotkeyActionConfig.HistoryPicker) },
            });
            ScheduleConfigSave();
            OmniLog.Write("config", "C2: seed default hotkey Ctrl+Alt+V → history picker");
        }
    }

    // ================= BOOT / WINDOW LIFECYCLE =================

    private void LoadAppIcon()
    {
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/OmniLink;component/Assets/app.ico", UriKind.Absolute));
        }
        catch
        {
        }
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        NativeMethods.ApplyAppIcon(helper.Handle);
        ApplyWindowVisual(helper.Handle);

        // C2: จับ local clipboard change ด้วย OS listener (เบา — ไม่ใช่ polling)
        if (helper.Handle != 0)
        {
            NativeMethods.AddClipboardFormatListener(helper.Handle);
            if (PresentationSource.FromVisual(this) is HwndSource source)
            {
                source.AddHook(ClipboardWndProc);
            }
        }
    }

    /// <summary>C2: echo suppression — เรียกก่อน "เราตั้ง clipboard เอง" เพื่อไม่จับกลับ (flag+timestamp)</summary>
    /// <summary>C2: picker เป็น window ลูก — เรียกผ่าน method นี้ (suppress การจับกลับหลังตั้ง clipboard)</summary>
    public void SuppressClipboardCaptureForPicker() => SuppressClipboardCapture();

    private void SuppressClipboardCapture()
    {
        _clipSuppressUntilTick = Environment.TickCount64 + 600;
    }

    private nint ClipboardWndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_CLIPBOARDUPDATE) return nint.Zero;
        // echo: การตั้ง clipboard ของเราเอง (จาก node / history picker) — ข้าม
        if (Environment.TickCount64 < _clipSuppressUntilTick) return nint.Zero;

        try
        {
            // อ่านบน UI thread (STA — bridge มี retry กัน CLIPBRD_E_CANT_OPEN อยู่แล้ว)
            if (_clipboardBridge.TryGetPng(out var png) && png.Length > 0)
            {
                _clipboardHistory.AddImage(png, "local");
            }
            else
            {
                var text = _clipboardBridge.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    _clipboardHistory.AddTextEntry(text, "local");
                }
            }
        }
        catch { }
        return nint.Zero;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        NativeMethods.ApplyAppIcon(helper.Handle);
        ApplyWindowVisual(helper.Handle);

        // Start Win32 Low-Level Hooks & Host Language Watcher
        if (_config.AutoStartKvm)
        {
            _kvm.Start();
        }
        else
        {
            OmniLog.Write("boot", "auto_start_kvm=false → ไม่ติดตั้ง Low-Level Hooks (โหมด KVM ปิด)");
        }

        // Bring window to front
        NativeMethods.BringWindowToTop(helper.Handle);
        NativeMethods.SetForegroundWindow(helper.Handle);

        // v1.9 C4: ทิปครั้งแรกที่เปิด (ฉุกเฉิน/สลับจอ/จับคู่/ภาษา) — tick "ไม่ต้องแสดงอีก" เก็บ config
        // v2.1 onboarding: dialog ต้อนรับ — ปิกด "เพิ่มเครื่องตอนนี้เลย" = เปิด wizard ต่อทันที (ทางเข้าเดียว)
        if (!_config.FirstRunDone)
        {
            var firstrun = new FirstRunDialog { Owner = this };
            firstrun.ShowDialog();
            if (firstrun.DontShowAgain)
            {
                _config.FirstRunDone = true;
                ScheduleConfigSave();
            }
            if (firstrun.AddNowRequested)
            {
                // กัน reentrancy: หนี loaded-event ก่อนเปิด dialog ที่สอง (ShowDialog ซ้อนใน event เดียว = WPF กลืนหน้าต่าง)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    var wizard = new AddFirstNodeWizard(this) { Owner = this };
                    wizard.ShowDialog();
                    _mapSignature = ""; // บังคับ redraw หลัง wizard (อาจเพิ่ม node แรก)
                });
            }
        }

        // Background startup for Remote Agent deploy & GStreamer Audio Bridge
        Task.Run(() =>
        {
            if (_dashboard) return; // S3: deploy + auto-audio เป็นของฝั่ง engine (hostd boot sequence)
            SetBanner(Loc.T("banner.boot.connecting", "กำลังเชื่อมต่อเครื่องลูกข่ายและเริ่มระบบเสียง LAN..."));
            Thread.Sleep(500);

            foreach (var ncfg in _config.Nodes)
            {
                if (ncfg.Enabled)
                {
                    _kvm.SessionViews.TryGetValue(ncfg.NodeId, out var sess);
                    if (sess is null || !sess.Connected)
                    {
                        var (ok, msg) = _kvm.DeployRemoteAgent(ncfg);
                        OmniLog.Write("deploy", $"boot deploy ok={ok}: {msg}");
                        Thread.Sleep(600);
                    }
                }
            }

            if (_config.AutoStartStream || _config.AutoStartMic)
            {
                AudioHubService.KillOrphanedAudioFromPreviousRun(_config.GstExe); // D5: PID file + orphan-match (เฉพาะ gst ของเราที่ parent ตายแล้ว)
                // รอ node เชื่อมก่อนสั่ง audio — กันสั่งแล้วหลุดเพราะ TCP ยังไม่ติด (เห็นจริงใน log 01:41)
                for (var i = 0; i < 30; i++)
                {
                    if (_kvm.SessionViews.Values.Any(s => s.Connected))
                    {
                        break;
                    }
                    Thread.Sleep(500);
                }
                if (_config.AutoStartStream) AudioStartStream();
                if (_config.AutoStartMic) AudioStartMic();
            }

            Dispatcher.BeginInvoke(() =>
            {
                var currentHostLang = _kvm.GetHostLangGroup();
                _kvm.CalibrateAllLanguages(currentHostLang);
                SetBanner(Loc.T("banner.boot.ready", "พร้อมทำงาน — ลากเมาส์ข้ามจอหรือกด Win+Space / ~ เพื่อสลับภาษาได้ทันที"));
                RefreshUiState();
            });
        });
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // ซ่อนลง tray เฉพาะเมื่อมี tray icon — ไม่งั้นหน้าต่างจะหายไปโดยไม่มีทางเรียกกลับ
        if (WindowState == WindowState.Minimized && _tray is not null)
        {
            Hide();
            _uiTimer.Stop();
            Task.Delay(500).ContinueWith(_ =>
            {
                try { NativeMethods.TrimWorkingSet(); } catch { }
            }, TaskScheduler.Default);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowExit)
        {
            if (_tray is null)
            {
                // ไม่มี tray icon = ปิดหน้าต่างแล้วไม่มีทางกลับมา → ต้องออกโปรแกรมจริง
                e.Cancel = true;
                ExitApplication();
                return;
            }
            // v1.9: ผู้ใช้เลือกพฤติกรรมปุ่มปิดได้ (Settings > General) — เดิมบังคับซ่อนลง tray เสมอ
            if (string.Equals(_config.CloseButtonAction, "exit", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                ExitApplication();
                return;
            }
            e.Cancel = true;
            Hide();
            _kvm.IsWindowHiddenInTray = true;
            _uiTimer.Stop();
            // E3: TrimWorkingSet มี blocking GC — ห้ามทำบน UI thread ให้เลื่อนไปหลังพื้นหลัง
            Task.Delay(500).ContinueWith(_ =>
            {
                try { NativeMethods.TrimWorkingSet(); } catch { }
            }, TaskScheduler.Default);
            return;
        }
        base.OnClosing(e);
    }

    public void ShowAndActivateWindow()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _kvm.IsWindowHiddenInTray = false;
            if (!IsVisible)
            {
                Show();
            }
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != 0)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            RefreshUiState();
            if (!_uiTimer.IsEnabled)
            {
                _uiTimer.Start();
            }
            Activate();
            Focus();
        });
    }

    /// <summary>
    /// v2.0: หน้าต่างทึบตลอด — ถอนระบบ backdrop/วัสดุ (Mica/Acrylic/Aero) ออกทั้งหมดตามคำสั่งผู้ใช้
    /// เหลือเพียงโทนสีจากธีม (AppWindowBgBrush) + dark-mode attribute + มุมโค้งตามเดิม
    /// </summary>
    private void ApplyWindowVisual(nint hwnd)
    {
        if (hwnd == 0 || PresentationSource.FromVisual(this) is not HwndSource source)
        {
            return;
        }

        ApplyTheme();

        var bg = TryFindResource("AppWindowBgBrush") is System.Windows.Media.SolidColorBrush b
            ? b.Color
            : System.Windows.Media.Color.FromRgb(0x13, 0x17, 0x22);
        DwmInterop.SetImmersiveDarkMode(source.Handle, !OmniLink.Themes.ThemeService.IsLight(_config.ThemeMode));
        DwmInterop.SetRoundedCorners(source.Handle, true);
        source.CompositionTarget!.BackgroundColor = bg;
        Root.Background = new SolidColorBrush(bg);
    }

    private void ApplyTheme()
    {
        Background = Brushes.Transparent;
        Foreground = (Brush?)TryFindResource("TextPrimaryBrush") ?? new SolidColorBrush(Colors.White);

        if (TryFindResource("AeroGlassCardBrush") is Brush glassBg &&
            TryFindResource("AeroSpecularRimBrush") is Brush rimBorder)
        {
            if (TopBannerCard is not null)
            {
                TopBannerCard.Background = glassBg;
                TopBannerCard.BorderBrush = rimBorder;
            }
            if (ScreenMapCard is not null)
            {
                ScreenMapCard.Background = glassBg;
                ScreenMapCard.BorderBrush = rimBorder;
            }
            if (AudioHubCard is not null)
            {
                AudioHubCard.Background = glassBg;
                AudioHubCard.BorderBrush = rimBorder;
            }
        }
    }

    private void LoadSettingsIntoControls()
    {
        _initializingControls = true;

        // v1.9 C2: ตัวเลือกของ combo แม็ป modifier — index 0 = ไม่แม็ป, 1-6 = shift/ctrl/alt/super/altgr/none
        foreach (var combo in new[] { ModMapShift, ModMapCtrl, ModMapAlt, ModMapSuper, ModMapAltGr })
        {
            combo.Items.Clear();
            combo.Items.Add(Loc.T("modmap.default", "ไม่แม็ป (ปกติ)"));
            combo.Items.Add(Loc.T("modmap.to.shift", "→ เป็น Shift"));
            combo.Items.Add(Loc.T("modmap.to.ctrl", "→ เป็น Ctrl"));
            combo.Items.Add(Loc.T("modmap.to.alt", "→ เป็น Alt"));
            combo.Items.Add(Loc.T("modmap.to.super", "→ เป็น Super (Win)"));
            combo.Items.Add(Loc.T("modmap.to.altgr", "→ เป็น AltGr"));
            combo.Items.Add(Loc.T("modmap.to.none", "กลืนปุ่ม (ปิดการใช้งาน)"));
            combo.SelectedIndex = 0;
        }
        var activeNode = _config.Nodes.FirstOrDefault(n => n.Enabled) ?? _config.Nodes.FirstOrDefault();
        L440PositionCombo.SelectedIndex = (activeNode?.Position) switch
        {
            "left" => 1,
            "top" => 2,
            "bottom" => 3,
            _ => 0
        };
        _initializingControls = false;
    }

    private void RefreshMicDropdownInitial()
    {
        _initializingControls = true;
        MicDeviceCombo.Items.Clear();
        var current = new MicDeviceInfo(_config.MicDeviceId, _config.MicDeviceName);
        MicDeviceCombo.Items.Add(current);
        MicDeviceCombo.SelectedIndex = 0;
        _initializingControls = false;
    }

    // ════════ S3 (Engine/UI split): ตัวกลางเสียง + config สำหรับสองโหมด ════════

    private bool AudioStreamRunningNow => _kvm is IpcKvmClient c ? c.AudioStreamRunning : _audio?.IsStreamRunning == true;
    private bool AudioMicRunningNow => _kvm is IpcKvmClient c ? c.AudioMicRunning : _audio?.IsMicRunning == true;

    private void AudioStartStream() { if (_kvm is IpcKvmClient c) { c.AudioOp("audio.start_stream"); return; } try { _audio!.StartStream(); } catch { } }
    private void AudioStopStream() { if (_kvm is IpcKvmClient c) { c.AudioOp("audio.stop_stream"); return; } try { _audio!.StopStream(); } catch { } }
    private void AudioStartMic() { if (_kvm is IpcKvmClient c) { c.AudioOp("audio.start_mic"); return; } try { _audio!.StartMic(); } catch { } }
    private void AudioStopMic() { if (_kvm is IpcKvmClient c) { c.AudioOp("audio.stop_mic"); return; } try { _audio!.StopMic(); } catch { } }

    /// <summary>S3: จุดเขียน config เดียว — engine mode = เขียนไฟล์ · dashboard = push ทั้งฉบับให้ engine apply (S0 §4.3)</summary>
    private Task PersistConfig()
    {
        if (_kvm is IpcKvmClient c)
        {
            return Task.Run(() => { try { c.ReplaceConfig(_config); } catch { } });
        }
        return Task.Run(() => { try { _config.Save(); } catch { } }); // UI-H4: เขียนไฟล์นอก UI thread (AtomicFile รอ mutex ได้ถึง 5 วิ)
    }

    private void ScheduleUiRefresh()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RefreshUiState));
    }

    public void SetBanner(string text)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (StatusBannerText is not null)
            {
                StatusBannerText.Text = text;
            }
        });
    }

    public void RefreshUi() => ScheduleUiRefresh();

    /// <summary>เวอร์ชัน synchronous สำหรับเรียกจาก UI thread เท่านั้น (crash handler)</summary>
    internal void ShowBannerNow(string text)
    {
        if (StatusBannerText is not null)
        {
            StatusBannerText.Text = text;
        }
    }

    /// <summary>E1: เรียกเมื่อ config เปลี่ยนจาก UI — รวมการเขียนจริงไว้ท้ายคลื่น</summary>
    private void ScheduleConfigSave()
    {
        _configSaveDebounce.Stop();
        _configSaveDebounce.Start();
    }

    /// <summary>B5: จุดเขียน config เดียวของโปรแกรม — mutate บน UI thread แล้ว debounce-save</summary>
    private void UpdateConfigOnUi(Action<OmniConfig> mutate)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                mutate(_config);
                ScheduleConfigSave();
            }
            catch (Exception ex)
            {
                OmniLog.Error("[config] update failed", ex);
            }
        });
    }

    internal void RefreshUiState()
    {
        // E2: เมื่อซ่อนอยู่ใน tray ให้อัปเดตเฉพาะ tray icon (เบา) — ข้าม node list / screen map / status.json
        // เพื่อให้โปรแกรมแฝงกิน RAM/CPU ต่ำที่สุดตาม Trap #7
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            var hostActiveH = _kvm.ActiveNodeId is null;
            var langH = hostActiveH
                ? _kvm.MasterLangGroup
                : (_kvm.ActiveNodeId is not null && _kvm.SessionViews.TryGetValue(_kvm.ActiveNodeId, out var sH)
                    ? sH.LangGroup
                    : _kvm.MasterLangGroup);
            _tray?.UpdateState(
                remoteActive: !hostActiveH,
                langGroup: langH,
                activeNodeText: hostActiveH ? Loc.T("tray.host", "Host ({0})", _config.HostName) : Loc.T("tray.remote", "Remote ({0})", _kvm.ActiveNodeId ?? ""));
            return;
        }

        var isTh = _kvm.MasterLangGroup == 1;
        if (BtnLangSegmentEn is not null && BtnLangSegmentTh is not null)
        {
            var activeText = (Brush?)TryFindResource("TextPrimaryBrush") ?? Brushes.White;
            var mutedText = (Brush?)TryFindResource("TextMutedBrush") ?? new SolidColorBrush(Color.FromRgb(0x8B, 0x9B, 0xB4));
            var cyanGlow = (Brush?)TryFindResource("AeroCyanGlowBrush");
            var amberGlow = (Brush?)TryFindResource("JewelAmberGlowBrush");

            if (isTh)
            {
                // TH Active (Jewel Amber)
                BtnLangSegmentEn.Background = Brushes.Transparent;
                BtnLangSegmentEn.Foreground = mutedText;
                BtnLangSegmentTh.Background = amberGlow ?? new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));
                BtnLangSegmentTh.Foreground = activeText;
            }
            else
            {
                // EN Active (Aero Cyan)
                BtnLangSegmentEn.Background = cyanGlow ?? new SolidColorBrush(Color.FromRgb(0x00, 0x88, 0xFF));
                BtnLangSegmentEn.Foreground = activeText;
                BtnLangSegmentTh.Background = Brushes.Transparent;
                BtnLangSegmentTh.Foreground = mutedText;
            }
        }

        // Update Node List on the left (real hardware-verified sess.LangGroup)
        var selectedIdx = NodeListBox.SelectedIndex;
        var rows = new List<NodeListRow>();
        foreach (var ncfg in _config.Nodes)
        {
            _kvm.SessionViews.TryGetValue(ncfg.NodeId, out var sess);
            var online = sess is not null && sess.Connected;
            var focused = _kvm.ActiveNodeId == ncfg.NodeId;
            var nodeLang = sess?.LangGroup ?? 0;

            var icon = GetCachedNodeBadge(online, focused, nodeLang);

            var statusShort = focused ? Loc.T("node.focus", "FOCUS")
                : online ? Loc.T("node.online", "ONLINE")
                : (ncfg.Enabled ? Loc.T("node.offline", "OFFLINE") : Loc.T("node.disabled", "DISABLED"));
            var badgeBg = focused ? FocusedBadgeBg : (online ? OnlineBadgeBg : OfflineBadgeBg);
            var badgeFg = focused ? FocusedBadgeFg : (online ? OnlineBadgeFg : OfflineBadgeFg);

            var friendlyTitle = ncfg.DisplayName;
            var parenIdx = friendlyTitle.IndexOf('(');
            var osInfo = parenIdx > 0 ? friendlyTitle.Substring(parenIdx).Trim('(', ')') : (!string.IsNullOrWhiteSpace(ncfg.DeviceType) ? ncfg.DeviceType : "Linux");
            var shortTitle = parenIdx > 0 ? friendlyTitle.Substring(0, parenIdx).Trim() : friendlyTitle;

            var sub = $"{osInfo} • {ncfg.Ip} • {ncfg.Position.ToUpperInvariant()}";
            // v1.8 C-lean: ping แสดงเป็นจำนวนเต็ม — EMA ขยับ <1ms ไม่ควรทำให้ row เปลี่ยนทั้งแถว
            var tele = online && sess is not null
                ? $"Ping {sess.PingMs:0}ms • RAM {sess.RamPct:0}% • Lang: {(nodeLang == 1 ? "TH" : "EN")}"
                : Loc.T("node.awaiting", "Awaiting hardware handshake...");

            rows.Add(new NodeListRow(ncfg.NodeId, icon, shortTitle, sub, tele, statusShort, badgeBg, badgeFg));
        }

        // v1.8 C-lean: อย่ารื้อ ItemsSource ทุก 900ms เมื่อข้อมูลเท่าเดิม —
        // การสลับ ItemsSource = รื้อ containers + selection flicker + brush churn (handles โต)
        // NodeListRow เป็น record = เทียบค่าได้ตรงๆ
        var currentRows = NodeListBox.ItemsSource as System.Collections.Generic.IReadOnlyList<NodeListRow>;
        var unchanged = currentRows is not null &&
                        currentRows.Count == rows.Count &&
                        !rows.Where((r, i) => !r.Equals(currentRows[i])).Any();
        if (!unchanged)
        {
            NodeListBox.ItemsSource = rows;
        }
        if (selectedIdx >= 0 && selectedIdx < rows.Count)
        {
            if (NodeListBox.SelectedIndex != selectedIdx)
            {
                NodeListBox.SelectedIndex = selectedIdx;
            }
        }
        else if (rows.Count > 0)
        {
            if (NodeListBox.SelectedIndex != 0)
            {
                NodeListBox.SelectedIndex = 0;
            }
        }

        // v1.9.1: ผัง topology วาดโดย RenderTopologyMap (Canvas ไดนามิก)
        RenderTopologyMap();

        // Synchronize Active Topology ComboBox + target header + device type with selected device
        var selectedNodeId = (NodeListBox?.SelectedItem as NodeListRow)?.NodeId;
        var selectedNodeCfg = _config.Nodes.FirstOrDefault(n => n.NodeId == selectedNodeId);
        if (selectedNodeCfg != null)
        {
            _initializingControls = true;
            L440PositionCombo.SelectedIndex = (selectedNodeCfg.Position?.ToLowerInvariant()) switch
            {
                "left" => 1,
                "top" => 2,
                "bottom" => 3,
                _ => 0
            };
            if (DeviceTypeCombo is not null)
            {
                DeviceTypeCombo.SelectedIndex = (selectedNodeCfg.DeviceType?.ToLowerInvariant()) switch
                {
                    "desktop" => 1,
                    "server" => 2,
                    _ => 0
                };
            }
            _initializingControls = false;
            NodeTargetHeaderText.Text = selectedNodeCfg.DisplayName;
        }
        else
        {
            NodeTargetHeaderText.Text = Loc.T("devices.target.none", "(ยังไม่ได้เลือกเครื่อง)");
        }

        // Update Audio Toggle State & Buttons
        var pbOn = AudioStreamRunningNow;
        var micOn = AudioMicRunningNow;

        BtnToggleStream.Content = pbOn ? Loc.T("audio.active", "ACTIVE") : Loc.T("audio.standby", "STANDBY");
        BtnToggleStream.Background = pbOn
            ? (Brush?)TryFindResource("JewelEmeraldDeepBrush") ?? new SolidColorBrush(Color.FromArgb(0x25, 0x00, 0xF5, 0xA0))
            : new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
        BtnToggleStream.BorderBrush = pbOn
            ? (Brush?)TryFindResource("JewelEmeraldBrush") ?? new SolidColorBrush(Color.FromRgb(0x00, 0xF5, 0xA0))
            : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

        BtnToggleMic.Content = micOn ? Loc.T("audio.active", "ACTIVE") : Loc.T("audio.standby", "STANDBY");
        BtnToggleMic.Background = micOn
            ? (Brush?)TryFindResource("JewelEmeraldDeepBrush") ?? new SolidColorBrush(Color.FromArgb(0x25, 0x00, 0xF5, 0xA0))
            : new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
        BtnToggleMic.BorderBrush = micOn
            ? (Brush?)TryFindResource("JewelEmeraldBrush") ?? new SolidColorBrush(Color.FromRgb(0x00, 0xF5, 0xA0))
            : new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

        var hostActive = _kvm.ActiveNodeId is null; // (เดิมประกาศในบล็อก slot-card ที่ย้ายไป RenderTopologyMap)
        var activeLang = hostActive
            ? _kvm.MasterLangGroup
            : (_kvm.ActiveNodeId is not null && _kvm.SessionViews.TryGetValue(_kvm.ActiveNodeId, out var actSess)
                ? actSess.LangGroup
                : _kvm.MasterLangGroup);

        _tray?.UpdateState(
            remoteActive: !hostActive,
            langGroup: activeLang,
            activeNodeText: hostActive ? Loc.T("tray.host", "Host ({0})", _config.HostName) : Loc.T("tray.remote", "Remote ({0})", _kvm.ActiveNodeId ?? ""));

        // เฉพาะหน้า Security ที่ต้องการข้อมูลสด (fingerprint/policy) — รื้อเมื่อ Settings tab เปิดอยู่เท่านั้น
        if (MainTabs.SelectedIndex == 3)
        {
            _securityPage?.RefreshPairingUi();
            UpdateSettingsFooterButtons(); // dirty tracking ของปุ่มยกเลิกการแก้ไข
        }

        // E2 + godclass-M1: UI thread แค่ capture snapshot — serialize/dedupe/เขียนไฟล์เป็น background
        var hwnd = new WindowInteropHelper(this).Handle;
        _statusJson.Report(new
        {
            pid = Environment.ProcessId,
            app_version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?", // F2: ยืนยันความสดของ binary
            hwnd = (long)hwnd,
            is_visible = IsVisible,
            window_state = WindowState.ToString(),
            virtual_left = _kvm.VirtualLeft,
            virtual_top = _kvm.VirtualTop,
            virtual_right = _kvm.VirtualRight,
            virtual_bottom = _kvm.VirtualBottom,
            active_node = _kvm.ActiveNodeId,
            master_lang = _kvm.MasterLangGroup,
            stream_running = pbOn,
            mic_running = micOn,
            nodes = _kvm.SessionViews.Values.Select(s => new
            {
                id = s.Config.NodeId,
                ip = s.Config.Ip,
                connected = s.Connected,
                ping_ms = Math.Round(s.PingMs, 0), // Perf: ปัดก่อน dedupe — เดิม float ลอยทำ dedupe ไม่มีวันเข้าเงื่อนไข (เขียนไฟล์ ~1 ครั้ง/วิ)
                width = s.Width,
                height = s.Height,
                lang = s.LangGroup,
            }).ToArray(),
        });
    }

    private void OnQuickScanClick(object sender, RoutedEventArgs e) => NavigateToPairingAndScan();

    private void OnScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 2.5);
            e.Handled = true;
        }
    }

    private void OnExitAppClick(object sender, RoutedEventArgs e) => ExitApplication();

    /// <summary>
    /// D6+D1: ลำดับการปิดที่ถูกต้อง — หยุด remote ก่อน (ต้องใช้ TCP ที่ยังเปิด)
    /// → ปิด KVM sessions → ฆ่า local gst — ทั้งหมดบน background พร้อม deadline
    /// ผู้ใช้จะไม่เจอหน้าต่างค้างตอนออก และเครื่อง Linux จะได้ sink กลับคืน
    /// </summary>
    private void ExitApplication(bool quick = false)
    {
        if (_exitStarted) return;
        _exitStarted = true;

        try { _subServer?.Dispose(); } catch { } // v2.0: ปิดโหมด sub ก่อน (คืนสิทธิ์ UDP/TLS พอร์ต)
        _subServer = null;

        SetBanner(quick
            ? Loc.T("banner.exit.quick", "กำลังปิดโปรแกรมอย่างรวดเร็ว (Session Ending)...")
            : Loc.T("banner.exit.slow", "กำลังปิดระบบเสียงและคืนสถานะเครื่อง Linux..."));

        var shutdown = Task.Run(() =>
        {
            // E1: flush config ค้างใน debounce ก่อนปิด — เขียนบน background เสมอ (UI-H4)
            PersistConfig();
            try
            {
                if (!quick) _audio?.ShutdownFull(); // remote stop + local stop (ใช้เวลา ~1-4s)
            }
            catch { }
            try { _discovery.Dispose(); } catch { }
            try { _kvm.Dispose(); } catch { } // engine: unhook+ปิด sessions · dashboard: ปิดท่อ IPC
            if (!_dashboard) EngineSingleton.Release(); // godclass-G2
            try { _audio?.Dispose(); } catch { }
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != 0) NativeMethods.RemoveClipboardFormatListener(hwnd); // C2
            }
            catch { }
        });

        var timeout = Task.Delay(TimeSpan.FromSeconds(quick ? 2 : 8));
        Task.WhenAny(shutdown, timeout).ContinueWith(_ =>
            Dispatcher.BeginInvoke(() =>
            {
                _uiTimer.Stop();
                _idleTrimTimer.Dispose();
                _tray?.Dispose();
                _allowExit = true;
                Close();
                Application.Current.Shutdown();
            }));
    }

}