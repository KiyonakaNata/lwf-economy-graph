# LWF Economy Graph — ビルドスクリプト
#
# dotnet SDK が無い環境向け。.NET Framework 同梱の csc.exe（C# 5）で直接コンパイルし、
# 出力をゲームの BepInEx/plugins へコピーする。
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -NoDeploy
#
# ⚠ ソースは UTF-8。csc.exe は BOM が無いと既定コードページで読むため、
#   /codepage:65001 を明示している（日本語の文字列リテラルが化けるのを防ぐ）。
# ⚠ このファイル自身は UTF-8 (BOM あり) で保存すること。
#   PowerShell 5.1 は BOM なし UTF-8 の .ps1 を ANSI として読み、行継続が壊れる。

param(
    [switch]$NoDeploy,
    # 別の場所に入れている場合はここを書き換えるか -Game で指す
    [string]$Game = "D:\SteamLibrary\steamapps\common\Lazy Witch's Factory"
)

$ErrorActionPreference = 'Stop'

$Managed = Join-Path $Game 'LazyWitchsFactory_Data\Managed'
$Core    = Join-Path $Game 'BepInEx\core'
$Plugins = Join-Path $Game 'BepInEx\plugins'
$Scripts = Join-Path $Game 'BepInEx\scripts'
$Csc     = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$outDir  = Join-Path $here 'bin'
$outDll  = Join-Path $outDir 'LwfEconomyGraph.dll'
$sources = @(
    (Join-Path $here 'EconomyChart.cs'),      # 描画の中身（ゲームにも BepInEx にも依存しない）
    (Join-Path $here 'EconomyChartColors.cs'),  # アイコンの代表色（自動生成）
    (Join-Path $here 'EconomyGraphMod.cs')    # プラグイン本体（記録・フック・IMGUI の筆）
)

foreach ($p in @($Csc, $Managed) + $sources) {
    if (-not (Test-Path $p)) { throw "見つかりません: $p" }
}
# 参照に BepInEx.dll と 0Harmony.dll が要る（どちらも BepInEx/core）
if (-not (Test-Path $Core)) {
    throw "BepInEx が見つかりません: $Core`n  先にゲームへ BepInEx 5 を入れてください。"
}
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$refNames = @(
    'mscorlib.dll',
    'System.dll',
    'System.Core.dll',
    'netstandard.dll',
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'UnityEngine.TextRenderingModule.dll',
    'Unity.InputSystem.dll',
    'Assembly-CSharp.dll',
    'Assembly-CSharp-firstpass.dll',
    'R3.dll',
    'R3.Unity.dll',
    'ObservableCollections.dll',
    'UniTask.dll',
    'Newtonsoft.Json.dll',
    'Unity.Localization.dll',
    'Unity.Addressables.dll',
    'Unity.ResourceManager.dll',
    'UnityEngine.UI.dll',
    'Unity.TextMeshPro.dll',
    'DOTween.dll',
    'UnityEngine.AudioModule.dll',
    'UnityEngine.PhysicsModule.dll',
    'UnityEngine.Physics2DModule.dll',
    'UnityEngine.AnimationModule.dll',
    'UnityEngine.UIModule.dll',
    'UnityEngine.InputLegacyModule.dll',
    'UnityEngine.ImageConversionModule.dll',
    'UnityEngine.ParticleSystemModule.dll',
    'UnityEngine.TilemapModule.dll',
    'UnityEngine.VideoModule.dll'
)
$coreRefNames = @('BepInEx.dll', '0Harmony.dll')

$refs = @()
foreach ($n in $refNames) {
    $p = Join-Path $Managed $n
    # 版によって存在しないものは黙って飛ばす（必須参照は下で落ちる）
    if (Test-Path $p) { $refs += "/r:`"$p`"" }
}
foreach ($n in $coreRefNames) {
    $p = Join-Path $Core $n
    if (-not (Test-Path $p)) { throw "参照アセンブリが見つかりません: $p" }
    $refs += "/r:`"$p`""
}

$cscArgs = @(
    '/nologo', '/noconfig', '/nostdlib+', '/target:library', '/optimize+', '/warn:2',
    '/codepage:65001',
    # ScriptEngine（開発用の再読込）は PDB を必須で読むので必ず出す。
    # 無いと SymbolsNotFoundException で読み込みに失敗する
    '/debug:pdbonly',
    "/out:`"$outDll`""
) + $refs + ($sources | ForEach-Object { "`"$_`"" })

Write-Host "ビルド中: $outDll"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Csc
$psi.Arguments = ($cscArgs -join ' ')
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$proc = [System.Diagnostics.Process]::Start($psi)
$stdout = $proc.StandardOutput.ReadToEnd()
$stderr = $proc.StandardError.ReadToEnd()
$proc.WaitForExit()
if ($stdout) { Write-Host $stdout }
if ($stderr) { Write-Host $stderr }
if ($proc.ExitCode -ne 0) { throw "コンパイルに失敗しました (exit $($proc.ExitCode))" }

Write-Host "OK: $outDll ($((Get-Item $outDll).Length) bytes)"

if (-not $NoDeploy) {
    # ScriptEngine（開発用）が入っていれば scripts\ へ置く。
    # そちらは**ゲームを起動したまま F6 で読み直せる**ので、テストプレイを捨てずに済む。
    # 配信するときは ScriptEngine を外して plugins\ に戻すこと。
    $hot = Test-Path (Join-Path $Plugins 'ScriptEngine.dll')
    $target = if ($hot) { $Scripts } else { $Plugins }

    if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target | Out-Null }

    # plugins\ に古い実体が残っていると二重に読まれる
    $stale = Join-Path $Plugins 'LwfEconomyGraph.dll'
    if ($hot -and (Test-Path $stale)) {
        try {
            Remove-Item $stale -Force
            $stalePdb = Join-Path $Plugins 'LwfEconomyGraph.pdb'
            if (Test-Path $stalePdb) { Remove-Item $stalePdb -Force }
            Write-Host "plugins\ の古い実体を取り除きました（scripts\ 側に一本化）"
        } catch {
            Write-Host "!! plugins\ の古い実体を消せません（ゲームが掴んでいます）。"
            Write-Host "   一度だけゲームを終了して、このスクリプトを実行し直してください。"
            Write-Host "   それ以降は起動したままで差し替えられます。"
            exit 2
        }
    }

    try {
        Copy-Item $outDll -Destination $target -Force

        # PDB も一緒に置く（ScriptEngine が読む）
        $outPdb = [System.IO.Path]::ChangeExtension($outDll, '.pdb')
        if (Test-Path $outPdb) { Copy-Item $outPdb -Destination $target -Force }
    } catch {
        Write-Host ""
        Write-Host "!! 配置できません。DLL が掴まれています。"
        Write-Host "   （ビルド自体は成功しています: $outDll）"
        exit 2
    }

    Write-Host "配置しました: $(Join-Path $target 'LwfEconomyGraph.dll')"
    if ($hot) { Write-Host "ゲーム内で F6 を押すと読み直されます（再起動は不要）" }
}
