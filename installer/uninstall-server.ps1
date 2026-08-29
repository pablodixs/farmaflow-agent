$ErrorActionPreference = "SilentlyContinue"
Stop-Service "FarmaFlowServer" -Force
& sc.exe delete "FarmaFlowServer" | Out-Null
Stop-Service "FarmaFlowPostgreSQL" -Force
& sc.exe delete "FarmaFlowPostgreSQL" | Out-Null
Remove-NetFirewallRule -DisplayName "FarmaFlow Server HTTPS"
Write-Host "Os dados e backups foram preservados em $env:ProgramData\FarmaFlow\Server."
