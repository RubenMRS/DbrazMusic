<#
    run.ps1 - arranca o bot.
    Uso:  .\run.ps1            (arranca)
          .\run.ps1 --check    (verifica dependências e token)
#>

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Push-Location $root
try {
    if ($args.Count -gt 0) {
        dotnet run -c Release -- @args
    }
    else {
        dotnet run -c Release
    }
}
finally {
    Pop-Location
}
