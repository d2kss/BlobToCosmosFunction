# CosmosDB Emulator Network Access Setup
# Run from project root. Makes CosmosDB accessible from Azure Function and URL.

param(
    [switch]$SkipFirewall,
    [switch]$SkipRestart
)

$ErrorActionPreference = "Continue"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "CosmosDB Emulator Network Access Setup" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan

# Step 1: Get Host IP Address
Write-Host "`n[1/6] Getting host IP address..." -ForegroundColor Yellow
$hostIP = "localhost"
try {
    $adapters = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object {
        $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*"
    }
    if ($adapters) {
        $first = $adapters | Select-Object -First 1
        $hostIP = $first.IPAddress
        Write-Host "   OK Host IP: $hostIP" -ForegroundColor Green
    }
    else {
        Write-Host "   Using localhost" -ForegroundColor Gray
    }
}
catch {
    Write-Host "   Using localhost ($($_.Exception.Message))" -ForegroundColor Gray
}

# Step 2: Stop existing containers
if (-not $SkipRestart) {
    Write-Host "`n[2/6] Stopping existing CosmosDB container..." -ForegroundColor Yellow
    try {
        $out = podman ps -a --filter "name=cosmosdb-emulator" --format "{{.Names}}" 2>&1
        if ($out -and "$out".Trim() -eq "cosmosdb-emulator") {
            podman stop cosmosdb-emulator 2>&1 | Out-Null
            podman rm cosmosdb-emulator 2>&1 | Out-Null
            Write-Host "   OK Containers stopped" -ForegroundColor Green
        }
        else {
            Write-Host "   OK No container to stop" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "   Warning: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
else {
    Write-Host "`n[2/6] Skipping restart (-SkipRestart)" -ForegroundColor Yellow
}

# Step 3: Start container if not running
Write-Host "`n[3/6] Checking CosmosDB container..." -ForegroundColor Yellow
$running = $null
try {
    $running = podman ps --filter "name=cosmosdb-emulator" --format "{{.Names}}" 2>&1
}
catch { }

if (-not $running -or "$running".Trim() -ne "cosmosdb-emulator") {
    Write-Host "   Starting CosmosDB emulator..." -ForegroundColor Gray
    try {
        podman network create aspire-network 2>&1 | Out-Null
    }
    catch { }

    $cmd = @(
        "run", "-d",
        "--name", "cosmosdb-emulator",
        "--hostname", "cosmosdb-emulator",
        "--network", "aspire-network",
        "-p", "8081:8081",
        "-p", "10250:10250", "-p", "10251:10251", "-p", "10252:10252",
        "-p", "10253:10253", "-p", "10254:10254", "-p", "10255:10255",
        "-e", "AZURE_COSMOS_EMULATOR_PARTITION_COUNT=10",
        "-e", "AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE=true",
        "-v", "cosmosdb-data:/tmp/cosmosdb-emulator-data",
        "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest"
    )
    $runResult = & podman @cmd 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "   FAILED to start: $runResult" -ForegroundColor Red
        Write-Host "   Try: .\start-emulators.bat" -ForegroundColor Yellow
    }
    else {
        Write-Host "   OK Container started. Waiting 30s for init..." -ForegroundColor Green
        Start-Sleep -Seconds 30
    }
}
else {
    Write-Host "   OK CosmosDB emulator already running" -ForegroundColor Green
}

# Step 4: Verify
Write-Host "`n[4/6] Verifying..." -ForegroundColor Yellow
try {
    $ports = podman port cosmosdb-emulator 2>&1
    if ($ports -match "8081") {
        Write-Host "   OK Port 8081 exposed" -ForegroundColor Green
    }
    else {
        Write-Host "   Warning: Could not verify port" -ForegroundColor Yellow
    }
}
catch {
    Write-Host "   Warning: $($_.Exception.Message)" -ForegroundColor Yellow
}

# Step 5: Firewall (optional)
if (-not $SkipFirewall) {
    Write-Host "`n[5/6] Firewall (port 8081)..." -ForegroundColor Yellow
    try {
        $existing = Get-NetFirewallRule -DisplayName "CosmosDB Emulator" -ErrorAction SilentlyContinue
        if (-not $existing) {
            New-NetFirewallRule -DisplayName "CosmosDB Emulator" -Direction Inbound -LocalPort 8081 -Protocol TCP -Action Allow -ErrorAction Stop | Out-Null
            Write-Host "   OK Rule added" -ForegroundColor Green
        }
        else {
            Write-Host "   OK Rule exists" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "   Skip (run as Admin to add rule): $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
else {
    Write-Host "`n[5/6] Firewall skipped (-SkipFirewall)" -ForegroundColor Yellow
}

# Step 6: Test URL
Write-Host "`n[6/6] Testing URL..." -ForegroundColor Yellow
try {
    $r = Invoke-WebRequest -Uri "https://localhost:8081/_explorer/index.html" -SkipCertificateCheck -UseBasicParsing -TimeoutSec 15 -ErrorAction Stop
    Write-Host "   OK Localhost: $($r.StatusCode)" -ForegroundColor Green
}
catch {
    Write-Host "   Localhost test failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "   Emulator may still be starting. Wait 1 min and try: https://localhost:8081/_explorer/index.html" -ForegroundColor Yellow
}

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Configuration Complete" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "`nExplorer URL:" -ForegroundColor Yellow
Write-Host "  https://localhost:8081/_explorer/index.html" -ForegroundColor Cyan
if ($hostIP -ne "localhost") {
    Write-Host "  https://${hostIP}:8081/_explorer/index.html" -ForegroundColor Cyan
}
Write-Host "`nConnection string (local.settings.json):" -ForegroundColor Yellow
Write-Host '  "CosmosDBConnection": "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;"' -ForegroundColor White
if ($hostIP -ne "localhost") {
    Write-Host "`nFor remote machine use:" -ForegroundColor Yellow
    Write-Host "  AccountEndpoint=https://${hostIP}:8081/" -ForegroundColor White
}
Write-Host "`n========================================" -ForegroundColor Cyan
