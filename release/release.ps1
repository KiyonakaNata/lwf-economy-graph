# LWF Economy Graph — 配布用の zip を作る
#
#   powershell -ExecutionPolicy Bypass -File release\release.ps1
#
# 開発用と同じソースから、同じ DLL を作って包むだけ。
# 配布用に機能を削った別ビルドは作らない——手元で動いている物と配った物が
# 別になると、不具合の報告が来たときに再現できなくなるため。
#
# zip には最低限だけ入れる。説明は GitHub 側にあるので、
# ここに画像や長い文章を抱えても更新が二重になるだけ:
#
#   LwfEconomyGraph.dll
#   README.txt   （zip-README.txt を改名したもの）
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
Write-Host ""
Write-Host "中身:"
Add-Type -AssemblyName System.IO.Compression
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
foreach ($entry in $archive.Entries) { Write-Host "  $($entry.FullName)" }
$archive.Dispose()
