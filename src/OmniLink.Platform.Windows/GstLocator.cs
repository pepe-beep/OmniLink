using System.IO;
using OmniLink.Core.Strategies;
using OmniLink.Interop;

namespace OmniLink.Platform.Windows;

/// <summary>
/// v2.0 (ย้ายจาก OmniPaths): ค้นหา gst-launch-1.0.exe บน Windows จาก Registry, PATH,
/// Environment Variables และโฟลเดอร์มาตรฐาน — Linux ใช้ gst จาก PATH ตรงๆ ไม่ผ่านตัวค้นหานี้
/// </summary>
public static class GstLocator
{
    public static string DiscoverGstreamerExe()
    {
        // 1. ตรวจจาก Environment Variable มาตรฐานของ GStreamer SDK
        var gstreamerRoot = Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_MSVC_X86_64")
            ?? Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_MINGW_W64")
            ?? Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_X86_64");

        if (!string.IsNullOrWhiteSpace(gstreamerRoot))
        {
            var p = Path.Combine(gstreamerRoot, "bin", "gst-launch-1.0.exe");
            if (File.Exists(p)) return p;
        }

        // 2. ตรวจจาก Registry HKCU / HKLM
        string[] regKeys =
        {
            @"Software\GStreamer1.0\x86_64",
            @"Software\GStreamer\x86_64",
            @"Software\WOW6432Node\GStreamer1.0\x86_64",
            @"Software\WOW6432Node\GStreamer\x86_64"
        };

        foreach (var subKey in regKeys)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey)
                    ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
                if (key?.GetValue("InstallDir") is string installDir && !string.IsNullOrWhiteSpace(installDir))
                {
                    var p = Path.Combine(installDir, "bin", "gst-launch-1.0.exe");
                    if (File.Exists(p)) return p;
                }
            }
            catch
            {
            }
        }

        // 3. ตรวจจาก PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var p = Path.Combine(dir, "gst-launch-1.0.exe");
                if (File.Exists(p)) return p;
            }
            catch
            {
            }
        }

        // 4. ตรวจจาก Local AppData ของ User ปัจจุบัน
        try
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var localGst = Path.Combine(localApp, "Programs", "gstreamer", "1.0", "msvc_x86_64", "bin", "gst-launch-1.0.exe");
            if (File.Exists(localGst)) return localGst;
        }
        catch
        {
        }

        // 5. ตรวจจาก Program Files มาตรฐาน
        string[] standardDirs =
        {
            @"C:\gstreamer\1.0\msvc_x86_64\bin\gst-launch-1.0.exe",
            @"C:\gstreamer\1.0\mingw_x86_64\bin\gst-launch-1.0.exe",
            @"C:\Program Files\gstreamer\1.0\msvc_x86_64\bin\gst-launch-1.0.exe",
            @"C:\Program Files (x86)\gstreamer\1.0\msvc_x86_64\bin\gst-launch-1.0.exe"
        };
        foreach (var p in standardDirs)
        {
            if (File.Exists(p)) return p;
        }

        return "gst-launch-1.0.exe";
    }
}

