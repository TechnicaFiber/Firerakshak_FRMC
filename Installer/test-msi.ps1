$msiPath = 'C:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL\Installer\bin\Release\FRMC.Installer.msi'

Write-Host "Testing MSI: $msiPath"
Write-Host "File Size: $((Get-Item $msiPath).Length) bytes"
Write-Host ""

try {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msiPath, 0))

    # Get Product Info
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT Property, Value FROM Property WHERE Property IN ('ProductName', 'ProductVersion', 'Manufacturer')"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)

    Write-Host "=== Product Information ==="
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    while ($record -ne $null) {
        $prop = $record.GetType().InvokeMember('StringData', 'Propertyget', $null, $record, 1)
        $val = $record.GetType().InvokeMember('StringData', 'Propertyget', $null, $record, 2)
        Write-Host "$prop : $val"
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)

    # Get File List
    Write-Host ""
    Write-Host "=== Files in MSI ==="
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT FileName FROM File"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)

    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    $fileCount = 0
    while ($record -ne $null) {
        $filename = $record.GetType().InvokeMember('StringData', 'Propertyget', $null, $record, 1)
        Write-Host "  - $filename"
        $fileCount++
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)

    Write-Host ""
    Write-Host "Total files: $fileCount"

    # Get Component count
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT Component FROM Component"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)

    $compCount = 0
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    while ($record -ne $null) {
        $compCount++
        $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    }
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)

    Write-Host "Total components: $compCount"

    Write-Host ""
    Write-Host "MSI validation: SUCCESS"

} catch {
    Write-Host "Error: $_"
    Write-Host "MSI validation: FAILED"
}
