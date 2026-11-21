# -*- coding: utf-8 -*-
"""隔离账户会话与 MSI 窗口操作工具（仅用于临时 MosaicQa 账户的界面验收）。

- run_as: 以隔离账户身份、携带显式子目录环境启动进程（与当前会话同一桌面）。
- wait / exit_code: 等待进程结束并读取退出码。
- windows / children: 枚举该进程的顶层窗口与子控件文本。
- click: 按控件文本发送 BM_CLICK。
- progress_pos: 读取进度条位置。
- screenshot: PrintWindow 截图保存 PNG。
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import subprocess
import time
from pathlib import Path

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
u32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)

CREATE_UNICODE_ENVIRONMENT = 0x00000400
LOGON_WITH_PROFILE = 0x00000001
STARTF_USESHOWWINDOW = 0x00000001
SW_SHOWNORMAL = 1
INFINITE = 0xFFFFFFFF

EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


class STARTUPINFO(ctypes.Structure):
    _fields_ = [
        ("cb", wt.DWORD), ("lpReserved", wt.LPWSTR), ("lpDesktop", wt.LPWSTR),
        ("lpTitle", wt.LPWSTR), ("dwX", wt.DWORD), ("dwY", wt.DWORD),
        ("dwXSize", wt.DWORD), ("dwYSize", wt.DWORD), ("dwXCountChars", wt.DWORD),
        ("dwYCountChars", wt.DWORD), ("dwFillAttribute", wt.DWORD), ("dwFlags", wt.DWORD),
        ("wShowWindow", wt.WORD), ("cbReserved2", wt.WORD),
        ("lpReserved2", ctypes.c_void_p), ("hStdInput", wt.HANDLE),
        ("hStdOutput", wt.HANDLE), ("hStdError", wt.HANDLE),
    ]


class PROCESS_INFORMATION(ctypes.Structure):
    _fields_ = [("hProcess", wt.HANDLE), ("hThread", wt.HANDLE),
                ("dwProcessId", wt.DWORD), ("dwThreadId", wt.DWORD)]


def read_cred(path: str) -> tuple[str, str]:
    name, pw = Path(path).read_text(encoding="ascii").splitlines()[:2]
    return name.strip(), pw.strip()


def default_env(profile: str) -> dict[str, str]:
    """隔离账户的显式环境：用户目录全部指向其自身配置文件，避免写入真实用户数据。"""
    root = profile.rstrip("\\")
    local = root + r"\AppData\Local"
    roaming = root + r"\AppData\Roaming"
    systemroot = r"C:\Windows"
    return {
        "USERPROFILE": root,
        "LOCALAPPDATA": local,
        "APPDATA": roaming,
        "TEMP": local + r"\Temp",
        "TMP": local + r"\Temp",
        "HOMEDRIVE": "C:",
        "HOMEPATH": root[2:],
        "USERNAME": Path(root).name,
        "USERDOMAIN": "LAPTOP-E7DLPOG1",
        "SystemRoot": systemroot,
        "windir": systemroot,
        "SystemDrive": "C:",
        "COMSPEC": systemroot + r"\system32\cmd.exe",
        "PATH": systemroot + r"\system32;" + systemroot + ";" + systemroot + r"\System32\Wbem;" + systemroot + "\\System32\\WindowsPowerShell\\v1.0\\",
        "PATHEXT": ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC",
        "PROCESSOR_ARCHITECTURE": "AMD64",
        "OS": "Windows_NT",
        "ALLUSERSPROFILE": r"C:\ProgramData",
        "ProgramData": r"C:\ProgramData",
        "PUBLIC": r"C:\Users\Public",
        "ProgramFiles": r"C:\Program Files",
        "ProgramFiles(x86)": r"C:\Program Files (x86)",
        "CommonProgramFiles": r"C:\Program Files\Common Files",
        "CommonProgramFiles(x86)": r"C:\Program Files (x86)\Common Files",
        "NUMBER_OF_PROCESSORS": "8",
    }


def _env_block(env: dict[str, str]) -> ctypes.Array:
    text = "".join(f"{k}={v}\0" for k, v in env.items()) + "\0"
    return ctypes.create_unicode_buffer(text, len(text) + 1)


def run_as(cred_path: str, profile: str, exe: str, args: str, env: dict[str, str] | None = None,
           cwd: str | None = None, wait: bool = False, timeout: float | None = None):
    """以隔离账户启动进程。返回 (pid, exit_code)；wait=False 时 exit_code 为 None。"""
    name, pw = read_cred(cred_path)
    env = env or default_env(profile)
    block = _env_block(env)
    si = STARTUPINFO()
    si.cb = ctypes.sizeof(STARTUPINFO)
    si.dwFlags = STARTF_USESHOWWINDOW
    si.wShowWindow = SW_SHOWNORMAL
    pi = PROCESS_INFORMATION()
    # lpApplicationName 非空时，lpCommandLine 仍须以程序名开头（argv[0]），否则目标程序
    # 会把首个开关当成 argv[0] 丢弃（msiexec 返回 1639）。
    cmdline = f'"{exe}"' + (f" {args}" if args else "")
    ok = advapi32.CreateProcessWithLogonW(
        ctypes.c_wchar_p(name), ctypes.c_wchar_p("."),
        ctypes.c_wchar_p(pw), LOGON_WITH_PROFILE,
        ctypes.c_wchar_p(exe), ctypes.c_wchar_p(cmdline),
        CREATE_UNICODE_ENVIRONMENT, ctypes.byref(block),
        ctypes.c_wchar_p(cwd) if cwd else None,
        ctypes.byref(si), ctypes.byref(pi))
    if not ok:
        raise OSError(ctypes.get_last_error(), "CreateProcessWithLogonW failed")
    pid = pi.dwProcessId
    if not wait:
        k32.CloseHandle(pi.hThread)
        k32.CloseHandle(pi.hProcess)
        return pid, None
    rc = wait_pid(pid, timeout or 1800)
    k32.CloseHandle(pi.hThread)
    k32.CloseHandle(pi.hProcess)
    return pid, rc


def wait_pid(pid: int, timeout: float = 1800) -> int | None:
    """等待进程退出；超时返回 None。"""
    h = k32.OpenProcess(0x00100000 | 0x1000, False, pid)  # SYNCHRONIZE|QUERY_LIMITED
    if not h:
        return None
    try:
        ms = int(timeout * 1000)
        if k32.WaitForSingleObject(h, ms) != 0:
            return None
        code = wt.DWORD()
        k32.GetExitCodeProcess(h, ctypes.byref(code))
        return code.value
    finally:
        k32.CloseHandle(h)


def process_gone(pid: int) -> bool:
    h = k32.OpenProcess(0x1000, False, pid)
    if not h:
        return True
    k32.CloseHandle(h)
    return False


def _text(hwnd: int) -> str:
    n = u32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(n + 1)
    u32.GetWindowTextW(hwnd, buf, n + 1)
    return buf.value


def _class(hwnd: int) -> str:
    buf = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def windows(pid: int) -> list[dict]:
    out = []
    rects = {}

    class RECT(ctypes.Structure):
        _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                    ("right", ctypes.c_long), ("bottom", ctypes.c_long)]

    def cb(hwnd, _):
        p = wt.DWORD()
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and u32.IsWindowVisible(hwnd):
            r = RECT()
            u32.GetWindowRect(hwnd, ctypes.byref(r))
            rects[hwnd] = (r.right - r.left, r.bottom - r.top)
            out.append({"hwnd": hwnd, "title": _text(hwnd), "class": _class(hwnd),
                        "size": rects[hwnd]})
        return True

    u32.EnumWindows(EnumWindowsProc(cb), 0)
    return out


def children(hwnd: int) -> list[dict]:
    out = []

    def cb(h, _):
        out.append({"hwnd": h, "id": u32.GetDlgCtrlID(h), "class": _class(h), "text": _text(h)})
        return True

    u32.EnumChildWindows(hwnd, EnumWindowsProc(cb), 0)
    return out


def find_child(hwnd: int, text: str, cls: str | None = None) -> int | None:
    for c in children(hwnd):
        if c["text"] == text and (cls is None or c["class"] == cls):
            return c["hwnd"]
    return None


def click(hwnd: int, text: str) -> bool:
    h = find_child(hwnd, text, "Button")
    if not h:
        return False
    u32.SendMessageW(h, 0x00F5, 0, 0)  # BM_CLICK
    return True


def send_click(hwnd_button: int) -> bool:
    """对已知按钮句柄发送 BM_CLICK。"""
    return bool(u32.SendMessageW(hwnd_button, 0x00F5, 0, 0) or True)


def progress_pos(hwnd: int) -> int | None:
    for c in children(hwnd):
        if c["class"] == "msctls_progress32":
            return u32.SendMessageW(c["hwnd"], 0x0408, 0, 0)  # PBM_GETPOS
    return None


def tooltip_text(hwnd: int) -> str:
    """进度窗口的提示条文本（安装时通常显示当前操作）。"""
    found = []
    for c in children(hwnd):
        if c["class"] == "Static" and c["text"]:
            found.append(c["text"])
    return " | ".join(found)


def wait_window(pid: int, timeout: float = 60, title: str | None = None) -> dict | None:
    end = time.time() + timeout
    while time.time() < end:
        for w in windows(pid):
            if title is None or title in w["title"]:
                return w
        time.sleep(0.2)
    return None


def screenshot(hwnd: int, path: str) -> str:
    rect = wt.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top
    hdc = u32.GetWindowDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    u32.PrintWindow(hwnd, mem, 2)  # PW_RENDERFULLCONTENT
    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                    ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]
    bih = BITMAPINFOHEADER()
    bih.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bih.biWidth, bih.biHeight = w, h
    bih.biPlanes, bih.biBitCount, bih.biCompression = 1, 32, 0
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bih), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    u32.ReleaseDC(hwnd, hdc)
    from PIL import Image
    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")
    img.save(path)
    return path


def read_log_text(path: str) -> str:
    """MSI 日志为 UTF-16LE（带 BOM），普通文本为 UTF-8/GBK。"""
    p = Path(path)
    if not p.exists():
        return ""
    raw = p.read_bytes()
    if raw.startswith(b"\xff\xfe"):
        return raw.decode("utf-16")
    if raw.startswith(b"\xfe\xff"):
        return raw.decode("utf-16-be")
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw.decode("utf-8-sig")
    for enc in ("utf-8", "gbk"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


def log_tail(path: str, count: int = 40) -> str:
    lines = read_log_text(path).splitlines()
    return "\n".join(lines[-count:])


def run_probe(cred_path: str, profile: str, cmd: str, log_path: str) -> int:
    """以隔离账户运行 cmd，把 stdout/stderr 写入 log_path，返回退出码。"""
    exe = r"C:\Windows\System32\cmd.exe"
    pid, rc = run_as(cred_path, profile, exe, f'/c ({cmd}) > "{log_path}" 2>&1', wait=True, timeout=120)
    return rc


def bash(cmd: str) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, shell=True, capture_output=True, text=True, encoding="utf-8", errors="replace")