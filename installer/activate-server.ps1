param(
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [switch]$KeepMigrationMarker
)
$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -InstallDirectory `"$InstallDirectory`""
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait
    exit $LASTEXITCODE
}
$serviceRoot = Join-Path $env:ProgramData "FarmaFlow\Server"
$secrets = Get-Content (Join-Path $serviceRoot "secrets.json") -Raw | ConvertFrom-Json
$postgresBin = Join-Path $InstallDirectory "runtime\postgres\bin"
$env:PGPASSWORD = $secrets.DatabasePassword
try {
    $schemaVersion = & (Join-Path $postgresBin "psql.exe") --host=127.0.0.1 --port=54329 --username=farmaflow --dbname=farmaflow --tuples-only --no-align --command="SELECT COALESCE((SELECT version FROM public.flyway_schema_history WHERE success ORDER BY installed_rank DESC LIMIT 1), '0')"
    if ([int]$schemaVersion -lt 52 -or [int]$schemaVersion -gt 54) { throw "Schema V$schemaVersion incompatível; esta release suporta V52 a V54." }
    $failedMigrations = & (Join-Path $postgresBin "psql.exe") --host=127.0.0.1 --port=54329 --username=farmaflow --dbname=farmaflow --tuples-only --no-align --command="SELECT COUNT(*) FROM public.flyway_schema_history WHERE NOT success"
    if ([int]$failedMigrations -ne 0) { throw "Flyway contém $failedMigrations migration(s) com falha; execute o repair antes de ativar." }
    $storeCount = & (Join-Path $postgresBin "psql.exe") --host=127.0.0.1 --port=54329 --username=farmaflow --dbname=farmaflow --tuples-only --no-align --command="SELECT COUNT(*) FROM public.stores"
    if ([int]$storeCount -ne 1) { throw "LOCAL_SINGLE_STORE exige exatamente uma loja; foram encontradas $storeCount." }
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}
& sc.exe config "FarmaFlowServer" "start= auto" | Out-Null
Start-Service "FarmaFlowServer"
if (-not $KeepMigrationMarker) {
    Remove-Item (Join-Path $serviceRoot "migration-required.txt") -Force -ErrorAction SilentlyContinue
}
Write-Host "FarmaFlow Server ativado no schema V$schemaVersion."
