#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch] $Native,
    [switch] $Live,
    [string] $Python = 'artifacts\builds\lxst-win-reference\Scripts\python.exe',
    [string] $Upstream = $env:DOTRETICULUM_UPSTREAM,
    [string] $OutputDirectory = 'artifacts\tests\sideband-propagation-telemetry\acceptance'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Set-Location $root
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path $output) { throw 'Use a fresh output directory to preserve all previous acceptance receipts.' }
$null = New-Item $output -ItemType Directory
$files = @(
    'src\LXMF\LxmPropagationStatusClient.cs',
    'src\Sideband\Sideband.Core\SidebandPropagationTelemetry.cs',
    'src\Sideband\Sideband.Core\SidebandPropagationTelemetrySource.cs',
    'src\Sideband\Sideband.Core\SidebandTelemetryCodec.cs',
    'src\Sideband\Sideband.Core\SidebandTelemetryValue.cs',
    'tests\Sideband.Tests\SidebandPropagationTelemetryTests.cs',
    'tests\Sideband.Tests\SidebandPropagationTelemetryLiveTests.cs',
    'tests\Sideband.Tests\GeneratePropagationTelemetryVectors.py',
    'tests\Sideband.Tests\Fixtures\sideband-propagation-telemetry.json',
    'tests\Interop\sideband_propagation_telemetry_node.py',
    'tests\Interop\SidebandPropagationTelemetryProbe.cs',
    'tests\Pester\Invoke-SidebandPropagationTelemetryAcceptance.ps1'
)
$inputs = @($files | ForEach-Object { @{ Path = $_; Sha256 = (Get-FileHash $_).Hash.ToLowerInvariant() } })
$inputs | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $output inputs.json)
if ($Live) {
    if (-not $Upstream) { throw 'Live mode requires the explicit pinned RNS checkout in -Upstream or DOTRETICULUM_UPSTREAM.' }
    $env:DOTRETICULUM_UPSTREAM = [IO.Path]::GetFullPath($Upstream)
    $env:DOTRETICULUM_PYTHON = [IO.Path]::GetFullPath($Python)
    $env:DOTRETICULUM_LIVE = '1'
    $env:DOTSIDEBAND_PROPAGATION_LIVE = '1'
    $env:DOTSIDEBAND_PROPAGATION_EVIDENCE = $output
}
else { $env:DOTSIDEBAND_PROPAGATION_LIVE = '0' }
& dotnet test tests\Sideband.Tests\Sideband.Tests.csproj -c Release --filter 'FullyQualifiedName~SidebandPropagationTelemetry' `
    --artifacts-path (Join-Path $output managed) --logger 'trx;LogFileName=provider.trx' --results-directory $output -warnaserror --nologo -v:q 2>&1 |
    Tee-Object (Join-Path $output tests.log)
if ($LASTEXITCODE -ne 0) { throw 'Provider tests failed.' }
if ($Native) {
    if (-not $IsWindows) { throw 'This acceptance runner currently verifies Windows NativeAOT only; Linux is unavailable, not passed.' }
    $projectDirectory = Join-Path $output probe
    $null = New-Item $projectDirectory -ItemType Directory
    $project = Join-Path $projectDirectory probe.csproj
    $sidebandProject = Join-Path $root 'src\Sideband\Sideband.Core\Sideband.Core.csproj'
    $probeSource = Join-Path $root 'tests\Interop\SidebandPropagationTelemetryProbe.cs'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><PublishAot>true</PublishAot><IsAotCompatible>true</IsAotCompatible><EnableDefaultCompileItems>false</EnableDefaultCompileItems><DebugType>none</DebugType><DebugSymbols>false</DebugSymbols></PropertyGroup>
  <ItemGroup><ProjectReference Include="$sidebandProject" /><Compile Include="$probeSource" /></ItemGroup>
</Project>
"@ | Set-Content $project
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $install = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $install) { throw 'MSVC tools are required for NativeAOT acceptance.' }
    $setup = Join-Path $install 'VC\Auxiliary\Build\vcvars64.bat'
    $binaryDirectory = Join-Path $output native
    $buildDirectory = Join-Path $output aot-build
    $env:PATH = (Split-Path $vswhere -Parent) + ';' + $env:PATH
    & $env:ComSpec /c "call `"$setup`" >nul && dotnet publish `"$project`" -c Release -r win-x64 --artifacts-path `"$buildDirectory`" -o `"$binaryDirectory`" -p:ILLinkTreatWarningsAsErrors=true -p:IlcTreatWarningsAsErrors=true -warnaserror --nologo -v:q" 2>&1 |
        Tee-Object (Join-Path $output publish.log)
    if ($LASTEXITCODE -ne 0) { throw 'NativeAOT probe publish failed.' }
    $binary = Join-Path $binaryDirectory probe.exe
    & $binary --help 2>&1 | Tee-Object (Join-Path $output native-help.log)
    if ($LASTEXITCODE -ne 0) { throw 'NativeAOT help failed.' }
    & $binary --self-test tests\Sideband.Tests\Fixtures\sideband-propagation-telemetry.json 2>&1 |
        Tee-Object (Join-Path $output native-self-test.log)
    if ($LASTEXITCODE -ne 0) { throw 'NativeAOT status/failure/recovery/disposal failed.' }
    if ($Live) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $port = $listener.LocalEndpoint.Port
        $listener.Stop()
        $peerDirectory = Join-Path $output native-live-primary
        $runtimeDirectory = Join-Path $output native-live-runtime
        $null = New-Item $runtimeDirectory -ItemType Directory
        @"
[reticulum]
 share_instance = no
 enable_transport = no
[logging]
 loglevel = 0
[interfaces]
 [[Primary]]
 type = TCPClientInterface
 enabled = yes
 target_host = 127.0.0.1
 target_port = $port
"@ | Set-Content (Join-Path $runtimeDirectory config)
        $start = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_PYTHON)
        $start.UseShellExecute = $false
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.Environment['PYTHONPATH'] = $env:DOTRETICULUM_UPSTREAM
        foreach ($argument in @('tests\Interop\sideband_propagation_telemetry_node.py',
                '--config', $peerDirectory, '--tcp-server-port', "$port")) { $start.ArgumentList.Add($argument) }
        $peer = [Diagnostics.Process]::Start($start)
        $stderr = $peer.StandardError.ReadToEndAsync()
        $observations = [Collections.Generic.List[string]]::new()
        try {
            $deadline = [DateTime]::UtcNow.AddSeconds(40)
            $ready = $null
            while (-not $ready -and [DateTime]::UtcNow -lt $deadline) {
                $line = $peer.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(40)).GetAwaiter().GetResult()
                if ($null -eq $line) { throw 'Primary lxmd exited before readiness.' }
                $observations.Add($line)
                if ($line.StartsWith('@@')) {
                    $value = $line.Substring(2) | ConvertFrom-Json
                    if ($value.event -eq 'ready') { $ready = $value }
                }
            }
            if (-not $ready) { throw 'Primary lxmd was not responsive before the deadline.' }
            & $binary --rnsconfig $runtimeDirectory --identity $ready.identity_path 2>&1 |
                Tee-Object (Join-Path $output native-live-query.log)
            if ($LASTEXITCODE -ne 0) { throw 'NativeAOT provider query against actual lxmd failed.' }
        }
        finally {
            if (-not $peer.HasExited) {
                $peer.StandardInput.WriteLine('quit')
                $peer.StandardInput.Flush()
                if (-not $peer.WaitForExit(10000)) { $peer.Kill($true); $peer.WaitForExit() }
            }
            $observations.Add($peer.StandardOutput.ReadToEnd())
            $observations | Set-Content (Join-Path $output native-live-primary.log)
            $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $output native-live-primary-stderr.log)
            $peer.Dispose()
            # Only these two freshly owned configurations contain ephemeral test keys.
            foreach ($directory in @($peerDirectory, $runtimeDirectory)) {
                if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
            }
        }
    }
    @{ Path = $binary; Sha256 = (Get-FileHash $binary).Hash.ToLowerInvariant() } |
        ConvertTo-Json | Set-Content (Join-Path $output native-binary.json)
}
foreach ($file in $inputs) {
    if ((Get-FileHash $file.Path).Hash.ToLowerInvariant() -cne $file.Sha256) { throw "Scoped input changed during acceptance: $($file.Path)" }
}
@{ Passed = $true; Native = [bool]$Native; Live = [bool]$Live; Head = (& git rev-parse HEAD); Inputs = $inputs } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output result.json)
