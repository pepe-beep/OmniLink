using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OmniLink.Core;
using OmniLink.Core.Events;
using OmniLink.Core.FileExchange;
using OmniLink.Interop;
using OmniLink.ViewModels;
using OmniLink.Views.Settings;

namespace OmniLink.Views;

// godclass-M4: ย้ายมาจาก MainWindow.xaml.cs แบบ move-verbatim (partial class)
// กลุ่ม AUDIO + FILES + HIST: audio tab handlers, transfer folder, clipboard history
public partial class MainWindow
{
    private void OnToggleStreamClick(object sender, RoutedEventArgs e)
    {
        Task.Run(() =>
        {
            if (_audio.IsStreamRunning)
            {
                SetBanner(Loc.T("banner.stream.stopping", "กำลังหยุดขารับเสียง Stream..."));
                _audio.StopStream();
                SetBanner(Loc.T("banner.stream.stopped", "หยุดขารับเสียง Stream แล้ว"));
            }
            else
            {
                SetBanner(Loc.T("banner.stream.starting", "กำลังเริ่มขารับเสียง Stream (เครื่องลูก → Windows)..."));
                _audio.StartStream();
                SetBanner(Loc.T("banner.stream.running", "ขารับเสียง Stream กำลังทำงาน"));
            }
            ScheduleUiRefresh();
        });
    }

    private void OnToggleMicClick(object sender, RoutedEventArgs e)
    {
        Task.Run(() =>
        {
            if (_audio.IsMicRunning)
            {
                SetBanner(Loc.T("banner.mic.stopping", "กำลังหยุดขาส่งไมโครโฟน..."));
                _audio.StopMic();
                SetBanner(Loc.T("banner.mic.stopped", "หยุดขาส่งไมโครโฟนแล้ว"));
            }
            else
            {
                SetBanner(Loc.T("banner.mic.starting", "กำลังเริ่มขาส่งไมโครโฟน (Windows → เครื่องลูก)..."));
                _audio.StartMic();
                SetBanner(Loc.T("banner.mic.running", "ขาส่งไมโครโฟนกำลังทำงาน"));
            }
            ScheduleUiRefresh();
        });
    }

    private void OnResetBothAudioClick(object sender, RoutedEventArgs e) => OnResetBothAudioTriggered();

    private void OnResetBothAudioTriggered()
    {
        Task.Run(() =>
        {
            SetBanner(Loc.T("banner.audio.resetting", "กำลังรีเซ็ตระบบเสียงทั้งขา Stream และ Mic..."));
            _audio.ResetBoth();
            SetBanner(Loc.T("banner.audio.resetdone", "รีเซ็ตระบบเสียงทั้งสองทิศทางเรียบร้อยแล้ว"));
            ScheduleUiRefresh();
        });
    }

    private void OnScanMicsClick(object sender, RoutedEventArgs e)
    {
        Task.Run(() =>
        {
            SetBanner(Loc.T("banner.mic.scanning", "กำลังสแกนอุปกรณ์ไมโครโฟน WASAPI2..."));
            var wasRunning = _audio.IsMicRunning;
            if (wasRunning) _audio.StopMic();

            var devs = _audio.ScanMicrophones();
            Dispatcher.BeginInvoke(() =>
            {
                _initializingControls = true;
                MicDeviceCombo.Items.Clear();
                var selectIdx = 0;
                for (var i = 0; i < devs.Count; i++)
                {
                    MicDeviceCombo.Items.Add(devs[i]);
                    if (devs[i].Id == _config.MicDeviceId) selectIdx = i;
                }
                MicDeviceCombo.SelectedIndex = selectIdx;
                _initializingControls = false;
            });

            if (wasRunning) _audio.StartMic();
            SetBanner(Loc.T("banner.mic.found", "พบไมโครโฟนทั้งหมด {0} รายการ", devs.Count));
            ScheduleUiRefresh();
        });
    }

    private void OnMicDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingControls) return;
        if (MicDeviceCombo.SelectedItem is MicDeviceInfo info)
        {
            _config.MicDeviceId = info.Id;
            _config.MicDeviceName = info.Name;
            ScheduleConfigSave();
            if (_audio.IsMicRunning)
            {
                Task.Run(() =>
                {
                    _audio.StartMic();
                    ScheduleUiRefresh();
                });
            }
        }
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    private void OnOpenTransferFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var target = _lastTransferFolder;
            if (string.IsNullOrEmpty(target) || !System.IO.Directory.Exists(target))
            {
                target = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
            if (System.IO.Directory.Exists(target))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = target,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            OmniLog.Warn($"[transfer] cannot open folder: {ex.Message}");
        }
    }

    private void OnClearClipboardHistoryClick(object sender, RoutedEventArgs e)
    {
        HistoryVM.Clear();
    }

    private void OnRefreshClipboardHistoryClick(object sender, RoutedEventArgs e)
    {
        HistoryVM.Refresh();
    }

    private void OnCopyHistoryItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OmniLink.Core.Clipboard.ClipboardHistoryEntry entry })
        {
            if (entry.MimeType == "image/png" && entry.BinaryData != null)
            {
                _clipboardBridge.SetPng(entry.BinaryData);
            }
            else if (!string.IsNullOrEmpty(entry.PreviewText))
            {
                _clipboardBridge.SetText(entry.PreviewText);
            }
        }
    }
}
