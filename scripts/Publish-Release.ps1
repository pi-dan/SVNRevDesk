[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [ValidateSet('Both', 'WithRuntime', 'WithoutRuntime')]
    [string]$Variant = 'Both'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $repositoryRoot "artifacts/releases/v$Version"
$project = Join-Path $repositoryRoot 'src/SVNRevDesk.Desktop/SVNRevDesk.Desktop.csproj'
$variants = @(
    @{ Name = 'without-runtime'; SelfContained = 'false'; Selection = 'WithoutRuntime'; Label = '不带运行时版'; Requirement = '需要安装 .NET 10 Desktop Runtime（Windows x64）。' },
    @{ Name = 'with-runtime'; SelfContained = 'true'; Selection = 'WithRuntime'; Label = '自带运行时版'; Requirement = '无需另外安装 .NET 运行时。' }
)
$checksums = [System.Collections.Generic.List[string]]::new()

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
    foreach ($item in $variants) {
        if ($Variant -ne 'Both' -and $Variant -ne $item.Selection) { continue }
        $packageName = "SVNRevDesk-v$Version-win-x64-$($item.Name)"
        $packageDirectory = Join-Path $releaseRoot $packageName
        # Publish to a new directory so personal data and old outputs cannot enter a package.
        if (Test-Path -LiteralPath $packageDirectory) {
            throw "发布目录已存在：$packageDirectory。请使用新的版本号，或先将该目录移走。"
        }
        $buildRoot = Join-Path $repositoryRoot ".build/publish/v$Version/$($item.Name)"
        dotnet publish $project -c Release -r win-x64 --self-contained $item.SelfContained `
            --artifacts-path $buildRoot -o $packageDirectory --nologo `
            "-p:ReleaseVersion=$Version" -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$($item.Name)" }
        if (!(Test-Path -LiteralPath (Join-Path $packageDirectory 'SVNRevDesk.exe'))) {
            throw '发布目录缺少 SVNRevDesk.exe。'
        }
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $packageDirectory
        Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md') -Destination $packageDirectory
        if ($item.SelfContained -eq 'true') {
            $runtimeConfigPath = Join-Path $buildRoot 'bin/SVNRevDesk.Desktop/release_win-x64/SVNRevDesk.runtimeconfig.json'
            $assetsPath = Join-Path $buildRoot 'obj/SVNRevDesk.Desktop/project.assets.json'
            $runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
            $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
            foreach ($framework in $runtimeConfig.runtimeOptions.includedFrameworks) {
                $packageId = "$($framework.name).Runtime.win-x64".ToLowerInvariant()
                $runtimePackage = $null
                foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
                    $candidate = Join-Path $folder "$packageId/$($framework.version)"
                    if (Test-Path -LiteralPath $candidate) { $runtimePackage = $candidate; break }
                }
                if (!$runtimePackage) { throw "Cannot find runtime license package: $packageId/$($framework.version)" }
                $licenseDirectory = Join-Path $packageDirectory "Licenses/$($framework.name)"
                New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
                $licenseFiles = @(Get-ChildItem -LiteralPath $runtimePackage -File |
                    Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' })
                if (!($licenseFiles | Where-Object Name -Match '^LICENSE')) { throw "Runtime license missing: $runtimePackage" }
                foreach ($licenseFile in $licenseFiles) {
                    Copy-Item -LiteralPath $licenseFile.FullName -Destination $licenseDirectory
                }
            }
        }
        $readme = @"
SVNRevDesk v$Version — $($item.Label)

$($item.Requirement)
两个发布版本功能完全相同，均内置 SVN 和 WinMerge。
支持 Windows 10 / 11 x64。将本目录完整解压到可写文件夹后运行 SVNRevDesk.exe。
首次使用 SVN / WinMerge 时，会自动在程序旁释放 Tools 文件。
设置、缓存、备份及 SVN 配置保存在程序目录，请在升级时保留。
记住的登录凭据加密保存在 Credentials 目录，升级时一并保留；仅原 Windows 用户可解密。
本机忽略规则单独保存在当前 Windows 用户配置中。
内置 SVN 的文件路径受 Windows 系统代码页限制，英文系统建议使用英文目录和文件名。

项目：https://github.com/pi-dan/SVNRevDesk
使用说明：https://github.com/pi-dan/SVNRevDesk/blob/main/docs/使用说明.md
反馈：https://github.com/pi-dan/SVNRevDesk/issues
第三方许可与对应 WinMerge 源码已嵌入程序，释放工具后位于 Tools 下。
"@
        Set-Content -LiteralPath (Join-Path $packageDirectory '使用说明.txt') -Value $readme -Encoding utf8
        $archive = Join-Path $releaseRoot "$packageName.zip"
        Compress-Archive -Path $packageDirectory -DestinationPath $archive -CompressionLevel Optimal
        $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksums.Add("$hash  $packageName.zip")
        $size = [math]::Round((Get-Item -LiteralPath $archive).Length / 1MB, 2)
        Write-Host "$($item.Label): $archive ($size MiB)"
    }
    # Always include every ZIP present when building variants in separate invocations.
    $checksums.Clear()
    foreach ($archive in Get-ChildItem -LiteralPath $releaseRoot -Filter '*.zip' -File | Sort-Object Name) {
        $checksums.Add("$((Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($archive.Name)")
    }
    Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Value $checksums -Encoding ascii
}
finally {
    Pop-Location
}
