using System.Windows;
using System.Windows.Input;
using OmniLink.Core;
using OmniLink.Core.Abstractions;
using OmniLink.Core.Clipboard;

namespace OmniLink.Views;

/// <summary>
/// C2: clipboard history picker (Win+V style) — เปิดด้วย hotkey history_picker (default Ctrl+Alt+V)
/// เลือก (double-click / Enter) = ตั้ง clipboard ปัจจุบัน + ถ้ากำลังควบคุม node อยู่ push ทันที
/// </summary>
public partial class ClipboardPickerWindow : Window
{
    private readonly MainWindow _owner;
    private readonly IKvmHost _kvm;
    private readonly IClipboardBridge _bridge;
    private readonly ClipboardHistoryStore _store;

    public ClipboardPickerWindow(MainWindow owner, IKvmHost kvm, IClipboardBridge bridge, ClipboardHistoryStore store)
    {
        InitializeComponent();
        _owner = owner;
        _kvm = kvm;
        _bridge = bridge;
        _store = store;
        PickerList.ItemsSource = _store.GetEntries();
        if (PickerList.Items.Count > 0)
        {
            PickerList.SelectedIndex = 0;
            PickerList.Focus();
        }
    }

    private void OnItemActivate(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return) ActivateSelected();
    }

    private void OnItemActivate(object sender, MouseButtonEventArgs e) => ActivateSelected();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void ActivateSelected()
    {
        if (PickerList.SelectedItem is not ClipboardHistoryEntry entry)
        {
            Close();
            return;
        }
        try
        {
            if (entry.MimeType == "image/png")
            {
                var png = _store.GetPng(entry);
                if (png is { Length: > 0 })
                {
                    _owner.SuppressClipboardCaptureForPicker();
                    _bridge.SetPng(png);
                }
            }
            else
            {
                var text = _store.GetText(entry);
                if (!string.IsNullOrEmpty(text))
                {
                    _owner.SuppressClipboardCaptureForPicker();
                    _bridge.SetText(text);
                }
            }
            // spec C2: ถ้ากำลังควบคุมลูกอยู่ push ไปลูกทันที
            _kvm.PushClipboardToActiveNode();
        }
        catch (Exception ex)
        {
            OmniLog.Warn("[picker] เลือก clipboard ล้มเหลว: " + ex.Message);
        }
        Close();
    }
}
