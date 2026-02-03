# PowerShell script to configure CosmosDB Emulator for network access
# This makes it accessible from Azure Functions and via URL from other systems

param(
    [switch]$SkipFirewall,
    [switch]$SkipRestart
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "CosmosDB Emulator Network Access Setup" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan

# Step 1: Get Host IP Address
Write-Host "`n[1/6] Getting host IP address..." -ForegroundColor Yellow
$ipAddresses = Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
    $_.IPAddress -notlike "127.*" -and 
    $_.IPAddress -notlike "169.254.*" -and
    $_.PrefixOrigin -ne "WellKnown"
} | Select-Object IPAddress, InterfaceAlias

if ($ipAddresses) {
    $hostIP = $ipAddresses[0].IPAddress
    Write-Host "   ✓ Host IP: $hostIP ($($ipAddresses[0].InterfaceAlias))" -ForegroundColor Green
} else {
    $hostIP = "localhost"
    Write-Host "   ⚠ Using localhost (no network IP found)" -ForegroundColor Yellow
}

# Step 2: Stop existing containers
if (-not $SkipRestart) {
    Write-Host "`n[2/6] Stopping existing containers..." -ForegroundColor Yellow
    $existingCosmos = podman ps -a --filter "name=cosmosdb-emulator" --format "{{.Names}}" 2>$null
    if ($existingCosmos) {
        Write-Host "   Stopping cosmosdb-emulator..." -ForegroundColor Gray
        podman stop cosmosdb-emulator 2>$null | Out-Null
        podman rm cosmosdb-emulator 2>$null | Out-Null
        Write-Host "   ✓ Containers stopped" -ForegroundColor Green
    } else {
        Write-Host "   ✓ No existing containers to stop" -ForegroundColor Green
    }
} else {
    Write-Host "`n[2/6] Skipping container restart (using -SkipRestart)" -ForegroundColor Yellow
}

# Step 3: Check if container is running
Write-Host "`n[3/6] Checking container status..." -ForegroundColor Yellow
$running = podman ps --filter "name=cosmosdb-emulator" --format "{{.Names}}" 2>$null
if (-not $running) {
    Write-Host "   ⚠ CosmosDB emulator is not running" -ForegroundColor Yellow
    Write-Host "   Starting CosmosDB emulator with network access..." -ForegroundColor Gray
    
    # Create network if it doesn't exist
    podman network create aspire-network 2>$null | Out-Null
    
    # Start CosmosDB emulator WITHOUT IP_ADDRESS_OVERRIDE
    Write-Host "   Starting container..." -ForegroundColor Gray
    $result = podman run -d `
        --name cosmosdb-emulator `
        --hostname cosmosdb-emulator `
        --network aspire-network `
        -p 0.0.0.0:8081:8081 `
        -p 0.0.0.0:10250:10250 `
        -p 0.0.0.0:10251:10251 `
        -p 0.0.0.0:10252:10252 `
        -p 0.0.0.0:10253:10253 `
        -p 0.0.0.0:10254:10254 `
        -p 0.0.0.0:10255:10255 `
        -e AZURE_COSMOS_EMULATOR_PARTITION_COUNT=10 `
        -e AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE=true `
        -v cosmosdb-data:/tmp/cosmosdb-emulator-data `
        mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest 2>&1
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "   ✓ CosmosDB emulator started successfully" -ForegroundColor Green
        Write-Host "   Waiting for emulator to initialize (30 seconds)..." -ForegroundColor Gray
        Start-Sleep -Seconds 30
    } else {
        Write-Host "   ✗ Failed to start container: $result" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "   ✓ CosmosDB emulator is already running" -ForegroundColor Green
}

# Step 4: Verify container configuration
Write-Host "`n[4/6] Verifying container configuration..." -ForegroundColor Yellow
$containerInfo = podman inspect cosmosdb-emulator --format "{{range .NetworkSettings.Ports}}{{.}}{{end}}" 2>$null
$ports = podman port cosmosdb-emulator 2>$null

if ($ports -match "0.0.0.0:8081") {
    Write-Host "   ✓ Port 8081 is bound to 0.0.0.0 (all interfaces)" -ForegroundColor Green
} else {
    Write-Host "   ⚠ Port binding may be restricted" -ForegroundColor Yellow
}

# Check environment variables
$envVars = podman inspect cosmosdb-emulator --format "{{range .Config.Env}}{{println .}}{{end}}" 2>$null
if ($envVars -match "IP_ADDRESS_OVERRIDE") {
    Write-Host "   ⚠ Warning: IP_ADDRESS_OVERRIDE is set (may restrict access)" -ForegroundColor Yellow
} else {
    Write-Host "   ✓ No IP address override (network access enabled)" -ForegroundColor Green
}

# Step 5: Configure Firewall
if (-not $SkipFirewall) {
    Write-Host "`n[5/6] Configuring Windows Firewall..." -ForegroundColor Yellow
    $firewallRule = Get-NetFirewallRule -DisplayName "CosmosDB Emulator" -ErrorAction SilentlyContinue
    if (-not $firewallRule) {
        try {
            New-NetFirewallRule `
                -DisplayName "CosmosDB Emulator" `
                -Direction Inbound `
                -LocalPort 8081 `
                -Protocol TCP `
                -Action Allow `
                -Description "Allow inbound traffic for CosmosDB Emulator" | Out-Null
            Write-Host "   ✓ Firewall rule created for port 8081" -ForegroundColor Green
        } catch {
            Write-Host "   ⚠ Could not create firewall rule: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "   You may need to run as Administrator" -ForegroundColor Gray
        }
    } else {
        Write-Host "   ✓ Firewall rule already exists" -ForegroundColor Green
    }
} else {
    Write-Host "`n[5/6] Skipping firewall configuration (using -SkipFirewall)" -ForegroundColor Yellow
}

# Step 6: Test Connectivity
Write-Host "`n[6/6] Testing connectivity..." -ForegroundColor Yellow

# Test localhost
Write-Host "   Testing localhost..." -ForegroundColor Gray
try {
    $localTest = Invoke-WebRequest -Uri "https://localhost:8081/_explorer/index.html" -SkipCertificateCheck -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
    Write-Host "   ✓ Localhost access: OK (Status: $($localTest.StatusCode))" -ForegroundColor Green
} catch {
    Write-Host "   ✗ Localhost access failed: $($_.Exception.Message)" -ForegroundColor Red
}

# Test network IP if different from localhost
if ($hostIP -ne "localhost" -and $hostIP -ne "127.0.0.1") {
    Write-Host "   Testing network IP ($hostIP)..." -ForegroundColor Gray
    try {
        $networkTest = Invoke-WebRequest -Uri "https://$hostIP:8081/_explorer/index.html" -SkipCertificateCheck -UseBasicParsing -TimeoutSec 10 -ErrorAction Stop
        Write-Host "   ✓ Network IP access: OK (Status: $($networkTest.StatusCode))" -ForegroundColor Green
    } catch {
        Write-Host "   ⚠ Network IP access failed: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "   This may be normal if testing from the same machine" -ForegroundColor Gray
    }
}

# Display Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Configuration Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan

Write-Host "`nConnection Information:" -ForegroundColor Yellow
Write-Host "  Host IP Address: $hostIP" -ForegroundColor White

Write-Host "`nExplorer URLs:" -ForegroundColor Yellow
Write-Host "  Local:    https://localhost:8081/_explorer/index.html" -ForegroundColor Cyan
if ($hostIP -ne "localhost" -and $hostIP -ne "127.0.0.1") {
    Write-Host "  Network:  https://$hostIP:8081/_explorer/index.html" -ForegroundColor Cyan
}

Write-Host "`nConnection Strings:" -ForegroundColor Yellow
Write-Host "  For local function (same machine):" -ForegroundColor Gray
Write-Host "    AccountEndpoint=https://localhost:8081/" -ForegroundColor White

if ($hostIP -ne "localhost" -and $hostIP -ne "127.0.0.1") {
    Write-Host "`n  For function on another machine:" -ForegroundColor Gray
    Write-Host "    AccountEndpoint=https://$hostIP:8081/" -ForegroundColor White
}

Write-Host "`n  Account Key (same for all):" -ForegroundColor Gray
Write-Host "    C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==" -ForegroundColor White

Write-Host "`nFull Connection String (for local.settings.json):" -ForegroundColor Yellow
$localConnectionString = "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;"
Write-Host "  `"CosmosDBConnection`: `"$localConnectionString`"" -ForegroundColor White

if ($hostIP -ne "localhost" -and $hostIP -ne "127.0.0.1") {
    Write-Host "`nFull Connection String (for remote function):" -ForegroundColor Yellow
    $remoteConnectionString = "AccountEndpoint=https://$hostIP:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;"
    Write-Host "  `"CosmosDBConnection`: `"$remoteConnectionString`"" -ForegroundColor White
}

Write-Host "`nNext Steps:" -ForegroundColor Yellow
Write-Host "  1. Update local.settings.json with connection string above" -ForegroundColor Gray
Write-Host "  2. Restart your Azure Function" -ForegroundColor Gray
Write-Host "  3. Open Explorer URL in browser (accept certificate warning)" -ForegroundColor Gray
Write-Host "  4. Check function logs for 'Successfully connected to CosmosDB'" -ForegroundColor Gray

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "✓ Setup Complete!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
