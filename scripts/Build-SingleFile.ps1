[CmdletBinding()]
param(
    # Use existing restored packages, for example when building offline.
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src/SVNRevDesk.Desktop/SVNRevDesk.Desktop.csproj'
$releaseRoot = Join-Path $repositoryRoot 'artifacts/releases'
$outputFile = Join-Path $releaseRoot 'SVNRevDesk.exe'
# A fresh staging directory prevents old output or application data from being published.
$stagingDirectory = Join-Path $repositoryRoot ('.build/single-file/' + [Guid]::NewGuid().ToString('N'))

Push-Location $repositoryRoot
try {
    $publishArguments = @(
        'publish', $project, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'false', '-o', $stagingDirectory, '--nologo',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:UseSharedCompilation=false'
    )
    if ($NoRestore) { $publishArguments += '--no-restore' }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码：$LASTEXITCODE。原发布文件未更新。" }

    $files = @(Get-ChildItem -LiteralPath $stagingDirectory -File -Recurse)
    $executable = Join-Path $stagingDirectory 'SVNRevDesk.exe'
    if ($files.Count -ne 1 -or !(Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "发布结果不是预期的单文件，请检查：$stagingDirectory。原发布文件未更新。"
    }
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    Copy-Item -LiteralPath $executable -Destination $outputFile -Force
    $version = (Get-Item -LiteralPath $outputFile).VersionInfo.ProductVersion
    Write-Host "已生成不带运行时的单文件（$version）：$outputFile"
    Write-Host '运行需要 .NET 10 Desktop Runtime（Windows x64）。'
}
finally {
    Pop-Location
}
