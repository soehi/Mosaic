# -*- coding: utf-8 -*-
"""导出 MSI 的 Dialog/Control/EventMapping/ControlEvent/InstallUISequence 为 JSON。"""
import json, subprocess, sys, tempfile
from pathlib import Path

PS = r'''
param([string]$PackagePath,[string]$Out)
$ErrorActionPreference='Stop'
$sdk=Join-Path $env:USERPROFILE '.nuget/packages/wixtoolset.sdk'
$dtf=Get-ChildItem $sdk -Directory | Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'tools/net472/WixToolset.Dtf.WindowsInstaller.dll' } | Where-Object { Test-Path $_ } | Select-Object -First 1
Add-Type -Path $dtf
$db=New-Object WixToolset.Dtf.WindowsInstaller.Database((Resolve-Path $PackagePath).Path)
function Rows($sql,$n){
  $v=$db.OpenView($sql); $v.Execute(); $r=@()
  while($true){ $rec=$v.Fetch(); if($null -eq $rec){break}; $a=@(); for($i=1;$i -le $n;$i++){ $s=''; try{$s=$rec.GetString($i)}catch{try{$s=[string]$rec.GetInteger($i)}catch{}}; $a+=$s }; $r+=,@($a) }
  $v.Close(); return $r
}
$o=[ordered]@{
  Dialog=(Rows 'SELECT * FROM Dialog' 10)
  Control=(Rows 'SELECT * FROM Control' 11)
  EventMapping=(Rows 'SELECT * FROM EventMapping' 4)
  ControlEvent=(Rows 'SELECT * FROM ControlEvent' 5)
  InstallUISequence=(Rows 'SELECT * FROM InstallUISequence' 3)
}
[IO.File]::WriteAllText($Out, ($o | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))
'''
with tempfile.TemporaryDirectory() as td:
    ps = Path(td) / "d.ps1"
    ps.write_text(PS, encoding="utf-8-sig")
    out = Path(sys.argv[2])
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(ps),
                    "-PackagePath", sys.argv[1], "-Out", str(out)], check=True)
d = json.load(open(sys.argv[2], encoding="utf-8"))
for row in d["Dialog"]:
    if isinstance(row, list) and row and "Progress" in str(row[0]) or "CancelDlg" in str(row[0]):
        print("Dialog", row)
print("EventMapping:", d["EventMapping"])
