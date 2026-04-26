param(
    [string]$ProjectPath = ".\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj",
    [string]$PackageManifestPath = ".\src\TurtleAIQuartetHub.Package\Package.appxmanifest",
    [string]$OutputRoot = ".\dist\msix-local",
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$CertificateSubject = "CN=TurtleAIQuartetHubDev",
    [switch]$Sign,
    [switch]$RunWack,
    [switch]$IncludeSymbols
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

function Resolve-RepoPath {
    param([string]$Path)
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return $Path
    }

    return Join-Path $repoRoot $Path
}

function Find-WindowsKitTool {
    param([string]$ToolName)

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        if ($ToolName -eq "appcert.exe") {
            $appCertPath = Join-Path $programFilesX86 "Windows Kits\10\App Certification Kit\appcert.exe"
            if (Test-Path -LiteralPath $appCertPath) {
                return $appCertPath
            }
        }

        $candidate = Get-ChildItem -Path (Join-Path $programFilesX86 "Windows Kits\10\bin") -Recurse -Filter $ToolName -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match "\\x64\\$([regex]::Escape($ToolName))$" } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

        if ($candidate) {
            return $candidate.FullName
        }
    }

    $command = Get-Command $ToolName -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    throw "$ToolName が見つかりません。Windows SDK をインストールしてください。"
}

function Reset-Directory {
    param(
        [string]$Path,
        [string]$AllowedRoot
    )

    $resolvedRoot = [System.IO.Path]::GetFullPath($AllowedRoot)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "安全確認に失敗しました。削除対象が出力ルート外です: $resolvedPath"
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }

    New-Item -ItemType Directory -Force $resolvedPath | Out-Null
}

$projectFullPath = Resolve-RepoPath $ProjectPath
$manifestFullPath = Resolve-RepoPath $PackageManifestPath
$outputFullPath = Resolve-RepoPath $OutputRoot
$publishPath = Join-Path $outputFullPath "publish"
$packageRoot = Join-Path $outputFullPath "package-root"
$packagePath = Join-Path $outputFullPath "TurtleAIQuartetHub.msix"

if (-not (Test-Path -LiteralPath $projectFullPath)) {
    throw "WPFプロジェクトが見つかりません: $projectFullPath"
}

if (-not (Test-Path -LiteralPath $manifestFullPath)) {
    throw "Package.appxmanifest が見つかりません: $manifestFullPath"
}

New-Item -ItemType Directory -Force $outputFullPath | Out-Null
Reset-Directory -Path $publishPath -AllowedRoot $outputFullPath
Reset-Directory -Path $packageRoot -AllowedRoot $outputFullPath

dotnet publish $projectFullPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish が失敗しました。"
}

Copy-Item -Path (Join-Path $publishPath "*") -Destination $packageRoot -Recurse -Force

if (-not $IncludeSymbols) {
    Get-ChildItem -Path $packageRoot -Recurse -Filter "*.pdb" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

Copy-Item -LiteralPath $manifestFullPath -Destination (Join-Path $packageRoot "AppxManifest.xml") -Force

$packageAssetsSource = Join-Path (Split-Path -Parent $manifestFullPath) "Assets"
$packageAssetsTarget = Join-Path $packageRoot "Assets"
Copy-Item -Path $packageAssetsSource -Destination $packageAssetsTarget -Recurse -Force

$makeAppx = Find-WindowsKitTool "makeappx.exe"
if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

& $makeAppx pack /d $packageRoot /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "makeappx pack が失敗しました。"
}

if ($Sign) {
    $signTool = Find-WindowsKitTool "signtool.exe"
    $cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $CertificateSubject -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if (-not $cert) {
        $cert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $CertificateSubject `
            -KeyUsage DigitalSignature `
            -FriendlyName "Turtle AI Code Quartet Hub Dev MSIX" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension "2.5.29.37={text}1.3.6.1.5.5.7.3.3"
    }

    & $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "signtool sign が失敗しました。"
    }
}

if ($RunWack) {
    $appCert = Find-WindowsKitTool "appcert.exe"
    $reportPath = Join-Path $outputFullPath "wack-report.xml"
    & $appCert reset
    & $appCert test -appxpackagepath $packagePath -reportoutputpath $reportPath
    if ($LASTEXITCODE -ne 0) {
        throw "Windows App Certification Kit が失敗しました。レポート: $reportPath"
    }
}

Write-Host "MSIX: $packagePath"
Write-Host "Signed: $Sign"
Write-Host "IncludeSymbols: $IncludeSymbols"
