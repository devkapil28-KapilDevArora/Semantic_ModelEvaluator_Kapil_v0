param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
if ($Runtime -ne 'win-x64') { throw 'Only win-x64 is release-verified in V0.' }
$root = Split-Path $PSScriptRoot
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$destination = Join-Path $root "artifacts\package-$stamp"
$archive = Join-Path $root "artifacts\Semantic_ModelEvaluator_Kapil_V0-$Runtime-$stamp.zip"
if (Test-Path -LiteralPath $destination) { throw 'Package directory already exists; refusing to overwrite it.' }
dotnet publish "$root\src\SemanticModelEvaluator.App\SemanticModelEvaluator.App.csproj" -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -o $destination --verbosity quiet --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath "$root\README.md" -Destination $destination
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $destination | Select-Object -ExpandProperty FullName) -DestinationPath $archive
$hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Output "Application: $destination\Semantic_ModelEvaluator_Kapil_V0.exe"
Write-Output "Package: $archive"
Write-Output "SHA256: $($hash.Hash)"
