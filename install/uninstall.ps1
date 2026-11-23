param(
    [string]$GameDir = "",
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$editorDir = Join-Path $root "编辑器"

function Say([string]$text) { if (-not $Quiet) { Write-Host $text } }
function Test-Game([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    return (Test-Path (Join-Path $dir "Nientum.exe"))
}

if (-not $Quiet) {
    Write-Host ""
    Write-Host "==== Nientum 谱面工具 (实验版) 卸载/还原程序 ====" -ForegroundColor Cyan
    Write-Host "将移除实验版快捷方式，并优先还原此前备份的稳定版 Mod（不删谱面与正式版）。"
    Write-Host ""
}

$gameProc = Get-Process -Name Nientum -ErrorAction SilentlyContinue
if ($gameProc) {
    Write-Host "游戏正在运行，请先完全退出游戏。" -ForegroundColor Yellow
    if ($Quiet) { exit 1 }
    Read-Host "退出后按回车继续" | Out-Null
}

$candidates = @()
if ($GameDir) { $candidates += $GameDir }
$settingsPath = Join-Path $editorDir "ui_settings.json"
if (Test-Path $settingsPath) {
    try {
        $s = Get-Content $settingsPath -Raw | ConvertFrom-Json
        if ($s.GamePath) { $candidates += $s.GamePath }
    } catch { }
}
$candidates += "C:\Program Files (x86)\Steam\steamapps\common\Liora"

$found = $null
foreach ($c in $candidates) { if (Test-Game $c) { $found = $c; break } }

if ($found) {
    $plugins = Join-Path $found "BepInEx\plugins"
    $target = Join-Path $plugins "NientumCustomCharts.dll"
    $stableBak = Join-Path $plugins "NientumCustomCharts.dll.stable_bak"
    if (Test-Path $stableBak) {
        Copy-Item $stableBak $target -Force
        Say "已从 .stable_bak 还原稳定版 Mod：$target"
    } elseif (Test-Path $target) {
        $backup = $target + ".bak_exp_removed_" + (Get-Date -Format "yyyyMMdd_HHmmss")
        Move-Item $target $backup -Force
        Say "已移除实验版 Mod（备份：$(Split-Path -Leaf $backup)）"
    }
}

try {
    $lnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "Nientum 谱面编辑器 (实验版).lnk"
    if (Test-Path $lnk) {
        Remove-Item $lnk -Force
        Say "已删除实验版桌面快捷方式（正式版快捷方式不受影响）。"
    }
} catch { }

Write-Host ""
Write-Host "实验版卸载/还原完成" -ForegroundColor Green
if (-not $Quiet) { Read-Host "按回车关闭" | Out-Null }
exit 0
