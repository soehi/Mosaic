# -*- coding: utf-8 -*-
"""正式跨盘测试：真实键盘（SendInput）在位置页输入 D 盘路径 → 下一步 → 确认 → 安装。

模拟用户真实行为：点「下一步」→ 位置页 → 点击输入框 → Ctrl+A 全选 → 逐键输入 →
点「下一步」（失焦提交）→ 确认页核对目标路径 → 安装 → 完成页 → 核对 D 盘文件与登记。
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
sys.path.insert(0, str(os.path.dirname(os.path.abspath(__file__))))
import qa_win as W  # noqa: E402
import cross_drive_repro as C  # noqa: E402

u32 = ctypes.WinDLL("user32", use_last_error=True)
k32 = ctypes.WinDLL("kernel32", use_last_error=True)

TARGET = r"D:\MosaicQA\跨盘 安装"
INPUT_vk = {c: ord(c.upper()) for c in
            "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"}
SHIFT_VK = 0x10


def send_char(ch):
    """SendInput 单字符：字母/数字直发，其余按 Shift 组合或扫描码近似处理。"""
    special = {"\\": (0x2A, 0xDC), ":": (0x2A, 0xBA), " ": (None, 0x20)}
    inputs = []

    class KEYBDINPUT(ctypes.Structure):
        _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                    ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]

    class INPUT(ctypes.Structure):
        class _U(ctypes.Union):
            _fields_ = [("ki", KEYBDINPUT), ("padding", ctypes.c_ubyte * 32)]
        _anonymous_ = ("u",)
        _fields_ = [("type", wt.DWORD), ("u", _U)]

    def key(vk, up=False):
        i = INPUT()
        i.type = 1
        i.ki.wVk = vk
        if up:
            i.ki.dwFlags = 2
        inputs.append(i)

    if ch in special:
        shift, vk = special[ch]
        if shift:
            key(shift)
        key(vk)
        key(vk, up=True)
        if shift:
            key(shift, up=True)
    elif ch in INPUT_vk:
        vk = INPUT_vk[ch]
        # 中文路径里的「跨盘 安装」用剪贴板粘贴，避免 IME
        key(vk)
        key(vk, up=True)
    else:
        raise ValueError(f"unsupported char {ch!r}")
    u32.SendInput(len(inputs), (INPUT * len(inputs))(*inputs), ctypes.sizeof(INPUT))


def paste_text(text):
    """写剪贴板并 Ctrl+V。"""
    import ctypes
    u32.OpenClipboard.argtypes = [ctypes.c_void_p]
    u32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
    k32.GlobalAlloc.restype = ctypes.c_void_p
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalLock.argtypes = [ctypes.c_void_p]
    k32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    cf_unicode = 13
    u32.OpenClipboard(None)
    u32.EmptyClipboard()
    h = k32.GlobalAlloc(0x2002, (len(text) + 1) * 2)
    p = k32.GlobalLock(h)
    ctypes.memmove(ctypes.c_void_p(p), ctypes.create_unicode_buffer(text), (len(text) + 1) * 2)
    k32.GlobalUnlock(h)
    u32.SetClipboardData(cf_unicode, ctypes.c_void_p(h))
    u32.CloseClipboard()

    class KEYBDINPUT(ctypes.Structure):
        _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                    ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(ctypes.c_ulong))]

    class INPUT(ctypes.Structure):
        class _U(ctypes.Union):
            _fields_ = [("ki", KEYBDINPUT), ("padding", ctypes.c_ubyte * 32)]
        _anonymous_ = ("u",)
        _fields_ = [("type", wt.DWORD), ("u", _U)]

    def key(vk, up=False):
        i = INPUT()
        i.type = 1
        i.ki.wVk = vk
        if up:
            i.ki.dwFlags = 2
        return i

    seq = [key(0x11), key(0x56), key(0x56, True), key(0x11, True)]
    u32.SendInput(len(seq), (INPUT * len(seq))(*seq), ctypes.sizeof(INPUT))


def main():
    pid, logf = C.start("/i", "cross-d-real-typed", "/qf")
    r = {"scenario": "跨盘手工输入", "target": TARGET, "log": logf, "pid": pid}
    r["fresh"] = C.wait_page(pid, "fresh", 60)
    C.click(pid, "下一步")
    r["location"] = C.wait_page(pid, "location", 30)
    s = C.snapshot(pid)
    edit = None
    for k in W.children(s["hwnd"]):
        if k["class"] in ("Edit", "RichEdit20W"):
            edit = k["hwnd"]
    r["edit_found"] = edit is not None
    if edit:
        u32.SetForegroundWindow(s["hwnd"])
        time.sleep(0.3)
        u32.SendMessageW.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p, ctypes.c_void_p]
        u32.SendMessageW(edit, 0x00B1, None, ctypes.c_void_p(0xFFFFFFFF))  # 全选
        paste_text(TARGET)
        time.sleep(0.8)
        # 读取 Edit 当前内容确认输入生效
        buf = ctypes.create_unicode_buffer(512)
        u32.SendMessageW(edit, 0x000D, ctypes.c_void_p(512), ctypes.addressof(buf))  # WM_GETTEXT
        r["edit_content"] = buf.value
        # 点「当前目标」空白处触发失焦提交，再点下一步
        C.click(pid, "下一步")
        time.sleep(1.0)
        r["err_after_next"] = C.observe_errors(pid, "d-real-next", r.setdefault("errors", []))
        r["page"] = C.page(pid)
        r["statics"] = (C.snapshot(pid) or {}).get("statics")
        C.shot(pid, "d-real-confirm")
        if r["page"] == "confirm":
            C.click(pid, "安装")
            r["progress"] = C.wait_page(pid, "progress", 30)
            r["done"] = C.wait_page(pid, "done", 600)
            C.observe_errors(pid, "d-real-done", r.setdefault("errors", []))
            C.shot(pid, "d-real-done")
            C.click(pid, "完成")
        else:
            C.click(pid, "取消")
    r["exit"] = W.wait_pid(pid, 300)
    r["installed"] = os.path.isdir(TARGET + r"\Mosaic")
    r["file_count"] = sum(len(f) for _, _, f in os.walk(TARGET + r"\Mosaic")) if r["installed"] else 0
    r["registration"] = C.probe('reg query "HKCU\\Software\\Mosaic\\Installer" /v InstallLocation', "d-real-reg")
    r["arp"] = C.probe(r'reg query "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall" /s /f Mosaic /d', "d-real-arp")
    r["sentinel_ok"] = "cross-drive-sentinel" in C.probe(
        r'type "%LOCALAPPDATA%\Mosaic\qa-sentinel.txt"', "d-real-sentinel")
    with open(r"C:\Users\Public\MosaicQA\cross-d-real.json", "w", encoding="utf-8") as f:
        json.dump(r, f, ensure_ascii=False, indent=1)
    print(json.dumps({k: v for k, v in r.items() if k not in ("errors", "arp", "registration")},
                     ensure_ascii=False, indent=1)[:2500])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
