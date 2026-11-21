# -*- coding: utf-8 -*-
"""跨盘安装复现：装 C 盘 → 卸载 → 完整 UI 选 D 盘（手工输入与浏览两种入口）。

捕获：每步窗口文本/按钮、意外错误框（class #32770 标题 Mosaic）、MSI /L*V! 日志。
仅作用于临时账户 MosaicQa0176 与其自身 profile；结果写 C:\\Users\\Public\\MosaicQA。
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import os
import re
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
sys.path.insert(0, str(os.path.dirname(os.path.abspath(__file__))))
import qa_win as W  # noqa: E402

CRED = r"C:\Users\ASUS\AppData\Local\Temp\mosaic-qa-cred.txt"
PROFILE = r"C:\Users\MosaicQa0176"
QA = r"C:\Users\Public\MosaicQA"
LOGS = QA + r"\logs"
SHOTS = QA + r"\shots"
MSI = r"D:\Code\Mosaic\artifacts\installer\Mosaic-0.17.6-win-x64.msi"
MSIEXEC = r"C:\Windows\System32\msiexec.exe"
TARGET_PARENT = r"D:\MosaicQA\跨盘 安装"

u32 = ctypes.WinDLL("user32", use_last_error=True)


def log(m):
    print(m, flush=True)


def windows(pid):
    out = []

    class RECT(ctypes.Structure):
        _fields_ = [("l", ctypes.c_long), ("t", ctypes.c_long), ("r", ctypes.c_long), ("b", ctypes.c_long)]

    def cb(hwnd, _):
        p = wt.DWORD()
        u32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and u32.IsWindowVisible(hwnd):
            r = RECT()
            u32.GetWindowRect(hwnd, ctypes.byref(r))
            out.append({"hwnd": hwnd, "title": W._text(hwnd), "class": W._class(hwnd),
                        "size": (r.r - r.l, r.b - r.t)})
        return True

    u32.EnumWindows(W.EnumWindowsProc(cb), 0)
    return out


def main_window(pid):
    ws = [w for w in windows(pid) if w["class"] == "MsiDialogCloseClass"]
    return ws[0] if ws else None


def error_boxes(pid):
    """MSI 引擎错误框：class #32770，标题为产品名。"""
    return [w for w in windows(pid) if w["class"] == "#32770"]


def snapshot(pid):
    w = main_window(pid)
    if not w:
        return None
    kids = W.children(w["hwnd"])
    return {"hwnd": w["hwnd"], "title": w["title"], "size": w["size"],
            "buttons": [k["text"] for k in kids if k["class"] == "Button" and k["text"]],
            "statics": [k["text"] for k in kids if k["class"] == "Static" and k["text"]],
            "progress": W.progress_pos(w["hwnd"])}


def page(pid):
    s = snapshot(pid)
    if not s:
        return None
    for key, text in (("fresh", "安装 Mosaic"), ("location", "选择安装位置"), ("confirm", "确认安装"),
                      ("progress", "正在处理，请稍候..."), ("done", "操作已完成"),
                      ("maintenance", "安装完整，无需修复"), ("upgrade", "升级 Mosaic"),
                      ("cancel", "确定要取消 Mosaic 安装吗？"), ("userexit", "安装已取消")):
        if any(text in t for t in s["statics"]) or text in s["title"]:
            return key
    return None


def wait_page(pid, want, timeout=60):
    end = time.time() + timeout
    while time.time() < end:
        if page(pid) == want:
            return True
        if W.process_gone(pid):
            return False
        time.sleep(0.25)
    return False


def click(pid, label):
    end = time.time() + 20
    while time.time() < end:
        s = snapshot(pid)
        if s and label in s["buttons"]:
            h = W.find_child(s["hwnd"], label, "Button")
            if h:
                W.send_click(h)
                return True
        time.sleep(0.2)
    return False


def edit_hwnd(pid):
    s = snapshot(pid)
    if not s:
        return None
    for k in W.children(s["hwnd"]):
        if k["class"] == "Edit":
            return k["hwnd"]
    return None


def type_path(pid, text):
    """模拟手工输入：聚焦编辑框 → 全选 → 输入 → 失焦（点击当前目标/标题）。

    走真实键盘消息（WM_CHAR），与用户键入一致；随后 WM_KILLFOCUS 触发 PathEdit 回写属性。
    """
    h = edit_hwnd(pid)
    if not h:
        return False
    u32.SendMessageW(h, 0x0007, 0, 0)  # WM_SETFOCUS
    u32.SendMessageW(h, 0x00B1, 0, 0)  # EM_SETSEL 0,0? 需要 lParam
    u32.SendMessageW(h, 0x00B1, 0, -1)  # EM_SETSEL(0,-1) 全选
    for ch in text:
        u32.SendMessageW(h, 0x0102, ord(ch), 0)  # WM_CHAR
    # 失焦：焦点移回对话框本身
    s = snapshot(pid)
    u32.SendMessageW(h, 0x0008, 0, 0)  # WM_KILLFOCUS
    u32.SendMessageW(s["hwnd"], 0x0007, 0, 0)
    return True


def shot(pid, tag):
    w = main_window(pid) or (error_boxes(pid) or [None])[0]
    if not w:
        return None
    p = f"{SHOTS}\\{tag}.png"
    try:
        W.screenshot(w["hwnd"], p)
        return p
    except Exception as exc:
        log(f"  [warn] 截图失败 {tag}: {exc}")
        return None


def start(mode, name, extra=""):
    logf = f"{LOGS}\\{name}.log"
    if os.path.exists(logf):
        os.remove(logf)
    args = f'{mode} "{MSI}" /L*V! "{logf}"'
    if extra:
        args += " " + extra
    pid, _ = W.run_as(CRED, PROFILE, MSIEXEC, args)
    return pid, logf


def start_watch(mode, msi, name, extra=""):
    """静默场景：启动后轮询窗口，记录出现过的任何窗口与退出码。"""
    pid, logf = start(mode, name, extra)
    seen = []
    while not W.process_gone(pid):
        for s in [snapshot(pid)]:
            if s:
                key = (s["title"], tuple(s["buttons"]))
                if key not in [(x["title"], tuple(x["buttons"])) for x in seen]:
                    seen.append({"title": s["title"], "buttons": s["buttons"], "statics": s["statics"]})
        time.sleep(0.4)
    rc = W.wait_pid(pid, 30)
    return pid, logf, {"windows": seen, "exit": rc}


def observe_errors(pid, tag, collected):
    boxes = error_boxes(pid)
    for b in boxes:
        kids = W.children(b["hwnd"])
        text = " | ".join(k["text"] for k in kids if k["text"])
        entry = {"tag": tag, "title": b["title"], "size": b["size"],
                 "buttons": [k["text"] for k in kids if k["class"] == "Button" and k["text"]],
                 "text": text}
        if entry not in collected:
            collected.append(entry)
            log(f"  [error-box] {tag}: {text[:300]}")
            try:
                W.screenshot(b["hwnd"], f"{SHOTS}\\err-{tag}.png")
            except Exception:
                pass
    return bool(boxes)


def close_error(pid):
    for b in error_boxes(pid):
        W.click(b["hwnd"], "确定") or W.click(b["hwnd"], "取消")


def probe(cmd, name):
    p = f"{LOGS}\\{name}.txt"
    W.run_probe(CRED, PROFILE, cmd, p)
    return W.read_log_text(p)


def main():
    os.makedirs(LOGS, exist_ok=True)
    os.makedirs(SHOTS, exist_ok=True)
    os.makedirs(TARGET_PARENT, exist_ok=True)
    result = {"target_parent": TARGET_PARENT, "errors": [], "steps": []}

    # 0. 复位：卸载任何已登记产品、清默认目录与目标目录
    codes = []
    out = probe(r'reg query "HKCU\Software\Microsoft\Installer\Products"', "products")
    for line in out.splitlines():
        line = line.strip()
        if line.startswith("HKEY_CURRENT_USER\\Software\\Microsoft\\Installer\\Products\\"):
            packed = line.rsplit("\\", 1)[-1]
            if len(packed) == 32:
                codes.append("{%s-%s-%s-%s-%s}" % (
                    packed[6:8] + packed[4:6] + packed[2:4] + packed[0:2],
                    packed[10:12] + packed[8:10], packed[14:16] + packed[12:14],
                    packed[16:18] + packed[18:20],
                    packed[20:22] + packed[22:24] + packed[24:26] + packed[26:28] + packed[28:30] + packed[30:32]))
    for code in codes:
        _, _ = W.run_as(CRED, PROFILE, MSIEXEC, f'/x {code} /qn', wait=True, timeout=300)
    probe(r'rmdir /s /q "%LOCALAPPDATA%\Programs\Mosaic" & rmdir /s /q "D:\MosaicQA" & reg delete "HKCU\Software\Mosaic" /f & echo RESET', "reset")
    result["reset_codes"] = codes

    # 1. 默认 C 盘完整 UI 安装
    pid, log_c = start("/i", "cross-c-install", "/qf")
    ok_fresh = wait_page(pid, "fresh", 60)
    click(pid, "下一步")
    ok_loc = wait_page(pid, "location", 30)
    result["steps"].append({"step": "C 盘默认安装到位置页", "fresh": ok_fresh, "location": ok_loc,
                            "location_text": snapshot(pid)["statics"] if ok_loc else None})
    observe_errors(pid, "c-location", result["errors"])
    click(pid, "下一步")
    result["steps"].append({"step": "C 盘确认页", "confirm": wait_page(pid, "confirm", 20)})
    click(pid, "安装")
    result["steps"].append({"step": "C 盘进度页", "progress": wait_page(pid, "progress", 30)})
    result["steps"].append({"step": "C 盘完成页", "done": wait_page(pid, "done", 600)})
    observe_errors(pid, "c-done", result["errors"])
    shot(pid, "c-done")
    click(pid, "完成")
    result["c_exit"] = W.wait_pid(pid, 300)
    result["c_log"] = log_c

    # 2. 正常卸载（UI），核验登记消失、哨兵数据保留
    probe(r'mkdir "%LOCALAPPDATA%\Mosaic" 2>nul & echo cross-drive-sentinel> "%LOCALAPPDATA%\Mosaic\qa-sentinel.txt"', "sentinel")
    pid, log_u = start("/x", "cross-c-uninstall")
    if wait_page(pid, "progress", 30):
        pass
    if wait_page(pid, "done", 120):
        click(pid, "完成")
    else:
        # 卸载可能直接结束
        for b in (snapshot(pid) or {}).get("buttons", []):
            click(pid, b)
    result["u_exit"] = W.wait_pid(pid, 300)
    result["u_log"] = log_u
    result["products_after_uninstall"] = probe(r'reg query "HKCU\Software\Microsoft\Installer\Products"', "products-u")
    result["sentinel_after_uninstall"] = probe(r'type "%LOCALAPPDATA%\Mosaic\qa-sentinel.txt"', "sentinel-u").strip()

    # 3. 重新完整 UI，手工输入 D 盘路径
    pid, log_d = start("/i", "cross-d-typed", "/qf")
    wait_page(pid, "fresh", 60)
    click(pid, "下一步")
    wait_page(pid, "location", 30)
    result["typed_input"] = type_path(pid, TARGET_PARENT)
    time.sleep(0.5)
    observe_errors(pid, "d-typed-before-next", result["errors"])
    shot(pid, "d-typed-location")
    click(pid, "下一步")
    time.sleep(1.0)
    result["typed_err_box"] = observe_errors(pid, "d-typed-after-next", result["errors"])
    result["typed_page"] = page(pid)
    result["typed_statics"] = (snapshot(pid) or {}).get("statics")
    shot(pid, "d-typed-after-next")
    if result["typed_page"] == "confirm":
        click(pid, "安装")
        result["typed_progress"] = wait_page(pid, "progress", 30)
        result["typed_done"] = wait_page(pid, "done", 600)
        observe_errors(pid, "d-typed-done", result["errors"])
        click(pid, "完成")
    else:
        click(pid, "取消")
    result["d_typed_exit"] = W.wait_pid(pid, 300)
    result["d_typed_log"] = log_d
    result["d_typed_installed"] = os.path.isdir(TARGET_PARENT + r"\Mosaic")
    result["d_typed_file_count"] = sum(len(f) for _, _, f in os.walk(TARGET_PARENT + r"\Mosaic")) if result["d_typed_installed"] else 0

    # 清理本轮安装，避免影响浏览复现
    probe(r'rmdir /s /q "D:\MosaicQA\跨盘 安装" & reg delete "HKCU\Software\Mosaic" /f', "after-typed")

    # 4. 浏览入口：手工在文件夹选择框输入 D 盘
    pid, log_b = start("/i", "cross-d-browse", "/qf")
    wait_page(pid, "fresh", 60)
    click(pid, "下一步")
    wait_page(pid, "location", 30)
    click(pid, "浏览...")
    time.sleep(2.0)
    browsed = {"dialog": None, "picked": False}
    end = time.time() + 20
    while time.time() < end:
        dlgs = [w for w in windows(pid) if w["class"] == "#32770" and w["title"] and "Mosaic" not in w["title"]]
        if dlgs:
            d = dlgs[0]
            kids = W.children(d["hwnd"])
            browsed["dialog"] = {"title": d["title"], "buttons": [k["text"] for k in kids if k["class"] == "Button"],
                                 "has_edit": any(k["class"] == "Edit" for k in kids)}
            try:
                W.screenshot(d["hwnd"], f"{SHOTS}\\browse-dialog.png")
            except Exception:
                pass
            edits = [k for k in kids if k["class"] == "Edit"]
            if edits:
                h = edits[0]["hwnd"]
                u32.SendMessageW(h, 0x0007, 0, 0)
                u32.SendMessageW(h, 0x00B1, 0, -1)
                for ch in TARGET_PARENT:
                    u32.SendMessageW(h, 0x0102, ord(ch), 0)
                for label in ("确定", "选择文件夹", "OK"):
                    if W.click(d["hwnd"], label):
                        browsed["picked"] = True
                        break
                time.sleep(1.0)
            if not browsed["picked"]:
                # 选择框无法自动操作时，取消并从复现结论中如实标注
                for label in ("取消", "Cancel"):
                    if W.click(d["hwnd"], label):
                        break
            break
        time.sleep(0.5)
    result["browse"] = browsed
    observe_errors(pid, "d-browse", result["errors"])
    result["browse_statics"] = (snapshot(pid) or {}).get("statics")
    shot(pid, "d-browse-after")
    click(pid, "下一步")
    time.sleep(1.0)
    result["browse_page"] = page(pid)
    result["browse_statics_after_next"] = (snapshot(pid) or {}).get("statics")
    observe_errors(pid, "d-browse-next", result["errors"])
    shot(pid, "d-browse-next")
    if result["browse_page"] == "confirm":
        click(pid, "安装")
        result["browse_progress"] = wait_page(pid, "progress", 30)
        result["browse_done"] = wait_page(pid, "done", 600)
        observe_errors(pid, "d-browse-done", result["errors"])
        click(pid, "完成")
    else:
        click(pid, "取消")
    result["d_browse_exit"] = W.wait_pid(pid, 300)
    result["d_browse_log"] = log_b
    result["d_browse_installed"] = os.path.isdir(TARGET_PARENT + r"\Mosaic")

    import json
    with open(os.path.join(QA, "cross-drive-repro.json"), "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
    log(json.dumps({k: v for k, v in result.items() if k not in ("errors",)}, ensure_ascii=False, indent=1)[:3000])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
