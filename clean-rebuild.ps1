# PowerShell script to clean and rebuild the MAUI project
# This helps ensure icons and splash screens are properly regenerated

Write-Host "Cleaning ObdInsight project..." -ForegroundColor Cyan

# Navigate to the solution directory
$solutionDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $solutionDir

# Clean the project
Write-Host "`nCleaning bin and obj folders..." -ForegroundColor Yellow
Get-ChildItem -Path "." -Include bin,obj -Recurse -Directory | Remove-Item -Recurse -Force

# Clean MAUI intermediate files
$mauiIntermediateDir = Join-Path $env:USERPROFILE ".nuget\packages\.tools\maui"
if (Test-Path $mauiIntermediateDir) {
    Write-Host "Cleaning MAUI intermediate files..." -ForegroundColor Yellow
    Remove-Item -Path $mauiIntermediateDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "`nRestoring NuGet packages..." -ForegroundColor Yellow
dotnet restore

Write-Host "`nBuilding solution..." -ForegroundColor Yellow
dotnet build

Write-Host "`nClean and rebuild complete!" -ForegroundColor Green
Write-Host "`nIMPORTANT: For iOS, you should:" -ForegroundColor Cyan
Write-Host "1. Uninstall the app from your iPhone" -ForegroundColor White
Write-Host "2. Clean the build folder in Xcode (if using Mac)" -ForegroundColor White
Write-Host "3. Redeploy the app" -ForegroundColor White
Write-Host "`nThis ensures the cached icon and splash screen are cleared." -ForegroundColor Yellow
