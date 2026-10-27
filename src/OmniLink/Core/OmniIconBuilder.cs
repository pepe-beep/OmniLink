using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OmniLink.Interop;
using Color = System.Drawing.Color;
using Pen = System.Drawing.Pen;
using LinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;

namespace OmniLink.Core;

/// <summary>
/// Fluent 2 Vector-Style Icon Provider:
/// Provides crisp, minimalist geometric iconography and badges for
/// nodes, display arrangements, and system tray following Windows 11 Fluent guidelines.
/// </summary>
public static class OmniIconBuilder
{
    private static readonly Dictionary<string, ImageSource> BadgeCache = new();
    private static readonly Dictionary<string, Icon> TrayCache = new();
    private static readonly object CacheLock = new();
    private static ImageSource? _appIconSource;

    public static ImageSource GetAppIconImageSource()
    {
        if (_appIconSource is not null) return _appIconSource;

        try
        {
            var uri = new Uri("pack://application:,,,/OmniLink;component/Assets/app.ico", UriKind.Absolute);
            var frame = BitmapFrame.Create(uri);
            frame.Freeze();
            _appIconSource = frame;
            return frame;
        }
        catch
        {
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                using var sysIcon = Icon.ExtractAssociatedIcon(exe);
                if (sysIcon is not null)
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    _appIconSource = src;
                    return src;
                }
            }
        }
        catch
        {
        }

        return GetNodeBadgeImage(online: true, focused: true, langGroup: 0, isHost: true);
    }

    public static ImageSource BuildAppLogoBadge() => GetAppIconImageSource();

    public static ImageSource BuildNodeBadge(bool isHost, bool online, bool focused, int langGroup)
        => GetNodeBadgeImage(online, focused, langGroup, isHost);

    /// <summary>
    /// Renders a clean 64x64 node badge adhering to Windows 11 Fluent specifications:
    /// - Calm neutral slate container (#222A36)
    /// - Subtle 1px border (#2E3B4E or Fluent Blue #0078D4 when focused)
    /// - Clean geometric monitor glyph
    /// - Simple, solid status dot (Green for Online, Grey for Offline, Blue for Focused)
    /// - Minimalist language tag (EN/TH)
    /// </summary>
    public static ImageSource GetNodeBadgeImage(bool online, bool focused, int langGroup, bool isHost = false)
    {
        var key = $"{isHost}:{online}:{focused}:{langGroup}";
        lock (CacheLock)
        {
            if (BadgeCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            const int size = 64;
            using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // 1. Base Plate: Clean Dark Slate Rounded Rectangle
                var plateRect = new RectangleF(3, 3, 58, 58);
                using var platePath = RoundedPath(plateRect, 10);

                Color plateBg = focused
                    ? Color.FromArgb(255, 24, 38, 56)      // Deep Slate Blue focus
                    : (online ? Color.FromArgb(255, 28, 33, 43) : Color.FromArgb(255, 20, 23, 30));

                Color plateBorder = focused
                    ? Color.FromArgb(255, 0, 120, 212)     // Fluent Accent Blue #0078D4
                    : Color.FromArgb(255, 52, 64, 84);

                using (var bgBrush = new SolidBrush(plateBg))
                {
                    g.FillPath(bgBrush, platePath);
                }

                using (var borderPen = new Pen(plateBorder, focused ? 1.8f : 1.0f))
                {
                    g.DrawPath(borderPen, platePath);
                }

                // 2. Hardware Silhouette: Sleek Minimalist Display
                var monRect = new RectangleF(14, 14, 36, 23);
                using var monPath = RoundedPath(monRect, 3);

                Color monFill = Color.FromArgb(255, 14, 17, 24);
                using (var monBrush = new SolidBrush(monFill))
                {
                    g.FillPath(monBrush, monPath);
                }
                using (var monBorder = new Pen(Color.FromArgb(255, 68, 84, 108), 1.0f))
                {
                    g.DrawPath(monBorder, monPath);
                }

                // Stand
                using (var standPen = new Pen(Color.FromArgb(255, 100, 116, 139), 1.6f))
                {
                    g.DrawLine(standPen, 32, 37, 32, 43);
                    g.DrawLine(standPen, 24, 43, 40, 43);
                }

                // 3. Status Dot (Solid, Crisp, Non-neon)
                Color dotColor = focused
                    ? Color.FromArgb(255, 76, 194, 255)   // Fluent Light Blue
                    : (online ? Color.FromArgb(255, 39, 174, 96) : Color.FromArgb(255, 120, 130, 145));

                using (var dotBrush = new SolidBrush(dotColor))
                {
                    g.FillEllipse(dotBrush, 17f, 17f, 5f, 5f);
                }

                // 4. Subtle Language Badge (Bottom Right)
                var isTh = langGroup == 1;
                var langRect = new RectangleF(34, 40, 24, 16);
                using var langPath = RoundedPath(langRect, 4);

                Color langBg = isTh ? Color.FromArgb(255, 217, 119, 6) : Color.FromArgb(255, 0, 103, 184);
                using (var langBrush = new SolidBrush(langBg))
                {
                    g.FillPath(langBrush, langPath);
                }

                using var font = new Font("Segoe UI", 8.5f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                using var whiteBrush = new SolidBrush(Color.White);
                g.DrawString(isTh ? "TH" : "EN", font, whiteBrush, new RectangleF(34, 40.5f, 24, 15), sf);
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = ms;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            BadgeCache[key] = bitmapImage;
            return bitmapImage;
        }
    }

    /// <summary>
    /// Renders a clean 32x32 System Tray Icon:
    /// Full-bleed Fluent rounded container with crisp "OL" logo and status dot,
    /// sized to match standard Windows notification area icon weights.
    /// </summary>
    public static Icon GetTrayIcon(bool remoteActive, int langGroup)
    {
        var key = $"{remoteActive}:{langGroup}";
        lock (CacheLock)
        {
            if (TrayCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // Full-bleed squircle (margin 0.5px) to match standard Windows tray icon dimensions
                var rect = new RectangleF(0.5f, 0.5f, 31f, 31f);
                using var path = RoundedPath(rect, 7f);

                // Fluent Blue base
                Color bg = remoteActive
                    ? Color.FromArgb(255, 0, 120, 212)   // Windows Accent Blue
                    : Color.FromArgb(255, 26, 34, 48);    // Calm Dark Slate

                using (var bgBrush = new SolidBrush(bg))
                {
                    g.FillPath(bgBrush, path);
                }

                using (var borderPen = new Pen(Color.FromArgb(90, 255, 255, 255), 1.2f))
                {
                    g.DrawPath(borderPen, path);
                }

                // Crisp Status Dot at Top Right (bold & clear with dark ring separation)
                Color dotColor = remoteActive ? Color.FromArgb(255, 39, 174, 96) : Color.FromArgb(255, 148, 163, 184);
                using (var dotBrush = new SolidBrush(dotColor))
                {
                    g.FillEllipse(dotBrush, 22.0f, 3.5f, 6.0f, 6.0f);
                }
                using (var dotRing = new Pen(Color.FromArgb(200, 20, 26, 38), 1.0f))
                {
                    g.DrawEllipse(dotRing, 22.0f, 3.5f, 6.0f, 6.0f);
                }

                // Typography: "OL" (OmniLink) - bold, clear, optical centered
                using var font = new Font("Segoe UI", 13.5f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                };
                using var whiteBrush = new SolidBrush(Color.White);
                g.DrawString("OL", font, whiteBrush, new RectangleF(0, 3.5f, 32, 27), sf);
            }

            var hicon = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(hicon).Clone();
            NativeMethods.DestroyIcon(hicon);

            TrayCache[key] = icon;
            return icon;
        }
    }

    private static GraphicsPath RoundedPath(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
