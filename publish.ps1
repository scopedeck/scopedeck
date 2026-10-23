# Builds "ScopeDeck.exe" (a single file) into .\dist.
# -SelfContained bundles the .NET runtime (~100 MB) so the exe also runs on PCs without .NET 10 installed.
param([switch]$SelfContained)

$ErrorActionPreference = "Stop"
$out = Join-Path $PSScriptRoot "dist"
dotnet publish (Join-Path $PSScriptRoot "src/Siglent.Desktop") -c Release -o $out --self-contained:$SelfContained
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-ChildItem $out | Format-Table Name, @{ n = "Size (MB)"; e = { [math]::Round($_.Length / 1MB, 1) } }
