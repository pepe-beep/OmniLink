using System.IO;
using System.Text.Json;
using OmniLink.Core;

namespace OmniLink.Views;

/// <summary>
/// ย้ายออกจาก MainWindow (godclass-M1): ผู้เขียน status.json สำหรับเครื่องมือภายนอก
/// ปรับพฤติกรรมเดียวของแผน: เดิม serialize บน UI thread ทุก refresh (900ms) — ตอนนี้
/// UI thread แค่ capture snapshot, serialize + dedupe + เขียนไฟล์ทำบน background
/// </summary>
internal sealed class StatusJsonReporter
{
    private string? _last; // E2: กันเขียนซ้ำเมื่อเนื้อหาไม่เปลี่ยน (กัน disk write ทุก 900ms)

    /// <summary>UI thread เรียกพร้อม snapshot ที่ capture แล้ว — serialize/เขียนไฟล์เป็น background</summary>
    public void Report(object snapshot)
    {
        try
        {
            var statusJson = JsonSerializer.Serialize(snapshot);
            if (statusJson == _last) return;
            _last = statusJson;
            Task.Run(() =>
            {
                try
                {
                    AtomicFile.WriteAllText(OmniPaths.StatusJsonPath, statusJson);
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
    }
}
