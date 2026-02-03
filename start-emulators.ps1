# PowerShell script to start CosmosDB and Azurite emulators using Podman

Write-Host "Starting Aspire emulators with Podman..." -ForegroundColor Green

# Verify Podman is available
$podmanCmd = Get-Command podman -ErrorAction SilentlyContinue
if (-not $podmanCmd) {
    Write-Host "Error: Podman is not installed or not in PATH" -ForegroundColor Red
    exit 1
}

# Check if podman-compose is available
$podmanCompose = Get-Command podman-compose -ErrorAction SilentlyContinue
$useDirectPodman = $false

if ($podmanCompose) {
    Write-Host "Using podman-compose..." -ForegroundColor Yellow
    podman-compose up -d
    if ($LASTEXITCODE -ne 0) {
        Write-Host "podman-compose failed, falling back to direct Podman commands..." -ForegroundColor Yellow
        $useDirectPodman = $true
    }
}
else {
    Write-Host "podman-compose not found, using direct Podman commands..." -ForegroundColor Yellow
    $useDirectPodman = $true
}

if ($useDirectPodman) {
    Write-Host "Starting containers individually with Podman..." -ForegroundColor Yellow
    
    # Create network if it doesn't exist
    podman network create aspire-network 2>$null
    
    # Start CosmosDB emulator
    Write-Host "Starting CosmosDB emulator..." -ForegroundColor Cyan
    podman run -d `
        --name cosmosdb-emulator `
        --hostname cosmosdb-emulator `
        --network aspire-network `
        -p 8081:8081 `
        -p 10250:10250 `
        -p 10251:10251 `
        -p 10252:10252 `
        -p 10253:10253 `
        -p 10254:10254 `
        -p 10255:10255 `
        -e AZURE_COSMOS_EMULATOR_PARTITION_COUNT=10 `
        -e AZURE_COSMOS_EMULATOR_ENABLE_DATA_PERSISTENCE=true `
        -e AZURE_COSMOS_EMULATOR_IP_ADDRESS_OVERRIDE=127.0.0.1 `
        -v cosmosdb-data:/tmp/cosmosdb-emulator-data `
        mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest
    
    # Start Azurite
    Write-Host "Starting Azurite (Blob Storage emulator)..." -ForegroundColor Cyan
    podman run -d `
        --name azurite-blob-storage `
        --hostname azurite `
        --network aspire-network `
        -p 10000:10000 `
        -p 10001:10001 `
        -p 10002:10002 `
        -v azurite-data:/data `
        mcr.microsoft.com/azure-storage/azurite:latest `
        azurite --blobHost 0.0.0.0 --blobPort 10000 --queueHost 0.0.0.0 --queuePort 10001 --tableHost 0.0.0.0 --tablePort 10002 --location /data --debug /data/debug.log
    
    Write-Host "Emulators started!" -ForegroundColor Green
}

Start-Sleep -Seconds 5

Write-Host ""
Write-Host "Checking container status..." -ForegroundColor Yellow
podman ps --filter "name=cosmosdb-emulator" --filter "name=azurite-blob-storage"

Write-Host ""
Write-Host "Emulators are running!" -ForegroundColor Green
Write-Host "CosmosDB Explorer: http://localhost:8081/_explorer/index.html" -ForegroundColor Cyan
Write-Host "Azurite Blob Storage: http://localhost:10000" -ForegroundColor Cyan
