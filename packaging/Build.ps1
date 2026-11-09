param(
    [string] $DotNet = 'dotnet',
    [string] $Wix = 'wix',
    [string] $VCRedistPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist ('publish-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    & $DotNet publish (Join-Path $root 'src/ChudType/ChudType.csproj') --configuration Release --runtime win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $stage
    if ($LASTEXITCODE -ne 0) { throw 'ChudType publish failed.' }
    if ($VCRedistPath) {
        $required = 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'msvcp140_codecvt_ids.dll',
            'vcruntime140.dll', 'vcruntime140_1.dll', 'vcruntime140_threads.dll'
        foreach ($name in $required) {
            $source = Join-Path $VCRedistPath $name
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $stage }
        }
    }
    foreach ($name in 'README.md', 'THIRD-PARTY-NOTICES.md') {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage
    }
    Copy-Item -LiteralPath (Join-Path $root 'third-party-licenses') -Destination $stage -Recurse
    $msi = Join-Path $dist 'ChudType-0.1.4-win-x64.msi'
    & $Wix build (Join-Path $PSScriptRoot 'ChudType.wxs') -arch x64 -d "Payload=$stage" -dcl mszip -pdbtype none -o $msi
    if ($LASTEXITCODE -ne 0) { throw 'ChudType MSI build failed.' }

    $marker = Join-Path $stage 'portable.flag'
    New-Item -ItemType File -Path $marker | Out-Null
    $zip = Join-Path $dist 'ChudType-0.1.4-win-x64-portable.zip'
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal -Force
    Get-FileHash -Algorithm SHA256 -LiteralPath $msi, $zip |
        ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } |
        Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

    $portable = Join-Path $dist 'portable'
    if (Test-Path -LiteralPath $portable) {
        # Preserve the portable profile. A fresh publish directory also avoids stale binaries.
        $prior = Join-Path $dist ('portable-prior-' + [guid]::NewGuid().ToString('N'))
        $resolved = (Resolve-Path -LiteralPath $portable).ProviderPath
        $distFull = (Resolve-Path -LiteralPath $dist).ProviderPath
        if ($resolved -ne (Join-Path $distFull 'portable')) { throw 'Unexpected portable directory.' }
        Move-Item -LiteralPath $resolved -Destination $prior
        $profile = Join-Path $prior 'data'
        if (Test-Path -LiteralPath $profile) { Copy-Item -LiteralPath $profile -Destination $stage -Recurse }
    }
    $resolved = (Resolve-Path -LiteralPath $stage).ProviderPath
    $distFull = (Resolve-Path -LiteralPath $dist).ProviderPath
    if ((Split-Path -Parent $resolved) -ne $distFull) { throw 'Unexpected staging directory.' }
    Move-Item -LiteralPath $resolved -Destination $portable
    Get-Item -LiteralPath $msi, $zip, (Join-Path $portable 'ChudType.exe')
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $resolved = (Resolve-Path -LiteralPath $stage).ProviderPath
        $distFull = (Resolve-Path -LiteralPath $dist).ProviderPath
        if ((Split-Path -Parent $resolved) -eq $distFull -and (Split-Path -Leaf $resolved) -match '^publish-[a-f0-9]{32}$') {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
}
