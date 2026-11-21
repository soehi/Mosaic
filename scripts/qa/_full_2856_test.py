# -*- coding: utf-8 -*-
"""End-to-end UI drive for the 2856 fix:
FreshStart -> Location -> Browse -> type E:\app -> OK -> Next -> expect ConfirmDlg.
Reports each step and the final window state. Leaves the installer on ConfirmDlg.
"""
import ctypes
from ctypes import wintypes
import subprocess
import time
import sys

user32 = ctypes.windll.user32
ENUM = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
BM_CLICK = 0x00F5
WM_SETTEXT = 0x000C

LOG = r'C:\Users\Public\MosaicQA2856\logs\2856-final.log'
MSI = r'D:\Code\Mosaic\artifacts\installer\Mosaic-0.17.6-win-x64.msi'

def text_of(hwnd):
    buf = ctypes.create_unicode_buffer(1024)
    user32.GetWindowTextW(hwnd, buf, 1024)
    return buf.value

def cls_of(hwnd):
    buf = ctypes.create_unicode_buffer(64)
    user32.GetClassNameW(hwnd, buf, 64)
    return buf.value

def children(hwnd):
    out = []
    def cb(h, lp):
        out.append(h)
        return True
    user32.EnumChildWindows(hwnd, ENUM(cb), 0)
    return out

def find_msi_dialog(marker):
    """Return (hwnd, childtexts) of the MsiDialogCloseClass window containing marker text."""
    found = []
    def cb(hwnd, lp):
        if user32.IsWindowVisible(hwnd) and cls_of(hwnd) == 'MsiDialogCloseClass':
            found.append(hwnd)
        return True
    user32.EnumWindows(ENUM(cb), 0)
    for h in found:
        texts = [text_of(k) for k in children(h)]
        if any(marker in t for t in texts):
            return h, texts
    return None, []

def click_button(dlg, label):
    for k in children(dlg):
        if cls_of(k) == 'Button' and text_of(k) == label:
            user32.SendMessageW(k, BM_CLICK, 0, 0)
            return True
    return False

def wait_dialog(marker, timeout=10):
    deadline = time.time() + timeout
    while time.time() < deadline:
        h, texts = find_msi_dialog(marker)
        if h:
            return h, texts
        time.sleep(0.4)
    return None, []

proc = subprocess.Popen(['msiexec', '/i', MSI, '/L*V', LOG])
time.sleep(7)

h, _ = wait_dialog('安装 Mosaic', timeout=25)
print('1 FreshStartDlg:', bool(h))
if not h: sys.exit(1)
click_button(h, '下一步')

h, texts = wait_dialog('选择安装位置')
print('2 LocationDlg:', bool(h))
if not h: sys.exit(1)
print('  current path shown:', [t for t in texts if 'Mosaic' in t or 'Users' in t])
click_button(h, '浏览...')
time.sleep(3)

browse = user32.FindWindowW('#32770', '选择 Mosaic 的安装位置')
print('3 browse dialog:', bool(browse))
if not browse:
    print('FAIL: browse dialog did not open')
    sys.exit(1)
edit = user32.FindWindowExW(browse, 0, 'Edit', None)
print('  edit hwnd:', edit)
if edit:
    user32.SendMessageW(edit, WM_SETTEXT, 0, 'E:\\app')
    time.sleep(0.5)
ok = user32.FindWindowExW(browse, 0, 'Button', '确定') or user32.FindWindowExW(browse, 0, 'Button', 'OK')
user32.SendMessageW(ok, BM_CLICK, 0, 0)
time.sleep(3)

h, texts = find_msi_dialog('选择安装位置')
print('4 back on LocationDlg:', bool(h))
if not h:
    print('FAIL: LocationDlg not present after browse OK')
    sys.exit(1)
print('  texts now:', [t for t in texts if t])
print('  E:\\app\\Mosaic shown:', any('E:' in t for t in texts))
click_button(h, '下一步')
time.sleep(3)

h, texts = find_msi_dialog('确认安装')
print('5 ConfirmDlg reached:', bool(h))
if h:
    print('  texts:', [t for t in texts if t])
    print('RESULT: PASS')
else:
    # check for error dialog
    err = []
    def cb2(hwnd, lp):
        if user32.IsWindowVisible(hwnd) and cls_of(hwnd) == '#32770':
            err.append((hwnd, text_of(hwnd), [text_of(k) for k in children(hwnd) if text_of(k)]))
        return True
    user32.EnumWindows(ENUM(cb2), 0)
    print('  dialogs present:', err)
    print('RESULT: FAIL')
