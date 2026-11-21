"""UI Automation 截图辅助：按窗口标题/PID 找窗口，PrintWindow 截图存 PNG。"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import time
from pathlib import Path

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

EnumWindows = user32.EnumWindows
EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
GetWindowTextW = user32.GetWindowTextW
GetWindowTextLengthW = user32.GetWindowTextLengthW
IsWindowVisible = user32.IsWindowVisible
GetWindowThreadProcessId = user32.GetWindowThreadProcessId
GetWindowRect = user32.GetWindowRect
PrintWindow = user32.PrintWindow
SetForegroundWindow = user32.SetForegroundWindow
ShowWindow = user32.ShowWindow


def find_windows(title_sub: str | None = None, pid: int | None = None):
    hits = []

    def cb(hwnd, lparam):
        if not IsWindowVisible(hwnd):
            return True
        if pid is not None:
            wp = wt.DWORD(0)
            GetWindowThreadProcessId(hwnd, ctypes.byref(wp))
            if wp.value != pid:
                return True
        if title_sub is not None:
            n = GetWindowTextLengthW(hwnd)
            if n == 0:
                return True
            buf = ctypes.create_unicode_buffer(n + 1)
            GetWindowTextW(hwnd, buf, n + 1)
            if title_sub not in buf.value:
                return True
        hits.append(hwnd)
        return True

    EnumWindows(EnumWindowsProc(cb), 0)
    return hits


def wait_for_window(title_sub: str | None, timeout: float = 30, pid: int | None = None,
                    min_w: int = 800, min_h: int = 500):
    """等主窗口出现且有合理尺寸，返回面积最大的那个 hwnd。"""
    t0 = time.time()
    while time.time() - t0 < timeout:
        hits = find_windows(title_sub, pid)
        sized = []
        for h in hits:
            r = wt.RECT()
            GetWindowRect(h, ctypes.byref(r))
            w, hh = r.right - r.left, r.bottom - r.top
            if w >= min_w and hh >= min_h:
                sized.append((w * hh, h))
        if sized:
            sized.sort(reverse=True)
            return sized[0][1]
        time.sleep(0.3)
    raise TimeoutError(f"window not found: title={title_sub!r} pid={pid}")


def wait_gone(pid: int, timeout: float = 30):
    """等指定 pid 的所有可见窗口消失。"""
    t0 = time.time()
    while time.time() - t0 < timeout:
        if not find_windows(None, pid):
            return
        time.sleep(0.3)
    raise TimeoutError(f"windows of pid {pid} never went away")


def screenshot(hwnd, out_path: str):
    rect = wt.RECT()
    GetWindowRect(hwnd, ctypes.byref(rect))
    w = rect.right - rect.left
    h = rect.bottom - rect.top
    if w <= 0 or h <= 0:
        raise RuntimeError(f"bad rect {w}x{h}")

    hwndDC = user32.GetWindowDC(hwnd)
    mfcDC = gdi32.CreateCompatibleDC(hwndDC)
    saveBitMap = gdi32.CreateCompatibleBitmap(hwndDC, w, h)
    gdi32.SelectObject(mfcDC, saveBitMap)
    # PW_RENDERFULLCONTENT = 2
    r = PrintWindow(hwnd, mfcDC, 2)
    if r == 0:
        r = PrintWindow(hwnd, mfcDC, 0)
    if r == 0:
        raise RuntimeError("PrintWindow failed")

    bmp = Bitmap.from_hbitmap(saveBitMap)
    bmp.save(out_path, "PNG")
    gdi32.DeleteObject(saveBitMap)
    gdi32.DeleteDC(mfcDC)
    user32.ReleaseDC(hwnd, hwndDC)
    return Path(out_path).resolve()


# System.Drawing.Bitmap 通过 pythonnet？不，标准 CPython 没有。
# 改用 PIL：CreateCompatibleBitmap + GetDIBits。
from PIL import Image  # noqa: E402


class Bitmap:  # minimal wrapper
    def __init__(self, hbmp, w, h):
        self.hbmp = hbmp
        self.w = w
        self.h = h

    @classmethod
    def from_hbitmap(cls, hbmp):
        # 需要 w/h——外层已算好，改用全局传递太丑；重构 screenshot 直接内联。
        raise NotImplementedError

    def save(self, path, fmt):
        raise NotImplementedError


# 简化：重写 screenshot，不走 Bitmap 类
def screenshot(hwnd, out_path: str):  # noqa: F811
    rect = wt.RECT()
    GetWindowRect(hwnd, ctypes.byref(rect))
    w = rect.right - rect.left
    h = rect.bottom - rect.top
    if w <= 0 or h <= 0:
        raise RuntimeError(f"bad rect {w}x{h}")

    hwndDC = user32.GetWindowDC(hwnd)
    mfcDC = gdi32.CreateCompatibleDC(hwndDC)
    saveBitMap = gdi32.CreateCompatibleBitmap(hwndDC, w, h)
    gdi32.SelectObject(mfcDC, saveBitMap)
    r = PrintWindow(hwnd, mfcDC, 2) or PrintWindow(hwnd, mfcDC, 0)
    if r == 0:
        user32.ReleaseDC(hwnd, hwndDC)
        gdi32.DeleteDC(mfcDC)
        gdi32.DeleteObject(saveBitMap)
        raise RuntimeError("PrintWindow failed")

    # BITMAPINFOHEADER
    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [
            ("biSize", wt.DWORD),
            ("biWidth", wt.LONG),
            ("biHeight", wt.LONG),
            ("biPlanes", wt.WORD),
            ("biBitCount", wt.WORD),
            ("biCompression", wt.DWORD),
            ("biSizeImage", wt.DWORD),
            ("biXPelsPerMeter", wt.LONG),
            ("biYPelsPerMeter", wt.LONG),
            ("biClrUsed", wt.DWORD),
            ("biClrImportant", wt.DWORD),
        ]

    class BITMAPINFO(ctypes.Structure):
        _fields_ = [("bmiHeader", BITMAPINFOHEADER), ("bmiColors", wt.DWORD * 3)]

    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h  # top-down
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    bmi.bmiHeader.biCompression = 0  # BI_RGB

    buf = ctypes.create_string_buffer(w * h * 4)
    hdc_screen = user32.GetDC(0)
    gdi32.GetDIBits(hdc_screen, saveBitMap, 0, h, buf, ctypes.byref(bmi), 0)
    user32.ReleaseDC(0, hdc_screen)

    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")
    img.save(out_path, "PNG")

    gdi32.DeleteObject(saveBitMap)
    gdi32.DeleteDC(mfcDC)
    user32.ReleaseDC(hwnd, hwndDC)
    return Path(out_path).resolve()


# UIA 部分：用 comtypes
def uia_root():
    import comtypes.client
    uia = comtypes.client.CreateObject("{ff48dba4-60ef-4201-aa87-54103eef594e}",
                                       interface=comtypes.client.GetModule("UIAutomationCore.dll").IUIAutomation)
    return uia


def uia_find_by_automation_id(hwnd, automation_id, timeout=10):
    import comtypes.client
    uia_module = comtypes.client.GetModule("UIAutomationCore.dll")
    uia = comtypes.client.CreateObject("{ff48dba4-60ef-4201-aa87-54103eef594e}",
                                       interface=uia_module.IUIAutomation)
    elem = uia.ElementFromHandle(hwnd)
    cond = uia.CreatePropertyCondition(uia_module.UIA_AutomationIdPropertyId, automation_id)
    t0 = time.time()
    while time.time() - t0 < timeout:
        found = elem.FindFirst(uia_module.TreeScope_Descendants, cond)
        if found:
            return found
        time.sleep(0.3)
    return None


def uia_click(elem):
    import comtypes.client
    uia_module = comtypes.client.GetModule("UIAutomationCore.dll")
    pattern = elem.GetCurrentPattern(uia_module.UIA_InvokePatternId)
    if pattern:
        invoke = pattern.QueryInterface(uia_module.IUIAutomationInvokePattern)
        invoke.Invoke()
        return True
    return False
