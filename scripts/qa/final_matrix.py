# -*- coding: utf-8 -*-
"""跨盘最终矩阵：D 盘健康维护 + 损坏维护（不自动修复）+ 静默卸载。"""
import hashlib
import json
import os
import sys
import time

sys.stdout.reconfigure(encoding="utf-8", errors="backslashreplace")
sys.path.insert(0, r"D:\Code\Mosaic\scripts\qa")
import cross_drive_repro as C  # noqa: E402
import qa_win as W  # noqa: E402

FOLDER = r"D:\MosaicQA\跨盘 安装\Mosaic"


def sha(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def main():
    result = {}
    victim = os.path.join(FOLDER, "Mosaic.ReleaseSummary.dll")
    before_sha = sha(victim)

    # 1. 健康维护
    pid, logf = C.start("/i", "final3-healthy", "/qf")
    result["healthy_page"] = C.wait_page(pid, "maintenance", 90)
    result["healthy_statics"] = (C.snapshot(pid) or {}).get("statics")
    result["healthy_buttons"] = (C.snapshot(pid) or {}).get("buttons")
    C.shot(pid, "final3-healthy")
    if result["healthy_page"]:
        C.click(pid, "完成")
    result["healthy_exit"] = W.wait_pid(pid, 300)
    result["healthy_return"] = [l.strip()[-28:] for l in W.read_log_text(logf).splitlines() if "MainEngineThread" in l]
    result["healthy_unchanged"] = sha(victim) == before_sha

    # 2. 损坏维护（删文件+删快捷方式）——应显示提示，不自动修复、不弹引擎错误框
    os.remove(victim)
    p = C.LOGS + r"\del-shortcut-final3.txt"
    W.run_probe(C.CRED, C.PROFILE, r'del "%USERPROFILE%\Desktop\Mosaic.lnk" & echo GONE', p)
    pid, logf = C.start("/i", "final3-damaged", "/qf")
    result["damaged_page"] = C.wait_page(pid, "maintenance", 90)
    result["damaged_statics"] = (C.snapshot(pid) or {}).get("statics")
    result["damaged_buttons"] = (C.snapshot(pid) or {}).get("buttons")
    C.shot(pid, "final3-damaged")
    result["damaged_errors"] = C.observe_errors(pid, "final3-damaged", [])
    if result["damaged_page"]:
        btn = result["damaged_buttons"]
        result["clicked"] = btn[0] if btn else None
        C.click(pid, btn[0] if btn else "完成")
    result["damaged_exit"] = W.wait_pid(pid, 300)
    result["damaged_return"] = [l.strip()[-28:] for l in W.read_log_text(logf).splitlines() if "MainEngineThread" in l]
    result["file_still_missing"] = not os.path.exists(victim)
    result["sentinel"] = "cross-drive-sentinel" in C.probe(
        r'type "%LOCALAPPDATA%\Mosaic\qa-sentinel.txt"', "final3-sentinel")

    # 3. 静默卸载
    _, _, w = C.start_watch("/x", C.MSI, "final3-uninstall", "/qn")
    result["uninstall_windows"] = w["windows"]
    result["uninstall_return"] = [l.strip()[-28:] for l in W.read_log_text(
        r"C:\Users\Public\MosaicQA\logs\final3-uninstall.log").splitlines() if "MainEngineThread" in l]
    result["folder_after"] = os.path.isdir(FOLDER)
    result["products_after"] = C.probe(
        r'reg query "HKCU\Software\Microsoft\Installer\Products"', "final3-products")

    with open(r"C:\Users\Public\MosaicQA\final3-result.json", "w", encoding="utf-8") as f:
        json.dump(result, f, ensure_ascii=False, indent=1)
    print(json.dumps(result, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
