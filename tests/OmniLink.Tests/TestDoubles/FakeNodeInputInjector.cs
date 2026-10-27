using System;
using System.Collections.Generic;
using OmniLink.Core;
using OmniLink.Core.Abstractions;

namespace OmniLink.Tests;

// godclass-K7b: ย้ายมาจาก src/OmniLink.Core/Abstractions/INodeInputInjector.cs แบบ move-verbatim —
// test double ไม่ควรอยู่ใน production assembly
/// <summary>Injector เปล่าสำหรับ unit test — บันทึกทุกคำสั่งลง list เพื่อ assert พฤติกรรม</summary>
public sealed class FakeNodeInputInjector : INodeInputInjector
{
    public sealed record Call(string Op, int A, int B, bool Flag);

    public List<Call> Calls { get; } = new();
    public object Lock { get; } = new();
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public (int, int) Pointer { get; set; } = (960, 540);

    public int ScreenWidth { get; private set; }
    public int ScreenHeight { get; private set; }

    public FakeNodeInputInjector()
    {
        ScreenWidth = Width;
        ScreenHeight = Height;
    }

    public void RefreshDimensions()
    {
        ScreenWidth = Width;
        ScreenHeight = Height;
    }

    public (int X, int Y) QueryPointer() => Pointer;

    public void MoveAbsolute(int x, int y)
    {
        lock (Lock) Calls.Add(new Call("abs", x, y, false));
        Pointer = (x, y);
    }

    public (int X, int Y) MoveRelative(int dx, int dy)
    {
        lock (Lock) Calls.Add(new Call("rel", dx, dy, false));
        var (nx, ny) = (Pointer.Item1 + dx, Pointer.Item2 + dy);
        Pointer = (Math.Clamp(nx, 0, ScreenWidth - 1), Math.Clamp(ny, 0, ScreenHeight - 1));
        return Pointer;
    }

    public void MouseButton(int button, bool down)
    {
        lock (Lock) Calls.Add(new Call("btn", button, 0, down));
    }

    public void MouseWheel(int wx, int wy)
    {
        lock (Lock) Calls.Add(new Call("wheel", wx, wy, false));
    }

    public void KeyEvent(int x11Keycode, bool down)
    {
        lock (Lock) Calls.Add(new Call("key", x11Keycode, 0, down));
    }

    public void SyncModifiers(byte modMask)
    {
        lock (Lock) Calls.Add(new Call("mods", modMask, 0, false));
    }

    public void ReleaseAll()
    {
        lock (Lock) Calls.Add(new Call("release", 0, 0, false));
    }

    public void Dispose() { }
}
