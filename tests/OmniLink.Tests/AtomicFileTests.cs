using System.IO;
using OmniLink.Core;
using Xunit;

namespace OmniLink.Tests;

/// <summary>
/// INV-9: การเขียนไฟล์ต้อง atomic — เนื้อหาในไฟล์สุดท้ายต้องเป็น "หนึ่งข้อความครบถ้วน"
/// ไม่ใช่เศษปนกันจากหลายเธรด แม้มี concurrent writer + reader พร้อมกัน
/// </summary>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "omnilink-atomic-test-" + Guid.NewGuid().ToString("N"));
    private volatile bool _readerShouldStop;

    public AtomicFileTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void WriteThenRead_ShouldRoundTrip()
    {
        var path = Path.Combine(_dir, "a.json");
        AtomicFile.WriteAllText(path, "hello สวัสดี", writeUtf8Bom: false);
        Assert.Equal("hello สวัสดี", AtomicFile.TryReadAllText(path));
    }

    [Fact]
    public void TryReadAllText_MissingFileReturnsNull()
    {
        Assert.Null(AtomicFile.TryReadAllText(Path.Combine(_dir, "missing.txt")));
    }

    [Fact]
    public void ConcurrentWriters_NeverProduceInterleavedContent()
    {
        var path = Path.Combine(_dir, "concurrent.json");
        const int writers = 20;
        const int writesPerWriter = 10;

        var allMessages = new HashSet<string>();
        using (var gate = new ManualResetEventSlim(false))
        {
            var threads = new List<Thread>();
            for (var w = 0; w < writers; w++)
            {
                var writerId = w;
                var t = new Thread(() =>
                {
                    gate.Wait();
                    for (var i = 0; i < writesPerWriter; i++)
                    {
                        var msg = new string((char)('A' + writerId), 500);
                        AtomicFile.WriteAllText(path, msg);
                    }
                })
                { IsBackground = true };
                threads.Add(t);
                t.Start();
            }
            gate.Set();
            foreach (var t in threads) t.Join();
        }

        for (var w = 0; w < writers; w++)
        {
            allMessages.Add(new string((char)('A' + w), 500));
        }

        var final = AtomicFile.TryReadAllText(path);
        Assert.NotNull(final);
        Assert.Contains(final!, allMessages); // ต้องเป็นข้อความใดข้อความหนึ่ง "ครบ 500 ตัว" ไม่ใช่เศษปนกัน
    }

    [Fact]
    public void ConcurrentReaderDuringWrites_SeesOnlyCompleteContent()
    {
        var path = Path.Combine(_dir, "reader.json");
        _readerShouldStop = false;
        AtomicFile.WriteAllText(path, "seed");

        var readerExceptions = new List<string>();
        var reader = new Thread(() =>
        {
            while (!_readerShouldStop)
            {
                var content = AtomicFile.TryReadAllText(path);
                if (content is not null && content != "seed" && content.Length != 500)
                {
                    lock (readerExceptions) readerExceptions.Add($"partial read: len={content.Length}");
                }
            }
        })
        { IsBackground = true };
        reader.Start();

        var writer = new Thread(() =>
        {
            for (var i = 0; i < 60; i++)
            {
                AtomicFile.WriteAllText(path, new string('X', 500));
            }
        })
        { IsBackground = true };
        writer.Start();
        writer.Join();
        _readerShouldStop = true;
        reader.Join();

        Assert.Empty(readerExceptions);
    }
}
