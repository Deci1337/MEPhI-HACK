# Build Android from C:\Temp to avoid "Permission denied" when project is in Desktop/OneDrive
$src = "c:\Users\HONOR\Desktop\mifi-hack"
$dst = "C:\Temp\mifi-hack"
Write-Host "Copying to $dst..."
if (Test-Path $dst) { Remove-Item $dst -Recurse -Force -ErrorAction SilentlyContinue }
robocopy $src $dst /E /XD .vs bin obj .git /NFL /NDL /NJH /NJS
Write-Host "Building Android..."
Push-Location "$dst\MassangerMaximka"
dotnet build MassangerMaximka/MassangerMaximka.csproj -f net9.0-android
$r = $LASTEXITCODE
Pop-Location
if ($r -eq 0) { Write-Host "APK: $dst\MassangerMaximka\MassangerMaximka\bin\Debug\net9.0-android\" }
exit $r
