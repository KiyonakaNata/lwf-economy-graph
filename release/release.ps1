# LWF Economy Graph — 配布用の zip を作る
#
#   powershell -ExecutionPolicy Bypass -File release\release.ps1
#
# 開発用と同じソースから、同じ DLL を作って包むだけ。
# 配布用に機能を削った別ビルドは作らない——手元で動いている物と配った物が
# 別になると、不具合の報告が来たときに再現できなくなるため。
#
# zip は2つ作る。
#
# GitHub 用（手で入れる人向け）。説明は GitHub 側にあるので最低限だけ:
#   LwfEconomyGraph.dll
#   README.txt   （zip-README.txt を改名したもの）
#
# Thunderstore 用（MOD管理ソフト向け）。決まった形が要る:
#   manifest.json / icon.png / README.md   ← thunderstore\ の中身
#   BepInEx/plugins/LwfEconomyGraph.dll
#
# ⚠ このファイル自身は UTF-8 (BOM あり) で保存すること。
#   PowerShell 5.1 は BOM なし UTF-8 の .ps1 を ANSI として読み、行継続が壊れる。

$ErrorActionPreference = 'Stop'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$root    = Split-Path -Parent $here
$outDir  = Join-Path $root 'dist'
$stage   = Join-Path $outDir 'stage'
$dll     = Join-Path $root 'bin\LwfEconomyGraph.dll'
$source  = Join-Path $root 'EconomyGraphMod.cs'

# ---- ソースから版を読む（ここが唯一の出どころ）----
$version = (Select-String -Path $source -Pattern 'PluginVersion\s*=\s*"([^"]+)"').Matches[0].Groups[1].Value
if (-not $version) { throw "PluginVersion を読めませんでした: $source" }
Write-Host "版: $version"

# ---- ビルド（配置はしない）----
# ($LASTEXITCODE は .ps1 の呼び出しでは更新されないので当てにしない。
#  ソースより新しい DLL が出来ているかで判断する)
$newest = (Get-ChildItem (Join-Path $root '*.cs') | Sort-Object LastWriteTime -Descending)[0].LastWriteTime
& (Join-Path $root 'build.ps1') -NoDeploy

if (-not (Test-Path $dll)) { throw "DLL がありません: $dll" }
if ((Get-Item $dll).LastWriteTime -lt $newest) { throw "DLL がソースより古い。ビルドに失敗しています" }

# ---- 並べる ----
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item $dll -Destination $stage -Force
Copy-Item (Join-Path $here 'zip-README.txt') -Destination (Join-Path $stage 'README.txt') -Force

# PDB は入れない。ScriptEngine で読み直すときにしか要らず、配布物では容量だけ食う

# ---- 包む ----
$zip = Join-Path $outDir "LwfEconomyGraph-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)

Remove-Item $stage -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host "OK: $zip ($size KB)"

# ---- Thunderstore 用 ----
# manifest / icon / README を直下に置き、DLL は BepInEx\plugins\ に入れる形。
# 版は EconomyGraphMod.cs が唯一の出どころなので、manifest の版はここで差し替える
$tsSrc = Join-Path $here 'thunderstore'
if (Test-Path $tsSrc) {
    Add-Type -AssemblyName System.Drawing
    $icon = Join-Path $tsSrc 'icon.png'
    $img = [System.Drawing.Image]::FromFile($icon)
    $iw = $img.Width; $ih = $img.Height
    $img.Dispose()
    if ($iw -ne 256 -or $ih -ne 256) { throw "icon.png は 256x256 でなければなりません（いまは ${iw}x${ih}）" }

    $manifestText = Get-Content (Join-Path $tsSrc 'manifest.json') -Raw -Encoding UTF8
    $manifest = $manifestText | ConvertFrom-Json
    if ($manifest.description.Length -gt 250) {
        throw "manifest の description が $($manifest.description.Length) 文字です（250文字まで）"
    }
    $manifestText = [regex]::Replace($manifestText, '("version_number"\s*:\s*")[^"]+(")', "`${1}$version`${2}")

    $tsStage = Join-Path $outDir 'ts-stage'
    if (Test-Path $tsStage) { Remove-Item $tsStage -Recurse -Force }
    New-Item -ItemType Directory -Path $tsStage -Force | Out-Null

    Copy-Item $icon -Destination $tsStage -Force
    Copy-Item (Join-Path $tsSrc 'README.md') -Destination $tsStage -Force
    [System.IO.File]::WriteAllText((Join-Path $tsStage 'manifest.json'), $manifestText,
        (New-Object System.Text.UTF8Encoding($false)))

    $tsZip = Join-Path $outDir "LwfEconomyGraph-thunderstore-$version.zip"
    if (Test-Path $tsZip) { Remove-Item $tsZip -Force }

    # ⚠ CreateFromDirectory は使わない。.NET Framework はエントリ名を「\」で書くが、
    #   zip の仕様は「/」。Thunderstore 側（Python）はそれを1個のファイル名として読むので、
    #   BepInEx/plugins に置かれず、MOD管理ソフトが正しく入れられなくなる。
    #   ここは名前を明示して詰める
    $entries = @(
        @{ Path = (Join-Path $tsStage 'manifest.json'); Name = 'manifest.json' },
        @{ Path = (Join-Path $tsStage 'icon.png');      Name = 'icon.png' },
        @{ Path = (Join-Path $tsStage 'README.md');     Name = 'README.md' },
        @{ Path = $dll; Name = 'BepInEx/plugins/LwfEconomyGraph.dll' }
    )
    $archive = [System.IO.Compression.ZipFile]::Open($tsZip, 'Create')
    foreach ($e in $entries) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $e.Path, $e.Name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $archive.Dispose()
    Remove-Item $tsStage -Recurse -Force

    $tsSize = [math]::Round((Get-Item $tsZip).Length / 1KB, 1)
    Write-Host "OK: $tsZip ($tsSize KB)"
}
Write-Host ""
Add-Type -AssemblyName System.IO.Compression
foreach ($z in @($zip, $tsZip)) {
    if (-not $z) { continue }
    Write-Host "$(Split-Path $z -Leaf) の中身:"
    $archive = [System.IO.Compression.ZipFile]::OpenRead($z)
    foreach ($entry in $archive.Entries) { Write-Host "  $($entry.FullName)" }
    $archive.Dispose()
}
