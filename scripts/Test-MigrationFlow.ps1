param(
    [Parameter(Mandatory = $true)][string]$PostgresBin,
    [Parameter(Mandatory = $true)][string]$MigrationExecutable
)

$ErrorActionPreference = "Stop"
$root = Join-Path $env:RUNNER_TEMP "farmaflow-migration-flow-$([Guid]::NewGuid().ToString('N'))"
$data = Join-Path $root "data"
$passwordFile = Join-Path $root "password.txt"
$package = Join-Path $root "store.ffstore"
$databasePassword = "MigrationSmoke-Database-2026"
$packagePassword = "MigrationSmoke-Package-2026"
$sourceDatabase = "farmaflow_staging_smoke"
$targetDatabase = "farmaflow_target_smoke"
$storeId = "20000000-0000-0000-0000-000000000001"
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()

function Invoke-Migration([string[]]$Arguments, [string[]]$Secrets, [string]$InputLine = "") {
    $start = [System.Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $MigrationExecutable
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.RedirectStandardInput = -not [string]::IsNullOrEmpty($InputLine)
    foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
    for ($index = 0; $index -lt $Secrets.Count; $index++) {
        $start.Environment["FARMAFLOW_SECRET_$($index + 1)"] = $Secrets[$index]
    }
    $process = [System.Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "Não foi possível iniciar FarmaFlow.Migration.exe." }
    if ($start.RedirectStandardInput) { $process.StandardInput.WriteLine($InputLine); $process.StandardInput.Close() }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Migration falhou ($($Arguments[0])): $($stderr.Result) $($stdout.Result)" }
    return $stdout.Result
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
Set-Content -Path $passwordFile -Value $databasePassword -NoNewline -Encoding ASCII
$env:PGPASSWORD = $databasePassword
try {
    & (Join-Path $PostgresBin "initdb.exe") --pgdata=$data --username=farmaflow --pwfile=$passwordFile --encoding=UTF8 --locale=C | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "initdb falhou." }
    Add-Content -Path (Join-Path $data "postgresql.conf") -Value "`nlisten_addresses='127.0.0.1'`nport=$port`n"
    Set-Content -Path (Join-Path $data "pg_hba.conf") -Value "host all all 127.0.0.1/32 scram-sha-256`nlocal all all scram-sha-256`n" -Encoding utf8NoBOM
    $postgresLog = Join-Path $root "postgres.log"
    & (Join-Path $PostgresBin "pg_ctl.exe") start --pgdata=$data --wait --timeout=60 --log=$postgresLog | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "pg_ctl start falhou." }
    & (Join-Path $PostgresBin "createdb.exe") --host=127.0.0.1 --port=$port --username=farmaflow $sourceDatabase
    if ($LASTEXITCODE -ne 0) { throw "Criação do banco de origem falhou." }
    $schemaFile = Join-Path $PSScriptRoot "Test-MigrationFlow.sql"
    & (Join-Path $PostgresBin "psql.exe") --host=127.0.0.1 --port=$port --username=farmaflow --dbname=$sourceDatabase --set=ON_ERROR_STOP=1 --file=$schemaFile | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Preparação do schema de teste falhou." }

    Invoke-Migration -Arguments @("filter-store-staging", "--host", "127.0.0.1", "--port", $port.ToString(), "--database", $sourceDatabase, "--username", "farmaflow", "--store-id", $storeId) -Secrets @($databasePassword) -InputLine $sourceDatabase | Out-Null
    Invoke-Migration -Arguments @("archive-media", "--host", "127.0.0.1", "--port", $port.ToString(), "--database", $sourceDatabase, "--username", "farmaflow") -Secrets @($databasePassword) | Out-Null

    $filterCheck = & (Join-Path $PostgresBin "psql.exe") --host=127.0.0.1 --port=$port --username=farmaflow --dbname=$sourceDatabase --tuples-only --no-align --command="SELECT (SELECT count(*) FROM stores),(SELECT count(*) FROM organizations),(SELECT count(*) FROM label_templates),(SELECT imported_by_user_id IS NULL FROM cmed_import_runs LIMIT 1)"
    if ($LASTEXITCODE -ne 0) { throw "Validação do filtro por loja falhou." }
    if ($filterCheck.Trim() -ne "1|1|2|t") { throw "Filtro por loja incorreto: $filterCheck" }

    Invoke-Migration -Arguments @("export-full", "--host", "127.0.0.1", "--port", $port.ToString(), "--database", $sourceDatabase, "--username", "farmaflow", "--pg-bin", $PostgresBin, "--ssl-mode", "Prefer", "--store-id", $storeId, "--output", $package) -Secrets @($databasePassword, $packagePassword, $packagePassword) | Out-Null
    Invoke-Migration -Arguments @("verify", "--input", $package) -Secrets @($packagePassword) | Out-Null
    & (Join-Path $PostgresBin "createdb.exe") --host=127.0.0.1 --port=$port --username=farmaflow $targetDatabase
    if ($LASTEXITCODE -ne 0) { throw "Criação do banco de destino falhou." }
    Invoke-Migration -Arguments @("restore", "--input", $package, "--host", "127.0.0.1", "--port", $port.ToString(), "--database", $targetDatabase, "--username", "farmaflow", "--pg-bin", $PostgresBin) -Secrets @($packagePassword, $databasePassword) | Out-Null
    $restoreCheck = & (Join-Path $PostgresBin "psql.exe") --host=127.0.0.1 --port=$port --username=farmaflow --dbname=$targetDatabase --tuples-only --no-align --command="SELECT (SELECT count(*) FROM stores),(SELECT count(*) FROM label_templates),(SELECT count(*) FROM sales),(SELECT count(*) FROM local_media_blobs)"
    if ($LASTEXITCODE -ne 0) { throw "Validação do banco restaurado falhou." }
    if ($restoreCheck.Trim() -ne "1|2|1|0") { throw "Restauração/reconciliação incorreta: $restoreCheck" }
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    if (Test-Path (Join-Path $data "postmaster.pid")) {
        & (Join-Path $PostgresBin "pg_ctl.exe") stop --pgdata=$data --mode=immediate --wait | Out-Null
    }
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
