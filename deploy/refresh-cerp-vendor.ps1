# Refreshes vendor/c-erp (the copy of C-ERP's Domain + Infrastructure that Render builds against)
# from the live checkout. C-ERP itself is only read. Run, then commit and push:
#   powershell -ExecutionPolicy Bypass -File deploy\refresh-cerp-vendor.ps1
param([string]$From = "D:\C-ERP\C_ERP-main\src")
$root = Split-Path -Parent $PSScriptRoot
foreach ($p in 'AegisErp.Domain', 'AegisErp.Infrastructure') {
    $dst = Join-Path $root "vendor\c-erp\src\$p"
    robocopy (Join-Path $From $p) $dst /MIR /XD bin obj .vs /XF *.user *.db *.db-shm *.db-wal /NFL /NDL /NJH /NJS /NP | Out-Null
}
$commit = git -C (Split-Path $From) rev-parse --short HEAD
Set-Content (Join-Path $root "vendor\c-erp\SOURCE.txt") "Copied from C-ERP commit $commit on $(Get-Date -Format 'yyyy-MM-dd')"
Write-Host "vendor/c-erp refreshed from C-ERP $commit"
