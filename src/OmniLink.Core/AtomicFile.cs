using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OmniLink.Core;

/// <summary>
/// การเขียนทุกไฟล์ของผู้ใช้ต้อง atomic (temp + File.Replace) + named mutex ต่อไฟล์
/// และอ่านแบบ FileShare.ReadWrite | FileShare.Delete (อ้างอิงกฎเหล็กข้อ 19 จาก AppGroupLauncher)
/// </summary>
public static class AtomicFile
{
    private static readonly Dictionary<string, Mutex> Mutexes = new();
    private static readonly object MutexesGate = new();

    public static string? TryReadAllText(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                Thread.Sleep(10 * (attempt + 1));
            }
        }
        return null;
    }

    public static void WriteAllText(string path, string contents, bool writeUtf8Bom = false)
    {
        // SV-M10 fix: เดิมไม่สนผล WaitOne — timeout แล้วยังเขียนต่อโดยไม่ได้ครอง mutex
        // (writer สองตัวแข่งกลาง File.Replace ได้) → ต้อง fail อย่างชัดเจน
        var mutex = AcquireMutex(path);
        if (mutex is null)
        {
            throw new TimeoutException(
                $"AtomicFile: รอ mutex ของ '{Path.GetFileName(path)}' ครบ 5 วิโดยไม่ได้กรรมสิทธิ์ — ปฏิเสธการเขียน (มี writer อื่นค้าง)");
        }
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            for (var attempt = 0; ; attempt++)
            {
                var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(tmp, contents, new UTF8Encoding(writeUtf8Bom));
                    if (File.Exists(path))
                    {
                        File.Replace(tmp, path, null);
                    }
                    else
                    {
                        File.Move(tmp, path);
                    }
                    return;
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(15 * (attempt + 1));
                }
                finally
                {
                    TryDelete(tmp);
                }
            }
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    /// <summary>คืน mutex ที่ "ได้กรรมสิทธิ์แล้ว" หรือ null ถ้าหมดเวลา (AbandonedMutex = ได้กรรมสิทธิ์ต่อจากเจ้าของที่ตาย)</summary>
    private static Mutex? AcquireMutex(string path)
    {
        Mutex mutex;
        lock (MutexesGate)
        {
            var key = Normalize(path);
            if (!Mutexes.TryGetValue(key, out mutex!))
            {
                mutex = new Mutex(initiallyOwned: false, NameOf(path));
                Mutexes[key] = mutex;
            }
        }

        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(5)) ? mutex : null;
        }
        catch (AbandonedMutexException)
        {
            return mutex;
        }
    }

    private static string NameOf(string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(path))))[..32];
        return $@"Local\OMNILINK-{hash}";
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
