using System.Threading.Channels;

namespace OmniLink.Core;

/// <summary>
/// godclass-K6b (ทางเลือก B): คิวคำสั่ง switching จากหลายแหล่ง (network thread ของ node,
/// edge detect, dwell timeout) ไปยัง pump thread เดียว (MouseWorkerLoop) ที่ประมวลทีละคำสั่ง —
/// สถานะ switching (SwitchingState) จึงมีผู้เขียนกลุ่มเดียวจากที่เคยถูก 5 thread แก้พร้อมกัน
/// ข้อจำกัดจริงที่ยังคงอยู่: LL keyboard hook ต้องตัดสินใจ synchronous (ต้องรู้ก่อนว่าจะกลืนคีย์ไหม)
/// → hook thread ยังเป็นผู้เขียนเฉพาะฟิลด์ที่ hook ตัดสินเอง (ModMask/win-key sequence)
/// </summary>
internal sealed class InputPipeline
{
    public enum CmdType : byte
    {
        /// <summary>agent ดันขอบกลับ host (edge_return)</summary>
        EdgeReturn,
        /// <summary>agent ขอสลับไปเครื่องอื่น (switch_request) / edge detect + dwell timeout ฝั่ง host</summary>
        SwitchRequest,
        /// <summary>agent รายงาน focused=false (desync detect → re-assert)</summary>
        FocusedReport,
        /// <summary>สลับด้วยพิกัด norm สำเร็จรูป (dwell timeout — คำนวณฝั่งผู้โพสต์แล้ว)</summary>
        SwitchDirect,
        /// <summary>hotkey actions (Payload = List<HotkeyActionConfig> — hook thread ห้ามทำงานหนักเอง, INV-5)</summary>
        HotkeyActions,
        /// <summary>สลับภาษา (grave/Alt+Shift/calibrate hotkey — งาน network+event ทำบน pump)</summary>
        LanguageToggle,
    }

    public readonly record struct Command(CmdType Type, string NodeId, string Edge, double NormX, double NormY, bool Focused, string Target, object? Payload);

    private readonly Channel<Command> _queue = Channel.CreateBounded<Command>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest, // คำสั่งเก่าหลุดยอมได้ (สถานะล่าสุดชนะเสมอ) — ไม่บล็อกผู้ส่ง
    });

    private ChannelReader<Command> Reader => _queue.Reader;

    /// <summary>โพสต์คำสั่งจาก thread ใดก็ได้ — ไม่มีวันบล็อก/โยน</summary>
    public void Post(CmdType type, string nodeId = "", string edge = "", double normX = 0, double normY = 0, bool focused = false, string target = "", object? payload = null)
    {
        _queue.Writer.TryWrite(new Command(type, nodeId, edge, normX, normY, focused, target, payload));
    }

    /// <summary>drain คำสั่งที่ค้างทั้งหมด ณ ตอนนี้ (pump เรียกทุก loop iteration ก่อนงานอื่น) — คืนจำนวนที่ประมวลผล</summary>
    public int Drain(Action<Command> handle)
    {
        var count = 0;
        while (Reader.TryRead(out var cmd))
        {
            handle(cmd);
            count++;
        }
        return count;
    }

    /// <summary>จำนวนคำสั่งค้างในคิว (test/วินิจฉัย)</summary>
    public int PendingCount => Reader.Count;
}
