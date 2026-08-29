param(
    [string]$StationDirectory = (Join-Path $PSScriptRoot "..\publish"),
    [string]$MigrationDirectory = (Join-Path $PSScriptRoot "..\publish-migration"),
    [string]$ServerDirectory = (Join-Path $PSScriptRoot "..\publish-server"),
    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot "..\artifacts")
)

$ErrorActionPreference = "Stop"

$required = @(
    (Join-Path $StationDirectory "FarmaFlowAgent.exe"),
    (Join-Path $StationDirectory "MicrosoftEdgeWebview2Setup.exe"),
    (Join-Path $StationDirectory "runtimes\win-x64\native\pdfium.dll"),
    (Join-Path $MigrationDirectory "FarmaFlowMigracaoSetup.exe"),
    (Join-Path $MigrationDirectory "FarmaFlow.Migration.exe"),
    (Join-Path $MigrationDirectory "postgres\bin\initdb.exe"),
    (Join-Path $MigrationDirectory "postgres\bin\pg_ctl.exe"),
    (Join-Path $MigrationDirectory "postgres\bin\createdb.exe"),
    (Join-Path $MigrationDirectory "postgres\bin\dropdb.exe"),
    (Join-Path $MigrationDirectory "postgres\bin\pg_dump.exe"),
    (Join-Path $MigrationDirectory "postgres\bin\pg_restore.exe"),
    (Join-Path $ServerDirectory "FarmaFlowServerHost.exe"),
    (Join-Path $ServerDirectory "FarmaFlowServerSetup.exe"),
    (Join-Path $ServerDirectory "FarmaFlow-Estacao-Setup.exe"),
    (Join-Path $ServerDirectory "runtime\backend\app.jar"),
    (Join-Path $ServerDirectory "runtime\web\server.js"),
    (Join-Path $ServerDirectory "runtime\web\.next\static"),
    (Join-Path $ServerDirectory "runtime\java\bin\java.exe"),
    (Join-Path $ServerDirectory "runtime\node\node.exe"),
    (Join-Path $ServerDirectory "runtime\postgres\bin\postgres.exe"),
    (Join-Path $ServerDirectory "runtime\postgres\bin\pg_dump.exe"),
    (Join-Path $ServerDirectory "runtime\postgres\bin\pg_restore.exe"),
    (Join-Path $ArtifactsDirectory "FarmaFlow-Server-Setup.exe"),
    (Join-Path $ArtifactsDirectory "FarmaFlow-Estacao-Setup.exe"),
    (Join-Path $ArtifactsDirectory "FarmaFlow-Migracao-Setup.exe")
)

$missing = @($required | Where-Object { -not (Test-Path $_) })
if ($missing.Count -gt 0) {
    throw "Payload dos instaladores incompleto:`n$($missing -join "`n")"
}

foreach ($installer in Get-ChildItem $ArtifactsDirectory -Filter "FarmaFlow-*-Setup.exe") {
    if ($installer.Length -lt 1MB) { throw "Instalador suspeitamente pequeno: $($installer.FullName)" }
}

Write-Host "Payload dos três instaladores validado com sucesso."
