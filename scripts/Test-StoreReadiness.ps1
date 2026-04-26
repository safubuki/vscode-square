param(
    [string]$ProjectPath = ".\src\TurtleAIQuartetHub.Panel\TurtleAIQuartetHub.Panel.csproj",
    [string]$SolutionPath = ".\TurtleAIQuartetHub.sln",
    [string]$OutputRoot = (Join-Path $env:TEMP "turtle-ai-store-readiness"),
    [switch]$Publish,
    [string]$ReportPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$checks = [System.Collections.Generic.List[object]]::new()

function Resolve-RepoPath {
    param([string]$RelativePath)
    return Join-Path $repoRoot $RelativePath
}

function Add-Check {
    param(
        [string]$Area,
        [string]$Name,
        [ValidateSet("PASS", "WARN", "FAIL", "INFO")]
        [string]$Status,
        [string]$Detail
    )

    $script:checks.Add([pscustomobject]@{
        Area = $Area
        Check = $Name
        Status = $Status
        Detail = $Detail
    })
}

function Test-RequiredFile {
    param(
        [string]$Area,
        [string]$RelativePath
    )

    $path = Resolve-RepoPath $RelativePath
    if (Test-Path -LiteralPath $path) {
        Add-Check $Area $RelativePath "PASS" "存在します。"
    }
    else {
        Add-Check $Area $RelativePath "FAIL" "見つかりません。"
    }
}

function Invoke-DotNet {
    param(
        [string[]]$Arguments,
        [string]$Area,
        [string]$Name
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -eq 0) {
        Add-Check $Area $Name "PASS" "成功しました。"
    }
    else {
        Add-Check $Area $Name "FAIL" "終了コード $LASTEXITCODE で失敗しました。"
    }
}

Test-RequiredFile "基本ファイル" "README.md"
Test-RequiredFile "基本ファイル" "LICENSE.txt"
Test-RequiredFile "基本ファイル" "PRIVACY.md"
Test-RequiredFile "基本ファイル" "SUPPORT.md"
Test-RequiredFile "基本ファイル" "docs\store-readiness.md"
Test-RequiredFile "基本ファイル" "docs\store-listing-draft.md"
Test-RequiredFile "基本ファイル" "docs\release-notes-draft.md"
Test-RequiredFile "基本ファイル" "docs\telemetry-notes.md"
Test-RequiredFile "基本ファイル" "docs\msix-packaging-guide.md"
Test-RequiredFile "基本ファイル" "assets\store\README.md"
Test-RequiredFile "基本ファイル" "config\turtle-ai-quartet-hub.example.json"
Test-RequiredFile "基本ファイル" "src\TurtleAIQuartetHub.Panel\app.ico"
Test-RequiredFile "MSIX" "scripts\New-LocalMsixPackage.ps1"
Test-RequiredFile "MSIX" "src\TurtleAIQuartetHub.Package\Package.appxmanifest"
Test-RequiredFile "MSIX" "src\TurtleAIQuartetHub.Package\TurtleAIQuartetHub.Package.wapproj"
Test-RequiredFile "MSIX" "src\TurtleAIQuartetHub.Package\Assets\Square44x44Logo.png"
Test-RequiredFile "MSIX" "src\TurtleAIQuartetHub.Package\Assets\Square71x71Logo.png"
Test-RequiredFile "MSIX" "src\TurtleAIQuartetHub.Package\Assets\Square150x150Logo.png"
Test-RequiredFile "MSIX" "src\TurtleAIQuartetHub.Package\Assets\Square310x310Logo.png"
Test-RequiredFile "MSIX" "src\TurtleAIQuartetHub.Package\Assets\StoreLogo.png"
Test-RequiredFile "MSIX" "src\TurtleAIQuartetHub.Package\Assets\Wide310x150Logo.png"

$solutionFullPath = Resolve-RepoPath $SolutionPath
if (Test-Path -LiteralPath $solutionFullPath) {
    $solutionText = Get-Content -Raw -Encoding UTF8 $solutionFullPath
    if ($solutionText -match "src[\\/]+TurtleAIQuartetHub\.Panel[\\/]+TurtleAIQuartetHub\.Panel\.csproj") {
        Add-Check "ソリューション" "WPFプロジェクト登録" "PASS" "$SolutionPath に登録されています。"
    }
    else {
        Add-Check "ソリューション" "WPFプロジェクト登録" "FAIL" "$SolutionPath に WPF プロジェクトが見つかりません。"
    }
}
else {
    Add-Check "ソリューション" "ソリューションファイル" "FAIL" "$SolutionPath が見つかりません。"
}

$privacyPath = Resolve-RepoPath "PRIVACY.md"
if (Test-Path -LiteralPath $privacyPath) {
    $privacyText = Get-Content -Raw -Encoding UTF8 $privacyPath
    if ($privacyText -match "正式公開前|置き換えてください|草案") {
        Add-Check "プライバシー" "正式情報への差し替え" "WARN" "公開者名、連絡先、公開日などが草案のままです。"
    }
    else {
        Add-Check "プライバシー" "正式情報への差し替え" "PASS" "草案表現は見つかりませんでした。"
    }
}

$packagingFiles = @(Get-ChildItem -Path $repoRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.FullName -notmatch "\\(bin|obj)\\"
    } |
    Where-Object {
        $_.Name -like "*.wapproj" -or
        $_.Name -eq "Package.appxmanifest" -or
        $_.Name -like "*.appxmanifest"
    })

if ($packagingFiles.Count -gt 0) {
    Add-Check "MSIX" "Packaging Project / Package manifest" "PASS" (($packagingFiles | ForEach-Object { $_.FullName.Replace($repoRoot + "\", "") }) -join ", ")
}
else {
    Add-Check "MSIX" "Packaging Project / Package manifest" "WARN" "まだ追加されていません。Visual Studioで追加してください。"
}

$localMsixPath = Resolve-RepoPath "dist\msix-local\TurtleAIQuartetHub.msix"
if (Test-Path -LiteralPath $localMsixPath) {
    Add-Check "MSIX" "ローカル確認用 MSIX" "PASS" "生成済みです: $localMsixPath"

    $packageRootPath = Resolve-RepoPath "dist\msix-local\package-root"
    $pdbFiles = @()
    if (Test-Path -LiteralPath $packageRootPath) {
        $pdbFiles = @(Get-ChildItem -Path $packageRootPath -Recurse -Filter "*.pdb" -File -ErrorAction SilentlyContinue)
    }

    if ($pdbFiles.Count -eq 0) {
        Add-Check "MSIX" "PDB同梱" "PASS" "ローカルMSIX用 package-root にPDBは含まれていません。"
    }
    else {
        Add-Check "MSIX" "PDB同梱" "WARN" "$($pdbFiles.Count) 件のPDBが package-root に残っています。"
    }
}
else {
    Add-Check "MSIX" "ローカル確認用 MSIX" "INFO" ".\scripts\New-LocalMsixPackage.ps1 で生成できます。"
}

$storeImagesPath = Resolve-RepoPath "assets\store"
$storeImages = @()
if (Test-Path -LiteralPath $storeImagesPath) {
    $storeImages = @(Get-ChildItem -Path $storeImagesPath -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in ".png", ".jpg", ".jpeg" })
}

if ($storeImages.Count -gt 0) {
    Add-Check "Store画像" "スクリーンショット候補" "PASS" "$($storeImages.Count) 件見つかりました。"

    $imageValidationAvailable = $true
    try {
        Add-Type -AssemblyName System.Drawing
    }
    catch {
        $imageValidationAvailable = $false
        Add-Check "Store画像" "画像寸法チェック" "WARN" "System.Drawing を読み込めないため寸法チェックをスキップしました。"
    }

    if ($imageValidationAvailable) {
        foreach ($imageFile in $storeImages) {
            if ($imageFile.Length -gt 50MB) {
                Add-Check "Store画像" $imageFile.Name "WARN" "50 MBを超えています。"
                continue
            }

            $image = [System.Drawing.Image]::FromFile($imageFile.FullName)
            try {
                if ($image.Width -ge 1366 -and $image.Height -ge 768) {
                    Add-Check "Store画像" $imageFile.Name "PASS" "$($image.Width)x$($image.Height)、$([Math]::Round($imageFile.Length / 1MB, 2)) MB。"
                }
                else {
                    Add-Check "Store画像" $imageFile.Name "WARN" "$($image.Width)x$($image.Height) です。1366x768 以上を推奨します。"
                }
            }
            finally {
                $image.Dispose()
            }
        }
    }
}
else {
    Add-Check "Store画像" "スクリーンショット候補" "WARN" "公開用スクリーンショットはまだ置かれていません。"
}

$appcert = Get-Command appcert.exe -ErrorAction SilentlyContinue
if ($null -ne $appcert) {
    Add-Check "WACK" "appcert.exe" "PASS" $appcert.Source
}
else {
    $programFilesX86 = ${env:ProgramFiles(x86)}
    $appcertPath = if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
        ""
    }
    else {
        Join-Path $programFilesX86 "Windows Kits\10\App Certification Kit\appcert.exe"
    }

    if (-not [string]::IsNullOrWhiteSpace($appcertPath) -and (Test-Path -LiteralPath $appcertPath)) {
        Add-Check "WACK" "appcert.exe" "PASS" $appcertPath
    }
    else {
        Add-Check "WACK" "appcert.exe" "WARN" "Windows App Certification Kit がPATH上または既定位置に見つかりません。"
    }
}

$wackReportPath = Resolve-RepoPath "dist\msix-local\wack-report.xml"
if (Test-Path -LiteralPath $wackReportPath) {
    $wackReportText = Get-Content -Raw -Encoding UTF8 $wackReportPath
    if ($wackReportText -match 'OVERALL_RESULT="PASS"') {
        Add-Check "WACK" "開発用MSIX WACK結果" "PASS" "PASS: $wackReportPath"
    }
    else {
        Add-Check "WACK" "開発用MSIX WACK結果" "WARN" "レポートはありますがPASSではありません: $wackReportPath"
    }
}
else {
    Add-Check "WACK" "開発用MSIX WACK結果" "INFO" ".\scripts\New-LocalMsixPackage.ps1 -Sign -RunWack で確認できます。"
}

if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $buildOut = Join-Path $OutputRoot "build"
    New-Item -ItemType Directory -Force $buildOut | Out-Null
    $projectFullPath = Resolve-RepoPath $ProjectPath
    Invoke-DotNet -Arguments @("build", $projectFullPath, "-o", $buildOut) -Area "ビルド" -Name "dotnet build"

    $licenseInBuild = Join-Path $buildOut "LICENSE.txt"
    if (Test-Path -LiteralPath $licenseInBuild) {
        Add-Check "配布物" "LICENSE.txt 同梱" "PASS" "ビルド出力に含まれています。"
    }
    else {
        Add-Check "配布物" "LICENSE.txt 同梱" "FAIL" "ビルド出力に含まれていません。"
    }

    $configInBuild = Join-Path $buildOut "config\turtle-ai-quartet-hub.example.json"
    if (Test-Path -LiteralPath $configInBuild) {
        Add-Check "配布物" "設定例 同梱" "PASS" "ビルド出力に含まれています。"
    }
    else {
        Add-Check "配布物" "設定例 同梱" "FAIL" "ビルド出力に含まれていません。"
    }

    if ($Publish) {
        $publishOut = Join-Path $OutputRoot "publish-win-x64"
        New-Item -ItemType Directory -Force $publishOut | Out-Null
        Invoke-DotNet -Arguments @(
            "publish",
            $projectFullPath,
            "-c",
            "Release",
            "-r",
            "win-x64",
            "--self-contained",
            "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-o",
            $publishOut
        ) -Area "Publish" -Name "self-contained win-x64"
    }
    else {
        Add-Check "Publish" "self-contained win-x64" "INFO" "-Publish 指定時に確認します。"
    }
}
else {
    Add-Check "ビルド" "dotnet" "FAIL" "dotnet コマンドが見つかりません。"
}

$checks | Sort-Object Status, Area, Check | Format-Table -AutoSize

if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
    $reportFullPath = if ([System.IO.Path]::IsPathRooted($ReportPath)) {
        $ReportPath
    }
    else {
        Resolve-RepoPath $ReportPath
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# Store 公開準備チェック結果")
    $lines.Add("")
    $lines.Add("| 状態 | 分野 | チェック | 詳細 |")
    $lines.Add("|---|---|---|---|")
    foreach ($check in $checks) {
        $detail = $check.Detail.Replace("|", "\|")
        $lines.Add("| $($check.Status) | $($check.Area) | $($check.Check) | $detail |")
    }

    $reportDir = Split-Path -Parent $reportFullPath
    if (-not [string]::IsNullOrWhiteSpace($reportDir)) {
        New-Item -ItemType Directory -Force $reportDir | Out-Null
    }

    [System.IO.File]::WriteAllLines($reportFullPath, $lines, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Report: $reportFullPath"
}

$failCount = @($checks | Where-Object { $_.Status -eq "FAIL" }).Count
if ($failCount -gt 0) {
    exit 1
}
