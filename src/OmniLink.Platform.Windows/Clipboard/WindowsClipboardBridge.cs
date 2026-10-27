using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using OmniLink.Core.Abstractions;

namespace OmniLink.Core.Clipboard;

/// <summary>
/// Windows-specific Clipboard Bridge (Information Expert / Adapter)
/// จัดการ STA Thread Isolation, COMException Retry (CLIPBRD_E_CANT_OPEN: 0x800401D0),
/// และ PNG-First Image Streaming ตามมาตรฐาน ROADMAP-CLIPBOARD-SUITE-NATIVE-UI.md
/// </summary>
public sealed class WindowsClipboardBridge : IClipboardBridge
{
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 50;

    public string? GetText()
    {
        return RunOnSta(() =>
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                return System.Windows.Clipboard.GetText();
            }
            return null;
        });
    }

    public bool SetText(string text)
    {
        if (text is null) return false;

        return RunOnSta(() =>
        {
            System.Windows.Clipboard.SetText(text);
            return true;
        });
    }

    public bool TryGetPng(out byte[] pngBytes)
    {
        var result = RunOnSta<byte[]?>(() =>
        {
            // 1. ลองอ่าน native "PNG" data format เป็นอันดับแรก (ป้องกัน WPF DIB positive-height bug)
            if (System.Windows.Clipboard.ContainsData("PNG"))
            {
                var data = System.Windows.Clipboard.GetData("PNG");
                if (data is MemoryStream ms)
                {
                    return ms.ToArray();
                }
                if (data is Stream s)
                {
                    using var copyMs = new MemoryStream();
                    s.CopyTo(copyMs);
                    return copyMs.ToArray();
                }
                if (data is byte[] b)
                {
                    return b;
                }
            }

            // 2. Fallback: ถ้าไม่มี "PNG" format ลองแปลง BitmapSource ผ่าน PngBitmapEncoder
            if (System.Windows.Clipboard.ContainsImage())
            {
                try
                {
                    var img = System.Windows.Clipboard.GetImage();
                    if (img != null)
                    {
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(img));
                        using var outMs = new MemoryStream();
                        encoder.Save(outMs);
                        return outMs.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    OmniLog.Warn($"[clipboard] Fallback GetImage encoder failed: {ex.Message}");
                }
            }

            return null;
        });

        if (result != null && result.Length > 0)
        {
            pngBytes = result;
            return true;
        }

        pngBytes = Array.Empty<byte>();
        return false;
    }

    public bool SetPng(byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0) return false;

        return RunOnSta(() =>
        {
            var dataObject = new DataObject();
            dataObject.SetData("PNG", new MemoryStream(pngBytes));

            try
            {
                using var ms = new MemoryStream(pngBytes);
                var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count > 0)
                {
                    dataObject.SetImage(decoder.Frames[0]);
                }
            }
            catch (Exception ex)
            {
                OmniLog.Warn($"[clipboard] SetPng decoder fallback failed: {ex.Message}");
            }

            System.Windows.Clipboard.SetDataObject(dataObject, true);
            return true;
        });
    }

    public bool ContainsFileDropList()
    {
        return RunOnSta(() => System.Windows.Clipboard.ContainsFileDropList());
    }

    public System.Collections.Generic.IReadOnlyList<string>? GetFileDropList(out bool isCut)
    {
        bool cutFlag = false;
        var result = RunOnSta<System.Collections.Generic.List<string>?>(() =>
        {
            if (!System.Windows.Clipboard.ContainsFileDropList())
            {
                return null;
            }

            var dropList = System.Windows.Clipboard.GetFileDropList();
            if (dropList == null || dropList.Count == 0)
            {
                return null;
            }

            if (System.Windows.Clipboard.ContainsData("Preferred DropEffect"))
            {
                var effectData = System.Windows.Clipboard.GetData("Preferred DropEffect");
                if (effectData is MemoryStream ms)
                {
                    var buf = ms.ToArray();
                    if (buf.Length >= 4 && (buf[0] & 2) == 2)
                    {
                        cutFlag = true;
                    }
                }
                else if (effectData is byte[] b && b.Length >= 4 && (b[0] & 2) == 2)
                {
                    cutFlag = true;
                }
            }

            var list = new System.Collections.Generic.List<string>(dropList.Count);
            foreach (var item in dropList)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    list.Add(item);
                }
            }
            return list;
        });

        isCut = cutFlag;
        return result;
    }

    public bool SetFileDropList(System.Collections.Generic.IReadOnlyList<string> paths, bool isCut)
    {
        if (paths == null || paths.Count == 0) return false;

        return RunOnSta(() =>
        {
            var dataObject = new DataObject();
            var stringCollection = new System.Collections.Specialized.StringCollection();
            foreach (var p in paths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    stringCollection.Add(p);
                }
            }

            if (stringCollection.Count == 0) return false;

            dataObject.SetFileDropList(stringCollection);

            // Preferred DropEffect: 1 = DROPEFFECT_COPY, 2 = DROPEFFECT_MOVE
            var effect = new byte[] { (byte)(isCut ? 2 : 1), 0, 0, 0 };
            dataObject.SetData("Preferred DropEffect", new MemoryStream(effect));

            System.Windows.Clipboard.SetDataObject(dataObject, true);
            return true;
        });
    }

    /// <summary>
    /// บังคับให้ Action ทำงานบน STA Thread เสมอ พร้อม Retry เมื่อเกิด Clipboard Lock
    /// </summary>
    private static T RunOnSta<T>(Func<T> func)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return ExecuteWithRetry(func);
        }

        T result = default!;
        Exception? caught = null;

        var staThread = new Thread(() =>
        {
            try
            {
                result = ExecuteWithRetry(func);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.IsBackground = true;
        staThread.Start();
        staThread.Join(3000);

        if (caught != null)
        {
            OmniLog.Warn($"[clipboard] STA execution exception: {caught.Message}");
        }

        return result;
    }

    private static T ExecuteWithRetry<T>(Func<T> func)
    {
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                return func();
            }
            catch (COMException ex) when (unchecked((uint)ex.ErrorCode) == 0x800401D0 || ex.ErrorCode == -2147221008)
            {
                // CLIPBRD_E_CANT_OPEN: แอปรอค้างหรือแอปอื่นกำลังเปิด clipboard
                if (attempt == MaxRetries)
                {
                    OmniLog.Warn($"[clipboard] COMException 0x800401D0 after {MaxRetries} retries: {ex.Message}");
                    return default!;
                }
                Thread.Sleep(RetryDelayMs);
            }
            catch (ExternalException ex)
            {
                if (attempt == MaxRetries)
                {
                    OmniLog.Warn($"[clipboard] ExternalException after {MaxRetries} retries: {ex.Message}");
                    return default!;
                }
                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception ex)
            {
                OmniLog.Warn($"[clipboard] Unexpected error during clipboard access: {ex.Message}");
                return default!;
            }
        }

        return default!;
    }
}
