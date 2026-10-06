# Check service status
Write-Host "=== FRMC Service Status ==="
$service = Get-Service -Name 'FireRakshakFRMC' -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "Service Name: $($service.Name)"
    Write-Host "Status: $($service.Status)"
    Write-Host "Start Type: $($service.StartType)"
} else {
    Write-Host "Service not found"
}

# Check event log
Write-Host ""
Write-Host "=== Recent Service Control Manager Events ==="
$events = Get-WinEvent -LogName 'System' -MaxEvents 50 | Where-Object { $_.ProviderName -eq 'Service Control Manager' }
$frmcEvents = $events | Where-Object { $_.Message -like '*FireRakshak*' -or $_.Message -like '*FRMC*' }
foreach ($event in $frmcEvents) {
    Write-Host "[$($event.TimeCreated)] $($event.Message)"
    Write-Host "---"
}

# Check installed files
Write-Host ""
Write-Host "=== Installed Files ==="
$installPath = 'C:\Program Files\Fire Rakshak\FRMC'
if (Test-Path $installPath) {
    $files = Get-ChildItem $installPath -File
    Write-Host "Total files: $($files.Count)"
    Write-Host "Executable exists: $(Test-Path (Join-Path $installPath 'FireRakshak.FRMC.Host.exe'))"
} else {
    Write-Host "Installation folder not found"
}
