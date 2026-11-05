<#
    setup.ps1 - descarrega as dependências do bot (só é preciso correr uma vez).
    Uso:  .\setup.ps1
#>

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$tools = Join-Path $root "tools"
$natives = Join-Path $root "natives"
$tmp = Join-Path $root "tmp"

New-Item -ItemType Directory -Force -Path $tools, $natives, $tmp | Out-Null

function Get-File($url, $destination) {
    if (Test-Path $destination) {
        Write-Host "  [ok] $(Split-Path -Leaf $destination) já existe" -ForegroundColor DarkGray
        return
    }
    Write-Host "  [..] a descarregar $(Split-Path -Leaf $destination)" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $destination
}

Write-Host "`n== yt-dlp ==" -ForegroundColor White
# Nightly: o YouTube muda mais depressa do que as versões estáveis.
Get-File "https://github.com/yt-dlp/yt-dlp-nightly-builds/releases/latest/download/yt-dlp.exe" (Join-Path $tools "yt-dlp.exe")

Write-Host "`n== deno (runtime JS para o yt-dlp) ==" -ForegroundColor White
$denoExe = Join-Path $tools "deno.exe"
if (-not (Test-Path $denoExe)) {
    $denoZip = Join-Path $tmp "deno.zip"
    Get-File "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip" $denoZip
    Expand-Archive $denoZip -DestinationPath (Join-Path $tmp "deno") -Force
    Copy-Item (Join-Path $tmp "deno" "deno.exe") $denoExe -Force
    Remove-Item (Join-Path $tmp "deno") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $denoZip -Force -ErrorAction SilentlyContinue
    Write-Host "  [ok] deno.exe" -ForegroundColor DarkGray
}
else {
    Write-Host "  [ok] deno.exe já existe" -ForegroundColor DarkGray
}

Write-Host "`n== ffmpeg ==" -ForegroundColor White
$ffmpegExe = Join-Path $tools "ffmpeg.exe"
if (-not (Test-Path $ffmpegExe)) {
    $zip = Join-Path $tmp "ffmpeg.zip"
    Get-File "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip" $zip
    Write-Host "  [..] a extrair ffmpeg" -ForegroundColor Cyan
    Expand-Archive $zip -DestinationPath (Join-Path $tmp "ff") -Force
    $found = Get-ChildItem (Join-Path $tmp "ff") -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
    Copy-Item $found.FullName $ffmpegExe -Force
    Remove-Item (Join-Path $tmp "ff") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host "  [ok] ffmpeg.exe já existe" -ForegroundColor DarkGray
}

Write-Host "`n== bibliotecas nativas (voz) ==" -ForegroundColor White

if (-not (Test-Path (Join-Path $natives "libsodium.dll")) -or -not (Test-Path (Join-Path $natives "opus.dll"))) {
    $vnZip = Join-Path $tmp "voice-natives.zip"
    Get-File "https://raw.githubusercontent.com/discord-net/Discord.Net/dev/voice-natives/vnext_natives_win32_x64.zip" $vnZip
    Expand-Archive $vnZip -DestinationPath (Join-Path $tmp "vn") -Force

    Copy-Item (Join-Path $tmp "vn" "libsodium.dll") (Join-Path $natives "libsodium.dll") -Force
    Copy-Item (Join-Path $tmp "vn" "libopus.dll") (Join-Path $natives "opus.dll") -Force
    Remove-Item (Join-Path $tmp "vn") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $vnZip -Force -ErrorAction SilentlyContinue
    Write-Host "  [ok] libsodium.dll + opus.dll" -ForegroundColor DarkGray
}
else {
    Write-Host "  [ok] libsodium.dll + opus.dll já existem" -ForegroundColor DarkGray
}

if (-not (Test-Path (Join-Path $natives "libdave.dll"))) {
    $daveZip = Join-Path $tmp "libdave.zip"
    Get-File "https://github.com/discord/libdave/releases/download/v1.2.2/cpp/libdave-Windows-X64-boringssl.zip" $daveZip
    Expand-Archive $daveZip -DestinationPath (Join-Path $tmp "dave") -Force
    Copy-Item (Join-Path $tmp "dave" "bin" "libdave.dll") (Join-Path $natives "libdave.dll") -Force
    Remove-Item (Join-Path $tmp "dave") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $daveZip -Force -ErrorAction SilentlyContinue
    Write-Host "  [ok] libdave.dll (E2EE/DAVE)" -ForegroundColor DarkGray
}
else {
    Write-Host "  [ok] libdave.dll já existe" -ForegroundColor DarkGray
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n== build ==" -ForegroundColor White
Push-Location $root
try {
    dotnet build -c Release --nologo
}
finally {
    Pop-Location
}

Write-Host "`nPronto! Falta colar o token no config.json e correr .\run.ps1`n" -ForegroundColor Green
