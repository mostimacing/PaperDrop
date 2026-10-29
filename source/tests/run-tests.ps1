param(
    [string]$ArtifactsDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'test-artifacts')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework 4.x compiler was not found.'
}

$sourceDirectory = Split-Path -Parent $PSScriptRoot
$artifactRoot = [System.IO.Path]::GetFullPath($ArtifactsDirectory)
$testDirectory = Join-Path $artifactRoot ([Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
Write-Output "Test artifacts: $testDirectory"

$downloadSource = Join-Path $sourceDirectory 'DownloadEngine.cs'
$citationSource = Join-Path $sourceDirectory 'CitationEngine.cs'
$downloadTest = Join-Path $PSScriptRoot 'EngineTests.cs'
$citationTest = Join-Path $PSScriptRoot 'CitationTests.cs'
$downloadExecutable = Join-Path $testDirectory 'EngineTests.exe'
$citationExecutable = Join-Path $testDirectory 'CitationTests.exe'

Push-Location -LiteralPath $testDirectory
try {
    & $compiler /nologo /langversion:5 /target:exe /platform:anycpu /codepage:65001 "/out:$downloadExecutable" /r:System.Net.Http.dll /r:System.Web.Extensions.dll $downloadSource $downloadTest
    if ($LASTEXITCODE -ne 0) { throw 'Download test compilation failed.' }
    & $downloadExecutable
    if ($LASTEXITCODE -ne 0) { throw 'Download tests failed.' }

    & $compiler /nologo /langversion:5 /target:exe /platform:anycpu /codepage:65001 "/out:$citationExecutable" /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Xml.Linq.dll $downloadSource $citationSource $citationTest
    if ($LASTEXITCODE -ne 0) { throw 'Citation test compilation failed.' }
    & $citationExecutable
    if ($LASTEXITCODE -ne 0) { throw 'Citation tests failed.' }

    Write-Output 'All offline tests passed. The application executable was not modified.'
}
finally {
    Pop-Location
}
