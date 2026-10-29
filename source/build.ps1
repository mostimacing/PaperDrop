$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw '.NET Framework 4.x compiler was not found.'
}
$outputFile = Join-Path (Split-Path -Parent $PSScriptRoot) 'PaperDrop.exe'
$sourceFiles = @('Program.cs', 'DownloadEngine.cs', 'CitationEngine.cs', 'AssemblyInfo.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$outputFile" "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$PSScriptRoot\app.ico" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll /r:System.Xml.Linq.dll $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $outputFile"
