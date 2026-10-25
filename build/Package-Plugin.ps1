# Stages the distributable plugin layout:
#   artifacts/package/Plugins/JavaScriptEventRegistrationAuditor.dll
#   artifacts/package/Plugins/JavaScriptEventRegistrationAuditor/<dependency dlls>
# Then (if nuget.exe is on PATH) creates the .nupkg with lib/net48/Plugins/ layout.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$name = 'JavaScriptEventRegistrationAuditor'
$out  = Join-Path $root "src/$name/bin/Release"
if (-not (Test-Path $out)) { dotnet build (Join-Path $root 'JavaScriptEventRegistrationAuditor.sln') -c Release }
$stage = Join-Path $root 'artifacts/package'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
$plugins = Join-Path $stage 'Plugins'; $deps = Join-Path $plugins $name
New-Item -ItemType Directory -Force $deps | Out-Null
Copy-Item (Join-Path $out "$name.dll") $plugins
# Libraries already shipped by XrmToolBox / Dataverse SDK must NOT be duplicated.
$exclude = 'XrmToolBox*','McTools*','Microsoft.Xrm*','Microsoft.Crm*','Newtonsoft.Json*','Microsoft.IdentityModel*','Microsoft.Identity*',"$name.dll"
Get-ChildItem $out -Filter *.dll | Where-Object { $n = $_.Name; -not ($exclude | Where-Object { $n -like $_ }) } | Copy-Item -Destination $deps
Write-Host "Staged at $stage"
if (Get-Command nuget -ErrorAction SilentlyContinue) {
  nuget pack (Join-Path $PSScriptRoot "$name.nuspec") -BasePath $stage -OutputDirectory (Join-Path $root 'artifacts')
}
