#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('csharp', 'powershell', 'python')]
    [string] $Server,
    [switch] $RestoreOnly,
    [string] $CacheRoot = (Join-Path $PSScriptRoot '..\artifacts\lsp')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$platform = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
$version = switch ($Server) {
    'csharp' { '0.28.0' }
    'powershell' { '4.7.0' }
    'python' { '1.15.0' }
}
$directory = Join-Path $CacheRoot "$platform\$Server\$version"
$executable = switch ($Server) {
    'csharp' { Join-Path $directory $(if ($IsWindows) { 'tool\csharp-ls.exe' } else { 'tool\csharp-ls' }) }
    'powershell' { Join-Path $directory 'PowerShellEditorServices\Start-EditorServices.ps1' }
    'python' { Join-Path $directory $(if ($IsWindows) { 'venv\Scripts\python.exe' } else { 'venv\bin\python' }) }
}
$marker = Join-Path $directory 'restored'

function Invoke-LspRestoreCommand {
    param([string] $Command, [string[]] $Arguments)
    # Keep package-manager output off the LSP protocol's stdout.
    $PSNativeCommandUseErrorActionPreference = $false
    & $Command @Arguments 2>&1 | ForEach-Object { [Console]::Error.WriteLine([string] $_) }
    if ($LASTEXITCODE -ne 0) { throw "$Command failed restoring $Server (exit $LASTEXITCODE)." }
}

$null = New-Item -ItemType Directory -Force -Path $directory
$lockPath = Join-Path $directory 'restore.lock'
$deadline = [DateTime]::UtcNow.AddMinutes(4)
$lock = $null
while (!$lock) {
    try { $lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] {
        if ([DateTime]::UtcNow -ge $deadline) { throw "Timed out waiting for $Server restore lock: $lockPath" }
        Start-Sleep -Milliseconds 200
    }
}
try {
    if (!(Test-Path -LiteralPath $marker) -or !(Test-Path -LiteralPath $executable)) {
        [Console]::Error.WriteLine("Restoring $Server language server $version to $directory")
        # A failed restore must never leave a success marker for later launches.
        if (Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
        switch ($Server) {
            'csharp' {
                if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'C# LSP restore requires the .NET 10 SDK on PATH.' }
                # Tool installation needs an empty tool directory after an interrupted install.
                $toolPath = Join-Path $directory 'tool'
                if (Test-Path -LiteralPath $toolPath) { Remove-Item -LiteralPath $toolPath -Recurse -Force }
                Invoke-LspRestoreCommand dotnet @('tool', 'install', 'csharp-ls', '--version', $version, '--tool-path', $toolPath)
            }
            'powershell' {
                $archive = Join-Path $directory 'PowerShellEditorServices.zip'
                Invoke-WebRequest -Uri "https://github.com/PowerShell/PowerShellEditorServices/releases/download/v$version/PowerShellEditorServices.zip" -OutFile $archive
                $expected = '5084f0326cc88539e9d880b08f6c52ce5be4672bb8ecee8921e35f8895a6b9d7'
                if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
                    throw 'PowerShell Editor Services archive SHA-256 mismatch; refusing to extract or execute it.'
                }
                Expand-Archive -LiteralPath $archive -DestinationPath $directory -Force
                Remove-Item -LiteralPath $archive
            }
            'python' {
                $python = if ($IsWindows) { 'python' } else { 'python3' }
                if (!(Get-Command $python -ErrorAction SilentlyContinue)) { throw "Python LSP restore requires Python 3.9+ on PATH ($python)." }
                Invoke-LspRestoreCommand $python @('-m', 'venv', (Join-Path $directory 'venv'))
                Invoke-LspRestoreCommand $executable @('-m', 'pip', 'install', '--disable-pip-version-check', "python-lsp-server==$version")
            }
        }
        if (!(Test-Path -LiteralPath $executable)) { throw "Restore did not produce $executable" }
        Set-Content -LiteralPath $marker -Value $version
    }
}
finally { $lock.Dispose() }

if ($RestoreOnly) { return }

$start = [Diagnostics.ProcessStartInfo]::new()
$start.UseShellExecute = $false
$start.WorkingDirectory = $root
$start.FileName = $executable
switch ($Server) {
    'csharp' {
        $start.ArgumentList.Add('--solution')
        $start.ArgumentList.Add('DotReticulum.Ecosystem.slnx')
    }
    'powershell' {
        $start.FileName = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source
        foreach ($argument in @('-NoLogo', '-NoProfile', '-File', $executable, '-Stdio', '-LogLevel', 'Error')) {
            $start.ArgumentList.Add($argument)
        }
    }
    'python' {
        $start.ArgumentList.Add('-m')
        $start.ArgumentList.Add('pylsp')
    }
}
# Inherit protocol handles directly, without PowerShell decoding/reformatting frames.
$process = [Diagnostics.Process]::Start($start)
try {
    $process.WaitForExit()
    $exitCode = $process.ExitCode
}
finally { $process.Dispose() }
exit $exitCode
