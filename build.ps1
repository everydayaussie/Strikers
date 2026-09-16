param(
    [string]$Commit = '',
    [string]$Holder = '',
    [string[]]$Property = @()
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'src\strikers-avalonia')))
{
    $root = Split-Path -Parent $PSScriptRoot
}

if (-not (Test-Path (Join-Path $root 'src\strikers-avalonia')))
{
    throw "build.ps1 must sit at the repository root beside src, or one folder below it."
}

if ($Commit -ne '' -and $Commit -notmatch '^[0-9a-f]{7,40}$')
{
    throw "-Commit is a lowercase git hash, 7 to 40 hex digits."
}

if ($Holder -ne '' -and $Holder -notmatch '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$')
{
    throw "-Holder is letters, digits, spaces, dots, dashes and underscores, 64 at most."
}

foreach ($p in $Property)
{
    if ($p -notmatch '^[A-Za-z][A-Za-z0-9_]*=[^;"]*$')
    {
        throw "-Property entries are Name=value: $p"
    }
}

if (Get-Process Strikers, netplay, live-probe -ErrorAction SilentlyContinue)
{
    throw "Close Strikers, netplay and live-probe first; a running exe cannot be published over."
}

$out = Join-Path $root 'out'
$staging = Join-Path $out 'archive'
$check = Join-Path $out 'archive-check'
$zip = Join-Path $out 'Strikers.zip'
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($dir in $staging, $check)
{
    if (Test-Path $dir)
    {
        Remove-Item $dir -Recurse -Force
    }
}

New-Item -ItemType Directory -Path $staging -Force | Out-Null

$props = @()
foreach ($p in $Property)
{
    $props += "-p:$p"
}

if ($Commit -ne '')
{
    $props += "-p:StrikersCommit=$Commit"
}

foreach ($project in 'strikers-avalonia', 'netplay', 'live-probe')
{
    $linked = Join-Path $root "src\$project\obj\Release\net10.0\win-x64\linked"
    if (Test-Path $linked)
    {
        Remove-Item $linked -Recurse -Force
    }

    Write-Host "publishing $project"
    dotnet publish (Join-Path $root "src\$project") -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:PublishTrimmed=true -p:TrimMode=partial `
        -p:DebugType=none -p:IncludeSourceRevisionInInformationalVersion=false -p:TrimmerRemoveSymbols=true -p:LauncherOnly=true `
        -o $staging --nologo -v q @props
    if ($LASTEXITCODE -ne 0)
    {
        throw "publish failed for $project"
    }
}

Get-ChildItem $staging -Filter *.pdb | Remove-Item -Force

$built = @('Strikers.exe', 'netplay.exe', 'live-probe.exe', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'av_libglesv2.dll')
foreach ($needed in $built)
{
    if (-not (Test-Path (Join-Path $staging $needed)))
    {
        throw "$needed is missing from the build"
    }
}

$deps = Join-Path $root 'src\strikers-avalonia\obj\Release\net10.0\win-x64\Strikers.deps.json'
if (-not (Test-Path $deps))
{
    throw "the build left no $deps to read the package versions from"
}

$packages = @{}
foreach ($m in [regex]::Matches((Get-Content $deps -Raw), '"([A-Za-z0-9._-]+)/([0-9][^"]*)": \{'))
{
    $packages[$m.Groups[1].Value] = $m.Groups[2].Value
}

$known = '^(Avalonia(\..*)?|HarfBuzzSharp(\..*)?|SkiaSharp(\..*)?|MicroCom\.Runtime|runtimepack\..*|Strikers(\..*)?)$'
foreach ($name in $packages.Keys)
{
    if ($name -notmatch $known)
    {
        throw "the build carries $name $($packages[$name]), which THIRD-PARTY-NOTICES.txt does not cover; add it there and here"
    }
}

function Need([string]$name)
{
    if (-not $packages.ContainsKey($name))
    {
        throw "the build has no package $name; THIRD-PARTY-NOTICES.txt and build.ps1 need updating"
    }

    return $packages[$name]
}

$runtimeVersion = Need 'runtimepack.Microsoft.NETCore.App.Runtime.win-x64'
$skiaVersion = Need 'SkiaSharp'
$harfVersion = Need 'HarfBuzzSharp'
$versions = @{
    '.NET runtime' = $runtimeVersion
    'Avalonia' = Need 'Avalonia'
    'MicroCom.Runtime' = Need 'MicroCom.Runtime'
    'SkiaSharp' = $skiaVersion
    'HarfBuzzSharp' = $harfVersion
    'ANGLE' = Need 'Avalonia.Angle.Windows.Natives'
}

$headFile = Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt'
$head = (Get-Content $headFile -Raw -Encoding UTF8) -replace "`r`n", "`n"
foreach ($name in $versions.Keys)
{
    $row = [regex]::Match($head, '(?m)^\s+(?:\d\.\s+)?' + [regex]::Escape($name) + '\s+(\S+)')
    if (-not $row.Success)
    {
        throw "THIRD-PARTY-NOTICES.txt has no table row for $name"
    }

    if ($row.Groups[1].Value -ne $versions[$name])
    {
        throw "THIRD-PARTY-NOTICES.txt names $name $($row.Groups[1].Value) but the build carries $($versions[$name]); update the file"
    }
}

$nugetLine = @(dotnet nuget locals global-packages --list) | Select-Object -First 1
if ("$nugetLine" -notmatch '^global-packages:\s*(.+)$')
{
    throw "could not read the NuGet package folder from 'dotnet nuget locals'"
}

$nuget = $Matches[1].Trim()

function PackageFile([string]$package, [string]$version, [string]$file)
{
    $path = Join-Path $nuget (Join-Path $package (Join-Path $version $file))
    if (-not (Test-Path $path))
    {
        throw "$path is missing; the restore did not fetch $package $version"
    }

    return $path
}

function Part([string]$package, [string]$version, [string]$file)
{
    return @{ Package = $package; Version = $version; File = $file; Path = (PackageFile $package $version $file) }
}

$skiaParts = @(
    (Part 'skiasharp' $skiaVersion 'LICENSE.txt'),
    (Part 'skiasharp.nativeassets.win32' $skiaVersion 'THIRD-PARTY-NOTICES.txt')
)
$harfLicence = Part 'harfbuzzsharp' $harfVersion 'LICENSE.txt'
if ((Get-FileHash -Algorithm SHA256 $harfLicence.Path).Hash -ne (Get-FileHash -Algorithm SHA256 $skiaParts[0].Path).Hash)
{
    $skiaParts += $harfLicence
}

$harfNotices = Part 'harfbuzzsharp.nativeassets.win32' $harfVersion 'THIRD-PARTY-NOTICES.txt'
if ((Get-FileHash -Algorithm SHA256 $harfNotices.Path).Hash -ne (Get-FileHash -Algorithm SHA256 $skiaParts[1].Path).Hash)
{
    $skiaParts += $harfNotices
}

$sections = @(
    @{
        Title = "1. .NET runtime $runtimeVersion (https://github.com/dotnet/runtime)"
        Parts = @(
            (Part 'microsoft.netcore.app.runtime.win-x64' $runtimeVersion 'LICENSE.TXT'),
            (Part 'microsoft.netcore.app.runtime.win-x64' $runtimeVersion 'THIRD-PARTY-NOTICES.TXT')
        )
    },
    @{
        Title = "4. SkiaSharp $skiaVersion and HarfBuzzSharp $harfVersion (https://github.com/mono/SkiaSharp)"
        Parts = $skiaParts
    }
)

$text = New-Object System.Text.StringBuilder
[void]$text.Append($head.TrimEnd("`n"))
[void]$text.Append("`n")
foreach ($section in $sections)
{
    [void]$text.Append("`n`n" + ('=' * 80) + "`n" + $section.Title + "`n")
    foreach ($part in $section.Parts)
    {
        $hash = (Get-FileHash -Algorithm SHA256 $part.Path).Hash
        [void]$text.Append("Appended unchanged by build.ps1 from the package $($part.Package) $($part.Version), file $($part.File), SHA-256 $hash`n")
    }

    [void]$text.Append(('=' * 80) + "`n")
    foreach ($part in $section.Parts)
    {
        $body = (Get-Content $part.Path -Raw -Encoding UTF8) -replace "`r`n", "`n"
        [void]$text.Append("`n--- $($part.Package) $($part.Version): $($part.File) ---`n`n")
        [void]$text.Append($body.TrimEnd("`n"))
        [void]$text.Append("`n")
    }
}

[IO.File]::WriteAllText((Join-Path $staging 'THIRD-PARTY-NOTICES.txt'), $text.ToString(), $utf8)

$licence = (Get-Content (Join-Path $root 'LICENSE') -Raw -Encoding UTF8) -replace "`r`n", "`n"
if ($Holder -ne '')
{
    $replaced = [regex]::Replace($licence, '(?m)^Copyright \(c\) (\d+) .+$', "Copyright (c) `$1 $Holder")
    if ($replaced -eq $licence)
    {
        throw "LICENSE has no 'Copyright (c) <year> <holder>' line to put the holder in"
    }

    $licence = $replaced
}

[IO.File]::WriteAllText((Join-Path $staging 'LICENSE'), $licence, $utf8)

$shipped = $built + @('LICENSE', 'THIRD-PARTY-NOTICES.txt')
$extra = @(Get-ChildItem $staging -Recurse -Force | Where-Object { $_.PSIsContainer -or $shipped -notcontains $_.Name } |
           ForEach-Object { $_.FullName.Substring($staging.Length).TrimStart('\') })
if ($extra.Count -gt 0)
{
    throw "the archive would carry more than its eight files: $($extra -join ', ')"
}

Copy-Item $staging $check -Recurse
Push-Location $root
$ErrorActionPreference = 'Continue'
try
{
    foreach ($exe in 'live-probe.exe', 'netplay.exe', 'Strikers.exe')
    {
        $summary = $null
        foreach ($attempt in 1, 2)
        {
            $lines = @(& (Join-Path $check $exe) --selftest 2>$null | ForEach-Object { "$_" })
            $summary = $lines | Select-String '(\d+) passed, (\d+) failed' | Select-Object -Last 1
            if ($summary -and $summary.Matches[0].Groups[2].Value -eq '0')
            {
                break
            }
        }

        if (-not $summary -or $summary.Matches[0].Groups[2].Value -ne '0')
        {
            $lines | Select-String '\[FAIL\]' | ForEach-Object { Write-Host $_.Line }
            throw "the trimmed $exe failed its selftest ($(if ($summary) { $summary.Line.Trim() } else { 'no summary line' }))"
        }

        Write-Host "trimmed $exe selftest: $($summary.Line.Trim())"
    }

    foreach ($exe in 'live-probe.exe', 'netplay.exe')
    {
        & (Join-Path $check $exe) --version --launcher-check 2>$null | Out-Null
        if ($LASTEXITCODE -ne 5)
        {
            throw "$exe ran outside Strikers (exit $LASTEXITCODE, expected 5), so the launcher-only lock is not in this build"
        }

        Write-Host "$exe refuses to run outside Strikers"
    }

    $stamp = (@(& (Join-Path $check 'Strikers.exe') --version 2>$null | ForEach-Object { "$_" }) -join ' ')
    if ($Commit -ne '' -and $stamp -notmatch "commit $Commit")
    {
        throw "Strikers.exe --version does not carry the commit stamp: $stamp"
    }

    if ($Commit -eq '' -and $stamp -match 'commit [0-9a-f]')
    {
        throw "Strikers.exe --version carries a commit stamp that was not asked for: $stamp"
    }

    Write-Host "Strikers.exe --version: $stamp"
}
finally
{
    Pop-Location
    Remove-Item $check -Recurse -Force -ErrorAction SilentlyContinue
    $ErrorActionPreference = 'Stop'
}

if ($env:STRIKERS_SIGN_THUMBPRINT)
{
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool)
    {
        throw "STRIKERS_SIGN_THUMBPRINT is set but signtool.exe is not on PATH (install the Windows SDK)."
    }

    foreach ($exe in 'Strikers.exe', 'netplay.exe', 'live-probe.exe')
    {
        & $signtool.Source sign /sha1 $env:STRIKERS_SIGN_THUMBPRINT /fd SHA256 `
            /tr http://timestamp.digicert.com /td SHA256 (Join-Path $staging $exe)
        if ($LASTEXITCODE -ne 0)
        {
            throw "signing failed for $exe"
        }
    }

    Write-Host "signed the three exes with cert $env:STRIKERS_SIGN_THUMBPRINT"
}
else
{
    Write-Host "NOT signed. Set STRIKERS_SIGN_THUMBPRINT to sign; an unsigned build WILL trip SmartScreen and antivirus."
}

if (Test-Path $zip)
{
    Remove-Item $zip -Force
}

Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -CompressionLevel Optimal

$hash = (Get-FileHash -Algorithm SHA256 $zip).Hash
"$hash  Strikers.zip" | Set-Content -Path "$zip.sha256" -Encoding ascii

Write-Host ""
Get-ChildItem $staging | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
Write-Host ("archive: {0}  ({1} MB)" -f $zip, [math]::Round((Get-Item $zip).Length / 1MB, 1))
Write-Host ("SHA-256: {0}" -f $hash)
if ($Commit -ne '')
{
    Write-Host ("built from commit {0}" -f $Commit)
}
