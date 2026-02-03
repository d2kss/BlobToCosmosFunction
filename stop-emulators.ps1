# PowerShell script to stop CosmosDB and Azurite emulators

Write-Host "Stopping Aspire emulators..." -ForegroundColor Yellow

# Check for running containers
$cosmosRunning = podman ps --filter "name=cosmosdb-emulator" --format "{{.Names}}" 2>$null
$azuriteRunning = podman ps --filter "name=azurite-blob-storage" --format "{{.Names}}" 2>$null

if ($cosmosRunning -or $azuriteRunning) {
    Write-Host "Stopping containers..." -ForegroundColor Yellow
    podman stop cosmosdb-emulator azurite-blob-storage 2>$null
    
    Write-Host "Removing containers..." -ForegroundColor Yellow
    podman rm cosmosdb-emulator azurite-blob-storage 2>$null
    
    Write-Host "Emulators stopped and removed!" -ForegroundColor Green
} else {
    Write-Host "No running emulator containers found." -ForegroundColor Yellow
    
    # Check for stopped containers
    $cosmosStopped = podman ps -a --filter "name=cosmosdb-emulator" --format "{{.Names}}" 2>$null
    $azuriteStopped = podman ps -a --filter "name=azurite-blob-storage" --format "{{.Names}}" 2>$null
    
    if ($cosmosStopped -or $azuriteStopped) {
        Write-Host "Removing stopped containers..." -ForegroundColor Yellow
        podman rm cosmosdb-emulator azurite-blob-storage 2>$null
        Write-Host "Stopped containers removed!" -ForegroundColor Green
    } else {
        Write-Host "No emulator containers found." -ForegroundColor Green
    }
}

# Try compose tools as fallback
$podmanCompose = Get-Command podman-compose -ErrorAction SilentlyContinue
if ($podmanCompose) {
    Write-Host "Cleaning up with podman-compose..." -ForegroundColor Yellow
    podman-compose down 2>$null
}
