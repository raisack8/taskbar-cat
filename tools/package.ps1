# 配布用 zip を作る:  package.bat から呼ばれる
#   dist\TaskbarCat-YYYYMMDD.zip （展開すると taskbar-cat\ フォルダになる）
# 個人の設定・スケジュール・ログは入れない
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

# 最新の exe を作ってから詰める（起動中の猫は exe を掴んでいるので一旦止める）
$running = Get-Process TaskbarCat -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process; Start-Sleep -Milliseconds 500 }
cmd /c "$root\build.bat"
$built = $LASTEXITCODE -eq 0
if ($running) { Start-Process (Join-Path $root 'TaskbarCat.exe') }
if (-not $built) { throw "build.bat が失敗しました" }

$include = @(
    'README.md', 'LICENSE', 'build.bat', 'package.bat',
    'TaskbarCat.cs', 'Schedule.cs', 'ScheduleForm.cs', 'OneShot.cs', 'Ports.cs', 'Update.cs', 'TaskbarCat.exe',
    'tools\build_sprites.py', 'tools\analyze.py', 'tools\package.ps1', 'tools\sign.ps1',
    'assets\sprites', 'assets\raw'
)
# 個人のデータ（ini・schedule.txt・oneshot.txt・logs\ など）は %USERPROFILE%\.taskbar-cat か保存先にあり、ここには無い

$stage = Join-Path $env:TEMP ("taskbarcat-pack-" + [guid]::NewGuid().ToString('N'))
$dst = Join-Path $stage 'taskbar-cat'
foreach ($item in $include) {
    $src = Join-Path $root $item
    if (-not (Test-Path $src)) { throw "見つかりません: $item" }
    $to = Join-Path $dst $item
    New-Item -ItemType Directory -Force (Split-Path $to -Parent) | Out-Null
    Copy-Item $src $to -Recurse -Force
}

$outDir = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $outDir | Out-Null
$zip = Join-Path $outDir ("TaskbarCat-" + (Get-Date -Format 'yyyyMMdd') + ".zip")
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# CreateFromDirectory はパス区切りが \ になり、環境によって展開できないので / で 1 件ずつ入れる
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $base = (Get-Item $dst).FullName   # TEMP が短い名前 (TK-SAK~1) でも正式なパスに揃える
    foreach ($f in Get-ChildItem $base -Recurse -File) {
        $name = 'taskbar-cat/' + $f.FullName.Substring($base.Length + 1).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force

$size = (Get-Item $zip).Length / 1MB
Write-Host ("作成しました: {0}  ({1:N1} MB)" -f $zip, $size)
