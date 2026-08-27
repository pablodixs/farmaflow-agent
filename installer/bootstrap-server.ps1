param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory
)

$ErrorActionPreference = "Stop"
$serviceRoot = Join-Path $env:ProgramData "FarmaFlow\Server"
$databaseRoot = Join-Path $serviceRoot "postgres-data"
$runtimeRoot = Join-Path $InstallDirectory "runtime"
$postgresBin = Join-Path $runtimeRoot "postgres\bin"
$passwordFile = Join-Path $env:TEMP "farmaflow-postgres-password.txt"

function New-RandomSecret([int]$bytes = 32) {
    $buffer = New-Object byte[] $bytes
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($buffer) } finally { $generator.Dispose() }
    return [Convert]::ToBase64String($buffer)
}

New-Item -ItemType Directory -Force -Path $serviceRoot, $databaseRoot | Out-Null

$secretsPath = Join-Path $serviceRoot "secrets.json"
if (-not (Test-Path $secretsPath)) {
    $databasePassword = New-RandomSecret 36
    $secrets = [ordered]@{
        DatabasePassword = $databasePassword
        JwtSecret = New-RandomSecret 48
        NextAuthSecret = New-RandomSecret 48
        BackupKey = New-RandomSecret 32
    }
    $secrets | ConvertTo-Json | Set-Content -Path $secretsPath -Encoding UTF8
    & icacls.exe $secretsPath /inheritance:r /grant:r "SYSTEM:(F)" "Administrators:(F)" | Out-Null
} else {
    $secrets = Get-Content $secretsPath -Raw | ConvertFrom-Json
    $databasePassword = $secrets.DatabasePassword
}

if (-not (Test-Path (Join-Path $databaseRoot "PG_VERSION"))) {
    Set-Content -Path $passwordFile -Value $databasePassword -NoNewline -Encoding ASCII
    try {
        & (Join-Path $postgresBin "initdb.exe") --pgdata=$databaseRoot --username=farmaflow --pwfile=$passwordFile --encoding=UTF8 --locale=C
        if ($LASTEXITCODE -ne 0) { throw "initdb falhou com código $LASTEXITCODE" }
        Add-Content -Path (Join-Path $databaseRoot "postgresql.conf") -Value @"
listen_addresses = '127.0.0.1'
port = 54329
password_encryption = 'scram-sha-256'
"@
        Set-Content -Path (Join-Path $databaseRoot "pg_hba.conf") -Value @"
local all all scram-sha-256
host all all 127.0.0.1/32 scram-sha-256
host all all ::1/128 scram-sha-256
"@ -Encoding UTF8
    } finally {
        Remove-Item $passwordFile -Force -ErrorAction SilentlyContinue
    }
}

$postgresService = Get-Service -Name "FarmaFlowPostgreSQL" -ErrorAction SilentlyContinue
if ($null -eq $postgresService) {
    & (Join-Path $postgresBin "pg_ctl.exe") register -N "FarmaFlowPostgreSQL" -D $databaseRoot -S auto
    if ($LASTEXITCODE -ne 0) { throw "Não foi possível registrar o serviço PostgreSQL." }
}
Start-Service "FarmaFlowPostgreSQL"

$env:PGPASSWORD = $databasePassword
try {
    $databaseExists = & (Join-Path $postgresBin "psql.exe") --host=127.0.0.1 --port=54329 --username=farmaflow --dbname=postgres --tuples-only --no-align --command="SELECT 1 FROM pg_database WHERE datname='farmaflow'"
    if ($databaseExists.Trim() -ne "1") {
        & (Join-Path $postgresBin "createdb.exe") --host=127.0.0.1 --port=54329 --username=farmaflow --encoding=UTF8 farmaflow
        if ($LASTEXITCODE -ne 0) { throw "Não foi possível criar o banco farmaflow." }
    }
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

$hostExecutable = Join-Path $InstallDirectory "FarmaFlowServerHost.exe"
if ($null -ne (Get-Service -Name "FarmaFlowServer" -ErrorAction SilentlyContinue)) {
    Stop-Service "FarmaFlowServer" -Force -ErrorAction SilentlyContinue
    & sc.exe delete "FarmaFlowServer" | Out-Null
    Start-Sleep -Seconds 1
}
& sc.exe create "FarmaFlowServer" "binPath= `"$hostExecutable`"" "start= demand" "DisplayName= FarmaFlow Server" | Out-Null
& sc.exe failure "FarmaFlowServer" "reset= 86400" "actions= restart/5000/restart/15000/restart/30000" | Out-Null
& sc.exe description "FarmaFlowServer" "Servidor local, proxy HTTPS e supervisor do FarmaFlow" | Out-Null

$ruleName = "FarmaFlow Server HTTPS"
if ($null -eq (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8443 -Profile Private | Out-Null
}

$env:PGPASSWORD = $databasePassword
try {
    $schemaVersion = & (Join-Path $postgresBin "psql.exe") --host=127.0.0.1 --port=54329 --username=farmaflow --dbname=farmaflow --tuples-only --no-align --command="SELECT COALESCE((SELECT MAX(version) FROM public.flyway_schema_history WHERE success), '0')" 2>$null
    $storeCount = & (Join-Path $postgresBin "psql.exe") --host=127.0.0.1 --port=54329 --username=farmaflow --dbname=farmaflow --tuples-only --no-align --command="SELECT COUNT(*) FROM public.stores" 2>$null
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}
if ([int]$schemaVersion -ge 52 -and [int]$storeCount -eq 1) {
    & sc.exe config "FarmaFlowServer" "start= auto" | Out-Null
    Start-Service "FarmaFlowServer"
} else {
    Set-Content -Path (Join-Path $serviceRoot "migration-required.txt") -Value "Restaure e valide um pacote .ffbackup antes de ativar o FarmaFlow Server."
}
Write-Host "FarmaFlow Server instalado. Banco e backups permanecerão em $serviceRoot."
