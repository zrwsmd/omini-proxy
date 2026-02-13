$ErrorActionPreference = "Stop"

$dotnet = Join-Path $PSScriptRoot ".dotnet\\dotnet.exe"
$project = Join-Path $PSScriptRoot "src\\WowProxy.App\\WowProxy.App.csproj"
$outDir = Join-Path $PSScriptRoot "dist\\WowProxy"

New-Item -ItemType Directory -Force $outDir | Out-Null

$publishArgs = @(
  "publish",
  $project,
  "-c", "Release",
  "-r", "win-x64",
  "-o", $outDir,
  "/p:SelfContained=true",
  "/p:PublishSingleFile=true",
  "/p:IncludeNativeLibrariesForSelfExtract=true",
  "/p:PublishTrimmed=false",
  "--",
  "/m:1",
  "/nr:false",
  "/p:BuildInParallel=false"
)

for ($i = 0; $i -lt 3; $i++) {
  Start-Sleep -Seconds (2 + $i * 3)
  & $dotnet @publishArgs
  if ($LASTEXITCODE -eq 0) {
    break
  }
  if ($i -eq 2) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
  }
}

$fileCount = (Get-ChildItem -Force $outDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
if ($fileCount -eq 0) {
  throw "dotnet publish succeeded but output is empty: $outDir"
}

Write-Host "Published to: $outDir"
