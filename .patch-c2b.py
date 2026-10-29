# -*- coding: utf-8 -*-
# C2b: WM_CLIPBOARDUPDATE listener + echo suppression + shared store
B = chr(92)
p = 'src/OmniLink.Platform.Windows/Interop/NativeMethods.cs'
src = open(p, encoding='utf-8').read()
anchor = '''    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);'''
assert anchor in src
add = anchor + '''

    // C2: clipboard format listener — OS แจ้งเมื่อ clipboard เปลี่ยน (เบา ไม่ใช่ polling)
    public const uint WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(nint hwnd);'''
src = src.replace(anchor, add, 1)
open(p, 'w', encoding='utf-8').write(src)
print("nat OK")
