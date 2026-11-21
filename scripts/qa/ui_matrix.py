# -*- coding: utf-8 -*-
"""Mosaic 安装包完整界面验收矩阵（隔离账户 MosaicQa0176）。

场景：
  S1  0.17.5 /qf 首装：复现空白进度页卡死（模态阻塞，ExecuteAction 未执行）
  S2  0.17.6 /qf 首装：进度页实时文本与进度条、完成页、文件与登记
  S3  0.17.6 /qf 健康维护：自动扫描、「安装完整，无需修复」、点击完成无写入
  S4  0.17.6 /qf 损坏维护：删除+篡改文件后自动发现、修复、复检恢复哈希
  S5  0.17.5 静默安装 → 0.17.6 /qf 升级：原位升级、数据保留、单一登记
  S6a 0.17.6 取消→否：返回进度页并完成安装
  S6b 0.17.6 取消→是：退出并回滚，无安装目录与登记
  S7  0.17.6 /qn 静默安装、健康维护、卸载：无窗口、退出码与文件状态

用法：python -I scripts/qa/ui_matrix.py [S1 S2 ...]
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import qa_win as W  # noqa: E402

CRED = r"C:\Users\ASUS\AppData\Local\Temp\mosaic-qa-cred.txt"
PROFILE = r"C:\Users\MosaicQa0176"
QA = r"C:\Users\Public\MosaicQA0176"
LOGS = QA + r"\logs"
SHOTS = QA + r"\shots"
OLD = r"D:\Code\Mosaic\artifacts\installer\0.17.5\Mosaic-0.17.5-win-x64.msi"
NEW = r"D:\Code\Mosaic\artifacts\installer\Mosaic-0.17.6-win-x64.msi"
RESULTS = QA + r"\results.json"

MSIEXEC = r"C:\Windows\System32\msiexec.exe"

PAGES = {
    "fresh": "安装 Mosaic",
    "location": "选择安装位置",
    "confirm": "确认安装",
    "progress": "正在处理，请稍候...",
    "done": "操作已完成",
    "maintenance": "安装完整，无需修复",
    "upgrade": "升级 Mosaic",
    "cancel": "确定要取消 Mosaic 安装吗？",
    "userexit": "安装已取消",
}

SEVERE = ("error", "失败", "未完成")


# --------------------------------------------------------------------------- 基础工具

def log(msg: str) -> None:
    print(msg, flush=True)


def log_path(name: str) -> str:
    return LOGS + "\\" + name + ".log"


def sha256(path: str) -> str | None:
    p = Path(path)
    if not p.is_file():
        return None
    h = hashlib.sha256()
    with p.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def snapshot(pid: int) -> dict | None:
    ws = W.windows(pid)
    if not ws:
        return None
    # CancelDlg(260x100) 等小弹窗优先：它们盖在主窗口上且承载当前决策。
    # 小弹窗优先（CancelDlg 260x100，DPI 缩放后约 363x140）：它们盖在主窗口上且承载
    # 当前决策。阈值须容纳 DPI 缩放，又小于主窗口 370x270 的缩放尺寸（约 517x401）。
    small = [w for w in ws if w.get("size") and w["size"][0] < 450]
    w = (small or ws)[0]
    w = max(small or ws, key=lambda x: (len(x["title"]), x["hwnd"]))
    kids = W.children(w["hwnd"])
    return {
        "hwnd": w["hwnd"],
        "title": w["title"],
        "class": w["class"],
        "size": w.get("size"),
        "buttons": [k["text"] for k in kids if k["class"] == "Button" and k["text"]],
        "statics": [k["text"] for k in kids if k["class"] == "Static" and k["text"]],
        "progress": W.progress_pos(w["hwnd"]),
        "t": round(time.time(), 3),
    }


def page_of(snap: dict | None) -> str | None:
    if not snap:
        return None
    for key, text in PAGES.items():
        if any(text in s for s in snap["statics"]) or text in snap["title"]:
            return key
    return None


def wait_page(pid: int, page: str, timeout: float = 90) -> dict | None:
    end = time.time() + timeout
    while time.time() < end:
        s = snapshot(pid)
        if s and page_of(s) == page:
            return s
        if W.process_gone(pid):
            return None
        time.sleep(0.25)
    return None


def click(pid: int, label: str, timeout: float = 30) -> dict | None:
    """点击当前窗口中文本为 label 的按钮，返回点击后的快照。"""
    end = time.time() + timeout
    while time.time() < end:
        s = snapshot(pid)
        if s and label in s["buttons"] and W.click(s["hwnd"], label):
            return s
        if W.process_gone(pid):
            return None
        time.sleep(0.25)
    return None


def click_real(pid: int, label: str, timeout: float = 30) -> dict | None:
    """真实鼠标点击（SetCursorPos + mouse_event）：MSI 的 SpawnDialog 只对交互式
    点击生效，SendMessage(BM_CLICK) 不触发取消确认弹窗。"""
    import ctypes
    u32 = ctypes.WinDLL("user32")
    end = time.time() + timeout
    while time.time() < end:
        s = snapshot(pid)
        if s and label in s["buttons"]:
            h = W.find_child(s["hwnd"], label, "Button")
            if h:
                r = ctypes.wintypes.RECT()
                u32.GetWindowRect(h, ctypes.byref(r))
                u32.SetCursorPos((r.left + r.right) // 2, (r.top + r.bottom) // 2)
                time.sleep(0.05)
                u32.mouse_event(0x0002, 0, 0, 0, 0)
                time.sleep(0.05)
                u32.mouse_event(0x0004, 0, 0, 0, 0)
                return s
        if W.process_gone(pid):
            return None
        time.sleep(0.2)
    return None


def wait_cancel_dialog(pid: int, timeout: float = 15) -> dict | None:
    """取消确认弹窗存在时间可能不足 0.5s，用 0.05s 间隔高频轮询。"""
    end = time.time() + timeout
    while time.time() < end:
        s = snapshot(pid)
        if s and "确定要取消" in "".join(s["statics"]):
            return s
        if W.process_gone(pid):
            return None
        time.sleep(0.05)
    return None


def finish(pid: int, label: str = "完成", timeout: float = 300) -> int | None:
    s = wait_page(pid, "done", timeout)
    if s:
        shot(pid, "done")
    click(pid, label)
    return W.wait_pid(pid, 300)


def shot(pid: int, tag: str) -> str | None:
    s = snapshot(pid)
    if not s:
        return None
    path = f"{SHOTS}\\{tag}.png"
    try:
        W.screenshot(s["hwnd"], path)
        return path
    except Exception as exc:  # 截图失败不阻断场景
        log(f"  [warn] 截图失败 {tag}: {exc}")
        return None


def start(mode: str, msi: str, name: str, extra: str = "") -> tuple[int, str]:
    logf = log_path(name)
    Path(logf).unlink(missing_ok=True)
    args = f'{mode} "{msi}" /L*V! "{logf}"'
    if extra:
        args += " " + extra
    pid, _ = W.run_as(CRED, PROFILE, MSIEXEC, args)
    return pid, logf


def start_watch(mode: str, msi: str, name: str, extra: str = "") -> tuple[int, str, dict]:
    """静默场景：启动后轮询窗口，记录整个过程中出现过的任何窗口。"""
    pid, logf = start(mode, msi, name, extra)
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


def markers(logf: str) -> dict:
    text = W.read_log_text(logf)
    lines = text.splitlines()
    flags = {
        # UI 序列顶层动作也叫 INSTALL；静默会话不打印 ExecuteAction，以
        # InstallInitialize（进入事务）为准。
        "ExecuteAction": "Doing action: ExecuteAction" in text or "Doing action: InstallInitialize" in text,
        "InstallInitialize": "Doing action: InstallInitialize" in text,
        "InstallFiles": "Doing action: InstallFiles" in text,
        "InstallFinalize": "Doing action: InstallFinalize" in text,
        "Rollback": "Rolling back" in text,
        "MainEngineReturn": [l.strip() for l in lines if "MainEngineThread is returning" in l],
        "size": len(text),
    }
    flags["LogonUser"] = [l.strip() for l in lines if "Property(C): LogonUser =" in l][:1]
    flags["UserSID"] = [l.strip() for l in lines if "Property(C): UserSID =" in l][:1]
    return flags


def probe(cmd: str, name: str) -> str:
    p = LOGS + "\\" + name + ".txt"
    W.run_probe(CRED, PROFILE, cmd, p)
    return W.read_log_text(p)


def arp_entries() -> list[dict]:
    """HKLM 卸载表里所有 Mosaic 条目；按 InstallLocation 区分隔离账户与真实账户。"""
    out = probe(r'reg query "HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall" /s', "arp-all")
    entries = []
    for block in re.split(r"\r?\n(?=HKEY_)", out):
        if "Mosaic" not in block or "InstallLocation" not in block:
            continue
        loc = re.search(r"InstallLocation\s+REG_SZ\s+([^\r\n]+)", block)
        ver = re.search(r"DisplayVersion\s+REG_SZ\s+([^\r\n]+)", block)
        entries.append({
            "key": block.splitlines()[0].strip(),
            "location": loc.group(1).strip() if loc else "",
            "version": ver.group(1).strip() if ver else None,
            "qa": bool(loc) and QA.lower() in loc.group(1).lower(),
        })
    return entries


def qa_arp() -> list[dict]:
    return [e for e in arp_entries() if e["qa"]]


def qa_arp_for(folder: str) -> list[dict]:
    target = folder.rstrip("\\").lower()
    return [e for e in arp_entries() if e["location"].rstrip("\\").lower() == target]


def qa_exist(rel_path: str, name: str) -> bool:
    out = probe(f'if exist "%{rel_path}" (echo YES) else (echo NO)', name)
    return "YES" in out


def installed_location() -> str:
    out = probe('reg query "HKCU\\Software\\Mosaic\\Installer" /v InstallLocation', "install-location")
    m = re.search(r"InstallLocation\s+REG_SZ\s+(.+)", out)
    return m.group(1).strip() if m else ""


def hash_map(folder: str) -> dict:
    out = {}
    for name in ("Mosaic.Desktop.exe", "Mosaic.Kernel.dll", "Mosaic.Tasks.Core.dll", "Mosaic.ReleaseSummary.dll"):
        out[name] = sha256(os.path.join(folder, name))
    return out


def save_results(results: dict) -> None:
    Path(RESULTS).write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")


def uninstall(msi: str, name: str, folder: str | None = None) -> dict:
    _, logf, watch = start_watch("/x", msi, name, '/qn')
    return {"exit": watch["exit"], "folder_left": os.path.isdir(folder) if folder else None,
            "markers": markers(logf)}


def reset_qa(folders: list[str]) -> dict:
    """把隔离账户恢复到“从未安装”状态：卸载产品、清默认目录与场景目录、清隔离账户的 Mosaic 注册。

    只作用于 MosaicQa 账户与 QA 公共目录，不接触真实安装。
    """
    out = {}
    for tag, msi in (("new", NEW), ("old", OLD)):
        _, _, watch = start_watch("/x", msi, f"reset-uninstall-{tag}", "/qn")
        out[f"uninstall_{tag}"] = watch["exit"]
    # 重建 MSI 会换产品代码：按登记的压缩 GUID 反解出 ProductCode 逐一卸载，
    # 否则残留的同版本不同产品代码安装会让 LaunchConditions 拒绝新包。
    listed = probe(r'reg query "HKCU\Software\Microsoft\Installer\Products"', "reset-products")
    prefix = "HKEY_CURRENT_USER\\Software\\Microsoft\\Installer\\Products\\"
    codes = []
    for line in listed.splitlines():
        line = line.strip()
        if not line.startswith(prefix):
            continue
        packed = line.rsplit("\\", 1)[-1]
        if len(packed) != 32:
            continue
        try:
            code = "{%s-%s-%s-%s-%s}" % (
                packed[6:8] + packed[4:6] + packed[2:4] + packed[0:2],
                packed[10:12] + packed[8:10],
                packed[14:16] + packed[12:14],
                packed[16:18] + packed[18:20],
                packed[20:22] + packed[22:24] + packed[24:26] + packed[26:28] + packed[28:30] + packed[30:32],
            )
        except Exception:
            continue
        codes.append(code)
    uninstalled = {}
    for code in codes:
        _, _, watch = start_watch("/x", code, "reset-uninstall-code", "/qn")
        uninstalled[code] = watch["exit"]
    out["uninstalled_codes"] = uninstalled
    base = PROFILE + r"\AppData\Local\Programs\Mosaic"
    paths = [base] + list(folders)
    cmd = " & ".join([f'rmdir /s /q "{p}" 2>nul' for p in paths])
    cmd += ' & reg delete "HKCU\\Software\\Mosaic" /f 2>nul & reg delete "HKCU\\Software\\Microsoft\\Installer\\Products\\18EA3A05394CBF0418E2F699F1B26A3F" /f 2>nul & echo RESET-DONE'
    out["probe"] = probe(cmd, "reset")
    out["leftovers"] = [p for p in paths if os.path.isdir(p)]
    out["arp_qa"] = qa_arp()
    return out


# --------------------------------------------------------------------------- 场景

def s1_old_hang() -> dict:
    """0.17.5 首装：复现空白进度页（模态对话框阻塞 UI 序列）。"""
    parent = QA + r"\S1 旧版 空白进度"
    pid, logf = start("/i", OLD, "s1-old-qf", f'/qf MOSAICINSTALLPATH="{parent}"')
    r = {"scenario": "S1", "msi": OLD, "mode": "/qf", "pid": pid, "samples": []}
    r["fresh_page"] = bool(wait_page(pid, "fresh", 60))
    click(pid, "下一步")
    r["location_page"] = bool(wait_page(pid, "location", 30))
    click(pid, "下一步")
    r["confirm_page"] = bool(wait_page(pid, "confirm", 30))
    shot(pid, "s1-confirm")
    click(pid, "安装")
    p = wait_page(pid, "progress", 30)
    r["progress_page"] = bool(p)
    t0 = time.time()
    while time.time() - t0 < 14:
        s = snapshot(pid)
        if s:
            r["samples"].append({"t": round(time.time() - t0, 1), "statics": s["statics"],
                                 "progress": s["progress"], "buttons": s["buttons"]})
        time.sleep(1.0)
    r["alive_after_14s"] = not W.process_gone(pid)
    r["screenshot_hang"] = shot(pid, "s1-hang-14s")
    r["log_at_hang"] = markers(logf)
    r["action_texts"] = sorted({x for smp in r["samples"] for x in smp["statics"]})
    r["progress_values"] = sorted({smp["progress"] for smp in r["samples"] if smp["progress"] is not None})
    r["cancel_clicked"] = bool(click(pid, "取消", 10))
    r["exit"] = W.wait_pid(pid, 180)
    r["log_after"] = markers(logf)
    r["install_folder_exists"] = os.path.isdir(parent + r"\Mosaic")
    r["arp_entries"] = qa_arp_for(parent + r"\Mosaic")
    return r


def s2_fresh_new() -> dict:
    parent = QA + r"\S2 安装 位置"
    pid, logf = start("/i", NEW, "s2-new-qf", f'/qf MOSAICINSTALLPATH="{parent}"')
    r = {"scenario": "S2", "msi": NEW, "mode": "/qf", "pid": pid, "samples": []}
    r["fresh_page"] = bool(wait_page(pid, "fresh", 60))
    shot(pid, "s2-fresh")
    click(pid, "下一步")
    r["location_page"] = bool(wait_page(pid, "location", 30))
    r["location_statics"] = snapshot(pid)["statics"] if r["location_page"] else None
    click(pid, "下一步")
    r["confirm_page"] = bool(wait_page(pid, "confirm", 30))
    r["confirm_statics"] = snapshot(pid)["statics"] if r["confirm_page"] else None
    shot(pid, "s2-confirm")
    click(pid, "安装")
    r["progress_page"] = bool(wait_page(pid, "progress", 30))
    done = False
    t0 = time.time()
    shot_at = False
    while time.time() - t0 < 600:
        s = snapshot(pid)
        if s:
            r["samples"].append({"t": round(time.time() - t0, 1), "statics": s["statics"],
                                 "progress": s["progress"], "buttons": s["buttons"],
                                 "page": page_of(s)})
            if not shot_at and s["progress"] not in (None, 0):
                shot(pid, "s2-progress")
                shot_at = True
            if page_of(s) == "done":
                done = True
                break
        if W.process_gone(pid):
            break
        time.sleep(0.6)
    r["done_page"] = done
    r["done_statics"] = snapshot(pid)["statics"] if done else None
    r["screenshot_done"] = shot(pid, "s2-done")
    r["action_texts"] = sorted({x for smp in r["samples"] for x in smp["statics"]})
    r["progress_values"] = sorted({smp["progress"] for smp in r["samples"] if smp["progress"] is not None})
    r["progress_distinct"] = len({smp["progress"] for smp in r["samples"] if smp["progress"]})
    if done:
        click(pid, "完成")
    r["exit"] = W.wait_pid(pid, 300)
    r["log"] = markers(logf)
    folder = parent + r"\Mosaic"
    r["install_folder"] = folder
    r["folder_exists"] = os.path.isdir(folder)
    r["exe_exists"] = os.path.isfile(folder + r"\Mosaic.Desktop.exe")
    r["file_count"] = len(list(Path(folder).rglob("*"))) if r["folder_exists"] else 0
    r["registered_location"] = installed_location()
    r["arp_entries"] = qa_arp_for(folder)
    r["single_registration"] = len(r["arp_entries"]) == 1
    r["arp_entry_version"] = r["arp_entries"][0]["version"] if r["arp_entries"] else None
    r["start_menu"] = qa_exist(r"APPDATA%\Microsoft\Windows\Start Menu\Programs\Mosaic\Mosaic.lnk", "s2-startmenu")
    r["desktop_shortcut"] = qa_exist(r"USERPROFILE%\Desktop\Mosaic.lnk", "s2-desktop")
    r["hashes"] = hash_map(folder) if r["folder_exists"] else {}
    tlist = probe('tasklist /fi "imagename eq Mosaic.Desktop.exe" /v', "s2-tasklist")
    r["tasklist"] = tlist
    # 仅当进程属于隔离账户时才算“安装后自启”；真实账户自己的 Mosaic 不计入。
    r["app_running"] = "Mosaic.Desktop" in tlist and "MosaicQa0176" in tlist
    r["install_success"] = bool(r["folder_exists"] and r["exe_exists"] and r["single_registration"]
                                and r["log"].get("ExecuteAction") and r["log"].get("InstallFiles"))
    return r


def s3_healthy_maintenance(hashes: dict | None) -> dict:
    parent = QA + r"\S2 安装 位置"
    folder = parent + r"\Mosaic"
    before = hash_map(folder)
    mtimes = {k: os.path.getmtime(os.path.join(folder, k)) for k in before if before[k]}
    pid, logf = start("/i", NEW, "s3-maintenance-healthy", "/qf")
    r = {"scenario": "S3", "pid": pid, "before": before, "mtimes": mtimes}
    s = wait_page(pid, "maintenance", 90)
    r["maintenance_page"] = bool(s)
    if s:
        r["statics"] = s["statics"]
        r["buttons"] = s["buttons"]
        shot(pid, "s3-healthy")
        finish_label = "完成" if "完成" in s["buttons"] else (s["buttons"][0] if s["buttons"] else "完成")
        r["clicked"] = finish_label
        click(pid, finish_label)
    r["exit"] = W.wait_pid(pid, 300)
    r["log"] = markers(logf)
    r["after"] = hash_map(folder)
    r["unchanged"] = r["after"] == before
    r["mtimes_unchanged"] = all(os.path.getmtime(os.path.join(folder, k)) == mtimes[k] for k in mtimes)
    r["expected_hash_match"] = (not hashes) or all(before.get(k) == v for k, v in hashes.items())
    return r


def s4_damaged_repair() -> dict:
    folder = QA + r"\S2 安装 位置\Mosaic"
    before = hash_map(folder)
    victim_missing = os.path.join(folder, "Mosaic.ReleaseSummary.dll")
    victim_damaged = os.path.join(folder, "Mosaic.Tasks.Core.dll")
    Path(victim_missing).unlink()
    with open(victim_damaged, "ab") as f:
        f.write(b"\nQA-CORRUPTION-0.17.6\n")
    r = {"scenario": "S4", "before": before,
         "deleted": os.path.basename(victim_missing),
         "corrupted": os.path.basename(victim_damaged),
         "damaged_hash": sha256(victim_damaged)}
    pid, logf = start("/i", NEW, "s4-maintenance-damaged", "/qf")
    s = wait_page(pid, "maintenance", 120)
    r["maintenance_page"] = bool(s)
    if s:
        r["statics"] = s["statics"]
        r["buttons"] = s["buttons"]
        shot(pid, "s4-damaged")
        label = "修复" if "修复" in s["buttons"] else (s["buttons"][0] if s["buttons"] else "修复")
        r["clicked"] = label
        click(pid, label)
    r["progress_page"] = bool(wait_page(pid, "progress", 60))
    t0 = time.time()
    r["progress_samples"] = []
    while time.time() - t0 < 600:
        smp = snapshot(pid)
        if smp:
            r["progress_samples"].append({"t": round(time.time() - t0, 1), "statics": smp["statics"],
                                          "progress": smp["progress"], "page": page_of(smp)})
            if page_of(smp) == "done":
                break
        if W.process_gone(pid):
            break
        time.sleep(0.8)
    r["done_statics"] = snapshot(pid)["statics"] if snapshot(pid) else None
    shot(pid, "s4-done")
    click(pid, "完成")
    r["exit"] = W.wait_pid(pid, 300)
    r["log"] = markers(logf)
    r["after"] = hash_map(folder)
    r["restored"] = {k: (r["after"].get(k) == before.get(k)) for k in before}
    r["missing_file_back"] = os.path.isfile(victim_missing)
    r["corrupt_file_hash_back"] = r["after"].get("Mosaic.Tasks.Core.dll") == before.get("Mosaic.Tasks.Core.dll")
    return r


def s5_upgrade() -> dict:
    # 前置：QA 账户须处于“装着 0.17.5”的状态。若残留任何更高版本安装（降级保护会
    # 拒绝 0.17.5），先按登记的产品代码全部卸载并清理目录。
    r = {"scenario": "S5"}
    reset = reset_qa([QA + r"\S5 升级 安装"])
    r["pre_reset"] = reset
    folder = QA + r"\S5 升级 安装"
    _, logf0, watch0 = start_watch("/i", OLD, "s5-old-silent", f'/qn INSTALLFOLDER="{folder}"')
    r["old_exit"] = watch0["exit"]
    r["old_log"] = markers(logf0)
    r["old_folder"] = os.path.isdir(folder)
    # 哨兵文件在隔离账户配置文件里，跨账户读写须走 probe（QA 侧写入、QA 侧读出）。
    probe(f'mkdir "{PROFILE}\\AppData\\Local\\Mosaic" 2>nul & echo qa-sentinel-0.17.5> "{PROFILE}\\AppData\\Local\\Mosaic\\qa-sentinel.txt"', "s5-sentinel")
    sentinel = PROFILE + r"\AppData\Local\Mosaic\qa-sentinel.txt"
    r["sentinel_before"] = probe(f'type "{sentinel}"', "s5-sentinel-before").strip()
    old_hash = sha256(folder + r"\Mosaic.Desktop.exe")
    pid, logf = start("/i", NEW, "s5-upgrade-qf", "/qf")
    s = wait_page(pid, "upgrade", 90)
    r["upgrade_page"] = bool(s)
    if s:
        r["statics"] = s["statics"]
        r["buttons"] = s["buttons"]
        shot(pid, "s5-upgrade")
        click(pid, "升级")
    r["progress_page"] = bool(wait_page(pid, "progress", 60))
    r["done_page"] = bool(wait_page(pid, "done", 600))
    shot(pid, "s5-done")
    click(pid, "完成")
    r["exit"] = W.wait_pid(pid, 300)
    r["log"] = markers(logf)
    r["location_after"] = installed_location()
    r["location_preserved"] = r["location_after"].rstrip("\\").lower() == folder.rstrip("\\").lower()
    r["sentinel_after"] = probe(f'type "{sentinel}"', "s5-sentinel-after").strip()
    r["new_hash"] = sha256(folder + r"\Mosaic.Desktop.exe")
    r["binary_replaced"] = r["new_hash"] != old_hash
    r["arp_entries"] = qa_arp_for(folder)
    r["single_registration"] = len(r["arp_entries"]) == 1
    r["arp_version_0176"] = bool(r["arp_entries"]) and (r["arp_entries"][0]["version"] or "").startswith("0.17.6")
    return r


def s6a_cancel_no() -> dict:
    reset_qa([QA + r"\S6a 取消 继续\Mosaic", QA + r"\S6b 取消 回滚\Mosaic"])
    parent = QA + r"\S6a 取消 继续"
    pid, logf = start("/i", NEW, "s6a-cancel-no", f'/qf MOSAICINSTALLPATH="{parent}"')
    r = {"scenario": "S6a", "pid": pid}
    wait_page(pid, "fresh", 60)
    click(pid, "下一步")
    wait_page(pid, "location", 30)
    click(pid, "下一步")
    wait_page(pid, "confirm", 30)
    r["confirm_page"] = bool(wait_page(pid, "confirm", 20))
    click(pid, "安装")
    r["progress_page"] = bool(wait_page(pid, "progress", 30))
    time.sleep(1)
    click_real(pid, "取消")
    dlg = wait_cancel_dialog(pid)
    r["cancel_shown"] = bool(dlg)
    if dlg:
        r["cancel_statics"] = dlg["statics"]
        shot(pid, "s6a-cancel-prompt")
        click_real(pid, "否")
    r["back_to_progress"] = bool(wait_page(pid, "progress", 30))
    r["done_page"] = bool(wait_page(pid, "done", 600))
    shot(pid, "s6a-done")
    click(pid, "完成")
    r["exit"] = W.wait_pid(pid, 300)
    r["log"] = markers(logf)
    r["folder_exists"] = os.path.isdir(parent + r"\Mosaic")
    r["exe_exists"] = os.path.isfile(parent + r"\Mosaic\Mosaic.Desktop.exe")
    r["arp_entries"] = qa_arp_for(parent + r"\Mosaic")
    return r


def s6b_cancel_yes() -> dict:
    reset_qa([QA + r"\S6b 取消 回滚\Mosaic"])
    parent = QA + r"\S6b 取消 回滚"
    pid, logf = start("/i", NEW, "s6b-cancel-yes", f'/qf MOSAICINSTALLPATH="{parent}"')
    r = {"scenario": "S6b", "pid": pid}
    wait_page(pid, "fresh", 60)
    click(pid, "下一步")
    wait_page(pid, "location", 30)
    click(pid, "下一步")
    wait_page(pid, "confirm", 30)
    click(pid, "安装")
    r["progress_page"] = bool(wait_page(pid, "progress", 30))
    time.sleep(1)
    click_real(pid, "取消")
    dlg = wait_cancel_dialog(pid)
    r["cancel_shown"] = bool(dlg)
    if dlg:
        shot(pid, "s6b-cancel-prompt")
        click_real(pid, "是")
    ue = wait_page(pid, "userexit", 60)
    r["userexit_page"] = bool(ue)
    if ue:
        r["userexit_statics"] = ue["statics"]
        shot(pid, "s6b-userexit")
        click(pid, "关闭")
    r["exit"] = W.wait_pid(pid, 300)
    r["log"] = markers(logf)
    r["folder_exists"] = os.path.isdir(parent + r"\Mosaic")
    r["arp_entries"] = qa_arp_for(parent + r"\Mosaic")
    return r


def s7_silent() -> dict:
    folder = QA + r"\S7 静默 安装"
    _, logf1, w1 = start_watch("/i", NEW, "s7-silent-install", f'/qn INSTALLFOLDER="{folder}"')
    hashes = hash_map(folder)
    r = {"scenario": "S7", "install_exit": w1["exit"], "install_windows": w1["windows"],
         "install_log": markers(logf1), "folder_exists": os.path.isdir(folder), "hashes": hashes}
    _, logf2, w2 = start_watch("/i", NEW, "s7-silent-maintenance", "/qn")
    r["maintenance_exit"] = w2["exit"]
    r["maintenance_windows"] = w2["windows"]
    r["maintenance_log"] = markers(logf2)
    r["maintenance_unchanged"] = hash_map(folder) == hashes
    _, logf3, w3 = start_watch("/x", NEW, "s7-silent-uninstall", "/qn")
    r["uninstall_exit"] = w3["exit"]
    r["uninstall_windows"] = w3["windows"]
    r["uninstall_folder_left"] = os.path.isdir(folder)
    r["uninstall_log"] = markers(logf3)
    return r


# --------------------------------------------------------------------------- 主流程

def main() -> int:
    os.makedirs(LOGS, exist_ok=True)
    os.makedirs(SHOTS, exist_ok=True)
    results = {}
    if Path(RESULTS).exists():
        try:
            results = json.loads(Path(RESULTS).read_text(encoding="utf-8"))
        except json.JSONDecodeError:
            results = {}
    env = {
        "whoami": probe("whoami", "whoami"),
        "localappdata": probe("echo %LOCALAPPDATA%", "localappdata"),
        "date": time.strftime("%Y-%m-%d %H:%M:%S"),
        "old_msi": {"path": OLD, "size": os.path.getsize(OLD), "sha256": sha256(OLD)},
        "new_msi": {"path": NEW, "size": os.path.getsize(NEW), "sha256": sha256(NEW)},
    }
    results.setdefault("environment", env)

    if "--reset" in sys.argv:
        log("=== RESET ===")
        results["reset"] = reset_qa([
            QA + r"\S1 旧版 空白进度\Mosaic", QA + r"\S2 安装 位置\Mosaic", QA + r"\S5 升级 安装",
            QA + r"\S6a 取消 继续\Mosaic", QA + r"\S6b 取消 回滚\Mosaic", QA + r"\S7 静默 安装",
            QA + r"\Install 诊断", QA + r"\T1",
        ])
        log(json.dumps(results["reset"], ensure_ascii=False))
        save_results(results)

    wanted = [a.upper() for a in sys.argv[1:] if not a.startswith("--")] or \
        ["S1", "S2", "S3", "S4", "S5", "S6A", "S6B", "S7"]
    plan = [("S1", s1_old_hang), ("S2", s2_fresh_new), ("S3", None), ("S4", s4_damaged_repair),
            ("S5", s5_upgrade), ("S6A", s6a_cancel_no), ("S6B", s6b_cancel_yes), ("S7", s7_silent)]

    for key, fn in plan:
        if key not in wanted:
            continue
        log(f"=== {key} ===")
        try:
            if key == "S3":
                r = s3_healthy_maintenance(results.get("S2", {}).get("hashes"))
            else:
                r = fn()
        except Exception as exc:  # 记录失败，继续其余场景
            import traceback
            r = {"scenario": key, "error": f"{type(exc).__name__}: {exc}", "traceback": traceback.format_exc()}
            log(f"  [error] {r['error']}")
        keyname = key if key != "S6A" else "S6a"
        keyname = keyname if key != "S6B" else "S6b"
        results[keyname] = r
        save_results(results)
        brief = {k: v for k, v in r.items() if k not in ("samples", "progress_samples", "before", "after")}
        log(json.dumps(brief, ensure_ascii=False, indent=1)[:4000])
    save_results(results)
    log("results -> " + RESULTS)
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(errors="backslashreplace")
    raise SystemExit(main())
