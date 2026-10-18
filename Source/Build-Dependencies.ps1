#requires -Version 7.0
<#
.SYNOPSIS
    RimAgent 依賴組件（SmashTools / DevTools）可重現取得與建置腳本。
.DESCRIPTION
    1. 從 upstream 或本機快取取得 SmashPhil/SmashTools 與 SmashPhil/DevTools 固定 commit 到 repo .scratch 目錄。
    2. 自動產生適用於 .NET 10 與 RimWorld 1.7 的 Directory.Build.props，不需手動修改 .scratch 原始碼。
    3. 以 dotnet build 建置 net10.0 組件（CoreLib.dll、SmashTools.dll、DevTools.dll）。
    4. 輸出至 Source/Libs/SmashTools 與 Source/Libs/DevTools，並保留 upstream MIT 授權條款。
    5. 記錄實際建置產物之 SHA-256，確認複製後與建置來源一致。
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$RimWorldDir = "",

    [Parameter(Mandatory = $false)]
    [string]$WorkshopDir = "",

    [Parameter(Mandatory = $false)]
    [string]$HarmonyDll = "",

    [Parameter(Mandatory = $false)]
    [string]$ScratchDir = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputDir = "",

    [Parameter(Mandatory = $false)]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Assert-SourceCommit([string]$Directory, [string]$Commit) {
    $actual = & git -C $Directory rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or "$actual".Trim() -ne $Commit) {
        throw "來源版本不符：$Directory，應為 $Commit"
    }
    & git -C $Directory diff --quiet HEAD -- '*.cs' '*.csproj'
    if ($LASTEXITCODE -ne 0) {
        throw "來源包含未提交的 C# 或專案修改：$Directory"
    }
}

# 1. 解析基礎目錄路徑
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$RepoRoot = Resolve-Path (Join-Path $ScriptDir "..")

if ([string]::IsNullOrWhiteSpace($RimWorldDir)) {
    $CandidateRw = Resolve-Path (Join-Path $RepoRoot "..\..") -ErrorAction SilentlyContinue
    if ($CandidateRw -and (Test-Path (Join-Path $CandidateRw "Assembly-CSharp.dll"))) {
        $RimWorldDir = $CandidateRw.Path
    } else {
        $RimWorldDir = "C:\Program Files (x86)\Steam\steamapps\common\RimWorld"
    }
}
$RimWorldDir = [System.IO.Path]::GetFullPath($RimWorldDir)
if (-not (Test-Path (Join-Path $RimWorldDir "Assembly-CSharp.dll"))) {
    throw "找不到 RimWorld 1.7 遊戲根目錄：$RimWorldDir"
}

if ([string]::IsNullOrWhiteSpace($WorkshopDir)) {
    $CandidateWs = Resolve-Path (Join-Path $RimWorldDir "..\..\workshop\content\294100") -ErrorAction SilentlyContinue
    if ($CandidateWs -and (Test-Path $CandidateWs)) {
        $WorkshopDir = $CandidateWs.Path
    } else {
        $WorkshopDir = "C:\Program Files (x86)\Steam\steamapps\workshop\content\294100"
    }
}
$WorkshopDir = [System.IO.Path]::GetFullPath($WorkshopDir)

if ([string]::IsNullOrWhiteSpace($HarmonyDll)) {
    $CandidateHarmony = Join-Path $WorkshopDir "2009463077\1.7\Assemblies\0Harmony.dll"
    if (Test-Path $CandidateHarmony) {
        $HarmonyDll = $CandidateHarmony
    } else {
        $CandidateHarmony = Join-Path $RimWorldDir "0Harmony.dll"
        if (Test-Path $CandidateHarmony) {
            $HarmonyDll = $CandidateHarmony
        }
    }
}

if ([string]::IsNullOrWhiteSpace($ScratchDir)) {
    $ScratchDir = Join-Path $RepoRoot ".scratch"
}
$ScratchDir = [System.IO.Path]::GetFullPath($ScratchDir)
if (-not (Test-Path $ScratchDir)) {
    New-Item -ItemType Directory -Path $ScratchDir -Force | Out-Null
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $ScriptDir "Libs"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "RimAgent 依賴組件建置" -ForegroundColor Cyan
Write-Host "RimWorldDir:  $RimWorldDir"
Write-Host "WorkshopDir:  $WorkshopDir"
Write-Host "HarmonyDll:   $HarmonyDll"
Write-Host "ScratchDir:   $ScratchDir"
Write-Host "OutputDir:    $OutputDir"
Write-Host "Configuration:$Configuration"
Write-Host "========================================" -ForegroundColor Cyan

# 預期 commit 與雜湊值
$SmashToolsCommit = "be9eb6fe762d9762e09faee20005db12ca9592b0"
$DevToolsCommit   = "e0b33dfc222de214c45af979c8a45a74e1fb06f3"

# ==============================================================================
# 2. SmashTools 建置流程
# ==============================================================================
Write-Host "`n[1/2] 處理 SmashTools (Commit: $SmashToolsCommit)..." -ForegroundColor Yellow
$SmashToolsDir = Join-Path $ScratchDir "SmashTools"

if (-not (Test-Path $SmashToolsDir)) {
    Write-Host "正在自上游取得 SmashTools 原始碼..."
    git clone "https://github.com/SmashPhil/SmashTools.git" $SmashToolsDir
    git -C $SmashToolsDir checkout $SmashToolsCommit
} else {
    $CurrentCommit = (git -C $SmashToolsDir rev-parse HEAD).Trim()
    if ($CurrentCommit -ne $SmashToolsCommit) {
        Write-Host "將 SmashTools checkout 至固定 commit $SmashToolsCommit..."
        git -C $SmashToolsDir checkout $SmashToolsCommit
    }
}

Assert-SourceCommit $SmashToolsDir $SmashToolsCommit

# 自動產生 SmashTools 的 Directory.Build.props（避免需手動修改 .scratch 原始碼）
$SmashToolsPropsPath = Join-Path $SmashToolsDir "Directory.Build.props"
$SmashToolsPropsContent = @"
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Deterministic>true</Deterministic>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <RefDir Condition="'`$(RefDir)' == ''">$([System.IO.Path]::TrimEndingDirectorySeparator($RimWorldDir))\</RefDir>
    <WorkshopDir Condition="'`$(WorkshopDir)' == ''">$([System.IO.Path]::TrimEndingDirectorySeparator($WorkshopDir))\</WorkshopDir>
    <HarmonyDll Condition="'`$(HarmonyDll)' == ''">$HarmonyDll</HarmonyDll>
    <ModDir Condition="'`$(ModDir)' == ''">`$(MSBuildThisFileDirectory)</ModDir>
    <OutDir Condition="'`$(OutDir)' == ''">`$(MSBuildThisFileDirectory)bin\`$(Configuration)\</OutDir>
  </PropertyGroup>

  <ItemGroup Condition="'`$(UseRimWorldRefs)' != 'false'">
    <Reference Include="Assembly-CSharp">
      <HintPath>`$(RefDir)Assembly-CSharp.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="UnityStubs">
      <HintPath>`$(RefDir)UnityStubs.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="Waylith">
      <HintPath>`$(RefDir)Waylith.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>`$(HarmonyDll)</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
"@
[System.IO.File]::WriteAllText($SmashToolsPropsPath, $SmashToolsPropsContent, [System.Text.Encoding]::UTF8)

# 建置 CoreLib
Write-Host "正在建置 CoreLib.dll..."
$CoreLibProj = Join-Path $SmashToolsDir "Core\CoreLib.csproj"
& dotnet build $CoreLibProj -c $Configuration -p:NuGetAudit=false -p:RefDir="$([System.IO.Path]::TrimEndingDirectorySeparator($RimWorldDir))\" -p:HarmonyDll="$HarmonyDll" -p:ModDir="$SmashToolsDir"
if ($LASTEXITCODE -ne 0) { throw "CoreLib 建置失敗！" }

# 建置 SmashTools
Write-Host "正在建置 SmashTools.dll..."
$SmashToolsProj = Join-Path $SmashToolsDir "SmashTools\SmashTools\SmashTools.csproj"
& dotnet build $SmashToolsProj -c $Configuration -p:NuGetAudit=false -p:RefDir="$([System.IO.Path]::TrimEndingDirectorySeparator($RimWorldDir))\" -p:HarmonyDll="$HarmonyDll" -p:ModDir="$SmashToolsDir"
if ($LASTEXITCODE -ne 0) { throw "SmashTools 建置失敗！" }

# 複製 SmashTools 產物至 OutputDir
$SmashToolsOutDir = Join-Path $OutputDir "SmashTools"
if (-not (Test-Path $SmashToolsOutDir)) {
    New-Item -ItemType Directory -Path $SmashToolsOutDir -Force | Out-Null
}

$BuiltCoreLib = Join-Path $SmashToolsDir "1.7\Assemblies\CoreLib.dll"
$BuiltSmashTools = Join-Path $SmashToolsDir "1.7\Assemblies\SmashTools.dll"
Copy-Item $BuiltCoreLib (Join-Path $SmashToolsOutDir "CoreLib.dll") -Force
Copy-Item $BuiltSmashTools (Join-Path $SmashToolsOutDir "SmashTools.dll") -Force

# 保留 upstream MIT LICENSE
$SmashToolsLicense = Join-Path $SmashToolsDir "LICENSE"
if (Test-Path $SmashToolsLicense) {
    Copy-Item $SmashToolsLicense (Join-Path $SmashToolsOutDir "LICENSE") -Force
}

Write-Host "SmashTools 與 CoreLib 已建置並複製至 $SmashToolsOutDir" -ForegroundColor Green

# ==============================================================================
# 3. DevTools 建置流程
# ==============================================================================
Write-Host "`n[2/2] 處理 DevTools (Commit: $DevToolsCommit)..." -ForegroundColor Yellow
$DevToolsDir = Join-Path $ScratchDir "DevTools"

# 優先利用本機相鄰目錄作為快取來源以避免外部依賴
$LocalDevToolsCache = Join-Path $RepoRoot "..\FasterGameLoading---Continued\.test-temp\DevTools"
if (-not (Test-Path $DevToolsDir)) {
    if (Test-Path $LocalDevToolsCache) {
        Write-Host "自本機快取 ($LocalDevToolsCache) 複製 DevTools 原始碼..."
        robocopy $LocalDevToolsCache $DevToolsDir /E /XD bin obj _mod /NFL /NDL /NJH /NJS | Out-Null
    } else {
        Write-Host "自上游取得 DevTools 原始碼..."
        git clone "https://github.com/SmashPhil/DevTools.git" $DevToolsDir
        git -C $DevToolsDir checkout $DevToolsCommit
    }
}

if (Test-Path (Join-Path $DevToolsDir ".git")) {
    $CurrentDevToolsCommit = (git -C $DevToolsDir rev-parse HEAD).Trim()
    if ($CurrentDevToolsCommit -ne $DevToolsCommit) {
        Write-Host "將 DevTools checkout 至固定 commit $DevToolsCommit..."
        git -C $DevToolsDir checkout $DevToolsCommit
    }
}

Assert-SourceCommit $DevToolsDir $DevToolsCommit

# 自動產生 DevTools 的 Directory.Build.props
$DevToolsPropsPath = Join-Path $DevToolsDir "Directory.Build.props"
$DevToolsPropsContent = @"
<Project>
  <PropertyGroup>
    <Configurations>Debug;Unstable;Release;ReleaseTesting;</Configurations>
    <Configuration Condition=" '`$(Configuration)' == '' ">ReleaseTesting</Configuration>
    <Platform Condition=" '`$(Platform)' == '' ">AnyCPU</Platform>
    <FileVersion>1.0.0</FileVersion>
    <AssemblyVersion>1.0.0</AssemblyVersion>
    <GenerateDocumentationFile>False</GenerateDocumentationFile>
    <GenerateDependencyFile>false</GenerateDependencyFile>
    <Deterministic>true</Deterministic>
    <OutputType>Library</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <FileAlignment>512</FileAlignment>
    <AppDesignerFolder>Properties</AppDesignerFolder>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <DebugType>embedded</DebugType>
    <DebugSymbols>true</DebugSymbols>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <UseKrafsPackageRef>false</UseKrafsPackageRef>
    <ModDir>`$(MSBuildThisFileDirectory)</ModDir>
    <RefDir Condition="'`$(RefDir)' == ''">$([System.IO.Path]::TrimEndingDirectorySeparator($RimWorldDir))\</RefDir>
  </PropertyGroup>

  <PropertyGroup Condition=" '`$(Configuration)|`$(Platform)' == 'Release|AnyCPU' ">
    <Optimize>true</Optimize>
    <DefineConstants>TRACE;RELEASE;</DefineConstants>
  </PropertyGroup>

  <PropertyGroup Condition="'`$(Configuration)|`$(Platform)' == 'ReleaseTesting|AnyCPU'">
    <Optimize>true</Optimize>
    <DefineConstants>TRACE;RELEASE;UNITY_ASSERTIONS;DEV_TOOLS;PROFILER;</DefineConstants>
  </PropertyGroup>
</Project>
"@
[System.IO.File]::WriteAllText($DevToolsPropsPath, $DevToolsPropsContent, [System.Text.Encoding]::UTF8)

# 建置 DevTools
Write-Host "正在建置 DevTools.dll (ReleaseTesting)..."
$DevToolsProj = Join-Path $DevToolsDir "DevTools\DevTools.csproj"
$DevToolsTempOut = Join-Path $DevToolsDir "bin\ReleaseTesting"
& dotnet build $DevToolsProj -c ReleaseTesting -p:NuGetAudit=false -p:RefDir="$([System.IO.Path]::TrimEndingDirectorySeparator($RimWorldDir))\" -p:OutDir="$DevToolsTempOut\"
if ($LASTEXITCODE -ne 0) { throw "DevTools 建置失敗！" }

# 複製 DevTools 產物至 OutputDir
$DevToolsOutDir = Join-Path $OutputDir "DevTools"
if (-not (Test-Path $DevToolsOutDir)) {
    New-Item -ItemType Directory -Path $DevToolsOutDir -Force | Out-Null
}

$BuiltDevTools = Join-Path $DevToolsTempOut "DevTools.dll"
Copy-Item $BuiltDevTools (Join-Path $DevToolsOutDir "DevTools.dll") -Force

# 保留 upstream MIT LICENSE
$DevToolsLicense = Join-Path $DevToolsDir "LICENSE"
if (Test-Path $DevToolsLicense) {
    Copy-Item $DevToolsLicense (Join-Path $DevToolsOutDir "LICENSE") -Force
}

Write-Host "DevTools 已建置並複製至 $DevToolsOutDir" -ForegroundColor Green

# ==============================================================================
# 4. SHA-256 雜湊值驗證
# ==============================================================================
Write-Host "`n[驗證] 檢查產出組件 SHA-256 雜湊值..." -ForegroundColor Cyan

$VerifyTargets = @(
    @{ Name = "SmashTools.dll"; Path = Join-Path $SmashToolsOutDir "SmashTools.dll"; Built = $BuiltSmashTools },
    @{ Name = "CoreLib.dll";    Path = Join-Path $SmashToolsOutDir "CoreLib.dll";    Built = $BuiltCoreLib },
    @{ Name = "DevTools.dll";   Path = Join-Path $DevToolsOutDir "DevTools.dll";     Built = $BuiltDevTools }
)

$AllPassed = $true
foreach ($target in $VerifyTargets) {
    if (-not (Test-Path $target.Path)) {
        Write-Error "找不到組件檔案：$($target.Path)"
        $AllPassed = $false
        continue
    }
    $ActualHash = (Get-FileHash -Path $target.Path -Algorithm SHA256).Hash
    Write-Host "$($target.Name):" -NoNewline
    $BuiltHash = (Get-FileHash -LiteralPath $target.Built -Algorithm SHA256).Hash
    if ($ActualHash -eq $BuiltHash) {
        Write-Host " 一致 ($ActualHash)" -ForegroundColor Green
    } else {
        Write-Host " 不一致！" -ForegroundColor Yellow
        Write-Host "  建置來源：$BuiltHash"
        Write-Host "  實際：$ActualHash"
        $AllPassed = $false
    }
}

if (-not $AllPassed) {
    throw "組件雜湊值驗證未完全符合預期！"
}

Write-Host "`n依賴組件建置與驗證完成！" -ForegroundColor Green
