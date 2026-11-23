param(
    [string]$GameDir = "",
    [switch]$NoShortcut,
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$editorDir = Join-Path $root "编辑器"
$modDll = Join-Path $root "Mod\NientumCustomCharts.dll"
$bepSrc = Join-Path $root "BepInEx框架"

function Say([string]$text) { if (-not $Quiet) { Write-Host $text } }
function Write-Utf8NoBom([string]$path, [string]$text) {
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}
function Test-Game([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    return (Test-Path (Join-Path $dir "Nientum.exe"))
}

if (-not $Quiet) {
    Write-Host ""
    Write-Host "==== Nientum 谱面工具 (实验版 · 斜轨与直线投影) 安装程序 ====" -ForegroundColor Cyan
    Write-Host ""
}

$gameProc = Get-Process -Name Nientum -ErrorAction SilentlyContinue
if ($gameProc) {
    Write-Host "检测到游戏正在运行。请先完全退出游戏，再重新运行安装程序。" -ForegroundColor Yellow
    if ($Quiet) { exit 1 }
    Read-Host "确认游戏已退出后按回车继续" | Out-Null
    $gameProc = Get-Process -Name Nientum -ErrorAction SilentlyContinue
    if ($gameProc) {
        Write-Host "游戏仍在运行，已取消。" -ForegroundColor Red
        if (-not $Quiet) { Read-Host "按回车关闭" | Out-Null }
        exit 1
    }
}

# ---------- 定位游戏目录 ----------
$candidates = @()
if ($GameDir) { $candidates += $GameDir }
try {
    $steam = (Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) {
        $steam = (Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam" -ErrorAction SilentlyContinue).InstallPath
    }
    if ($steam) {
        $candidates += (Join-Path $steam "steamapps\common\Liora")
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '"path"\s+"(.+?)"') {
                    $libPath = $Matches[1].Replace('\\', '\')
                    $candidates += (Join-Path $libPath "steamapps\common\Liora")
                }
            }
        }
    }
} catch { }
$candidates += "C:\Program Files (x86)\Steam\steamapps\common\Liora"

$found = $null
foreach ($c in $candidates) {
    if (Test-Game $c) { $found = $c; break }
}

if (-not $found -and -not $Quiet) {
    Write-Host "未自动找到游戏目录。" -ForegroundColor Yellow
    Write-Host "请把 Liora 游戏文件夹（包含 Nientum.exe）拖到这里后回车，或直接输入完整路径："
    $manual = Read-Host
    $manual = $manual.Trim().Trim('"').Trim()
    if ($manual) {
        if (Test-Game $manual) { $found = $manual }
        elseif (Test-Game (Join-Path $manual "Liora")) { $found = Join-Path $manual "Liora" }
    }
}

# ---------- 自动部署 BepInEx 框架并安装实验版 Mod ----------
$bepInstalledNow = $false
$modInstalled = $false
if ($found) {
    Say "游戏目录：$found"
    $bepRoot = Join-Path $found "BepInEx"
    $bepCore = Join-Path $bepRoot "core"
    $plugins = Join-Path $bepRoot "plugins"
    $bepConfig = Join-Path $bepRoot "config"
    $bepPatchers = Join-Path $bepRoot "patchers"
    $bepCache = Join-Path $bepRoot "cache"
    $gameCharts = Join-Path $found "CustomCharts"

    $needBep = (-not (Test-Path (Join-Path $found "winhttp.dll"))) -or
               (-not (Test-Path (Join-Path $found "doorstop_config.ini"))) -or
               (-not (Test-Path (Join-Path $bepCore "BepInEx.Preloader.dll")))

    if ($needBep) {
        if (Test-Path (Join-Path $bepSrc "BepInEx\core\BepInEx.Preloader.dll")) {
            Say "未检测到完整的 BepInEx，正在一键自动安装 BepInEx 框架..."
            New-Item -ItemType Directory -Path $bepCore, $plugins, $bepConfig, $bepPatchers, $bepCache -Force | Out-Null
            Copy-Item (Join-Path $bepSrc "winhttp.dll") $found -Force
            if (-not (Test-Path (Join-Path $found "doorstop_config.ini"))) {
                Copy-Item (Join-Path $bepSrc "doorstop_config.ini") $found -Force
            }
            if (Test-Path (Join-Path $bepSrc ".doorstop_version")) {
                Copy-Item (Join-Path $bepSrc ".doorstop_version") $found -Force
            }
            Copy-Item (Join-Path $bepSrc "BepInEx\core\*") $bepCore -Force
            $bepInstalledNow = $true
            if (-not $Quiet) { Write-Host "已自动安装 BepInEx 框架并生成 BepInEx 文件夹。" -ForegroundColor Green }
        } else {
            Write-Host "未发现 BepInEx 且安装包内缺少 BepInEx 框架目录：$bepSrc" -ForegroundColor Yellow
        }
    } else {
        New-Item -ItemType Directory -Path $plugins, $bepConfig -Force | Out-Null
        Say "已检测到游戏内现有 BepInEx 框架（保持原有配置与其他插件不变）。"
    }

    if (-not (Test-Path $plugins)) {
        Write-Host "BepInEx 插件目录不可用，实验版 Mod 未安装。实验版编辑器仍可独立使用。" -ForegroundColor Yellow
    } elseif (-not (Test-Path $modDll)) {
        Write-Host "安装包内缺少实验版 Mod 文件：$modDll" -ForegroundColor Red
    } else {
        $target = Join-Path $plugins "NientumCustomCharts.dll"
        $stableBak = Join-Path $plugins "NientumCustomCharts.dll.stable_bak"
        if (Test-Path $target) {
            if (-not (Test-Path $stableBak)) {
                Copy-Item $target $stableBak -Force
                Say "已保留原版/稳定版 Mod 备份：NientumCustomCharts.dll.stable_bak"
            } else {
                $backup = $target + ".bak_" + (Get-Date -Format "yyyyMMdd_HHmmss")
                Copy-Item $target $backup -Force
                Say "已备份当前 Mod：$(Split-Path -Leaf $backup)"
            }
        }
        Copy-Item $modDll $target -Force
        $modInstalled = $true
        Say "实验版 Mod 已自动安装：$target"

        $cfg = Join-Path $bepConfig "com.nientum.customcharts.cfg"
        if (Test-Path $cfg) {
            try {
                $content = Get-Content $cfg -Raw
                $content = [regex]::Replace($content, '(?m)^ExtraChartsDirectory\s*=.*$', 'ExtraChartsDirectory = ' + $gameCharts)
                Write-Utf8NoBom $cfg $content
            } catch { }
        }
        if (-not (Test-Path $gameCharts)) {
            New-Item -ItemType Directory -Path $gameCharts | Out-Null
        }
    }
} else {
    Write-Host "未指定有效的游戏目录，BepInEx 与 Mod 未安装。实验版编辑器仍可独立使用。" -ForegroundColor Yellow
}

# ---------- 实验版编辑器设置 ----------
$settingsPath = Join-Path $editorDir "ui_settings.json"
$settings = $null
if (Test-Path $settingsPath) {
    try { $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json } catch { $settings = $null }
}
if (-not $settings) {
    $settings = [pscustomobject]@{
        SettingsVersion = 5
        GamePath = ""
        Hitsound = $true
        Metronome = $false
        FollowPlayhead = $true
        AutoMiddle = $true
        ShowWaveform = $true
        MasterVolume = 1.0
        HitsoundVolume = 0.65
        HitLeadMs = 20
        MetronomeVolume = 0.55
        DefaultRate = 1.0
        SnapDiv = 16
        PreviewGridDiv = 2
        PanelLeftVisible = $true
        PanelRightVisible = $true
        PanelLibrary = $true
        PanelMeta = $true
        PanelHealth = $true
        PanelInspector = $true
        GpuAcceleration = $true
        PreviewScale = 1.0
        MaxFps = 0
        RenderQuality = "high"
        ShowProjectionBox = $true
        ShowProjectionLines = $true
    }
}
$settings | Add-Member -NotePropertyName GamePath -NotePropertyValue $(if ($found) { $found } else { "" }) -Force
Write-Utf8NoBom $settingsPath ($settings | ConvertTo-Json -Depth 4)
Write-Utf8NoBom (Join-Path $editorDir "portable.txt") "portable"

# ---------- 独立桌面快捷方式（不覆盖正式版） ----------
if (-not $NoShortcut) {
    try {
        $desktop = [Environment]::GetFolderPath("Desktop")
        $lnkPath = Join-Path $desktop "Nientum 谱面编辑器 (实验版).lnk"
        $exePath = Join-Path $editorDir "NientumStudio.exe"
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($lnkPath)
        $lnk.TargetPath = $exePath
        $lnk.WorkingDirectory = $editorDir
        $lnk.Description = "Nientum Studio 实验版（斜轨与直线投影）"
        $lnk.Save()
        Say "已创建独立桌面快捷方式：$lnkPath"
    } catch {
        Say "创建快捷方式失败（不影响使用）：$($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "实验版安装完成（与正式版互不干扰）" -ForegroundColor Green
Write-Host "  实验版编辑器：$(Join-Path $editorDir 'NientumStudio.exe')"
if ($found) {
    Write-Host "  游戏目录：$found"
    Write-Host "  同步目标：$(Join-Path $found 'CustomCharts')"
}
if ($bepInstalledNow) {
    Write-Host "  BepInEx：已自动安装到游戏目录" -ForegroundColor Green
}
if ($modInstalled) {
    Write-Host "  实验版 Mod：已自动安装（支持斜轨图层 .layers.json；卸载可自动还原稳定版备份）" -ForegroundColor Green
}
if (-not $Quiet) { Read-Host "按回车关闭" | Out-Null }
exit 0
