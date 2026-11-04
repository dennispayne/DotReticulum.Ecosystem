Describe 'NomadNet primary node profile identity boundary' -Tag 'NodeProfileBoundary' {
    It 'T263 observes retained primary peers with source RNS cache and discarded peers with an isolated fresh cache' {
        if ($env:DOTRETICULUM_NOMADNET_NODE_PROFILE -ne '1') {
            Set-ItResult -Skipped -Because 'Enable DOTRETICULUM_NOMADNET_NODE_PROFILE=1 with pinned primary Python references; this diagnoses a missing migration contract, not native migration acceptance.'
            return
        }
        $repository = if ($env:DOTRETICULUM_TEST_REPOSITORY) { $env:DOTRETICULUM_TEST_REPOSITORY }
            else { Split-Path (Split-Path $PSScriptRoot -Parent) -Parent }
        foreach ($name in 'DOTRETICULUM_PYTHON', 'DOTRETICULUM_UPSTREAM',
            'DOTRETICULUM_LXMF_UPSTREAM', 'DOTRETICULUM_NOMADNET_SOURCE') {
            if (-not [Environment]::GetEnvironmentVariable($name)) { throw "$name is required." }
        }
        $work = Join-Path (Get-Location) "artifacts\tests\nomad-node-migration\boundary-$([Guid]::NewGuid().ToString('N'))"
        $null = New-Item $work -ItemType Directory -Force
        function Invoke-PrimaryNodeObservation([string] $name, [string] $profile, [string] $rns, [bool] $inspect) {
            $output = Join-Path $work "$name.json"
            $info = [Diagnostics.ProcessStartInfo]::new($env:DOTRETICULUM_PYTHON)
            $info.UseShellExecute = $false
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.WorkingDirectory = $work
            foreach ($argument in @((Join-Path $repository 'tests\Interop\ObserveNomadNetNodeProfile.py'),
                '--profile', $profile, '--rnsconfig', $rns, '--output', $output)) {
                $info.ArgumentList.Add($argument)
            }
            if ($inspect) { $info.ArgumentList.Add('--inspect') }
            $info.Environment['PYTHONPATH'] = @($env:DOTRETICULUM_UPSTREAM,
                $env:DOTRETICULUM_LXMF_UPSTREAM) -join [IO.Path]::PathSeparator
            $info.Environment['PYTHON_CPU_COUNT'] = '2'
            foreach ($key in 'TMP', 'TEMP', 'TMPDIR') { $info.Environment[$key] = $work }
            $process = [Diagnostics.Process]::new()
            $process.StartInfo = $info
            try {
                $null = $process.Start()
                $stdout = $process.StandardOutput.ReadToEndAsync()
                $stderr = $process.StandardError.ReadToEndAsync()
                if (-not $process.WaitForExit(90000)) {
                    $process.Kill($true)
                    $process.WaitForExit()
                    throw "Primary observation timed out; evidence: $work"
                }
                $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $work "$name.stdout")
                $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $work "$name.stderr")
                $process.ExitCode | Should -Be 0 -Because "actual primary diagnostics remain in $work"
                return $output
            }
            finally { $process.Dispose() }
        }
        $profile = Join-Path $work 'primary'
        $knownRns = Join-Path $work 'known-rns'
        $seed = Invoke-PrimaryNodeObservation 'seed' $profile $knownRns $false
        (Get-FileHash $seed).Hash | Should -Be (Get-FileHash (
            Join-Path $repository 'tests\NomadNet.Tests\NomadNetNodeProfileBoundaryVectors.json')).Hash
        $isolated = Join-Path $work 'isolated-primary'
        Copy-Item $profile $isolated -Recurse
        $known = Get-Content (Invoke-PrimaryNodeObservation 'known' $profile $knownRns $true) -Raw | ConvertFrom-Json
        $absent = Get-Content (Invoke-PrimaryNodeObservation 'absent' $isolated (Join-Path $work 'fresh-rns') $true) -Raw | ConvertFrom-Json
        $known.peersOnLoad | Should -Be 1
        $known.offered | Should -Be 23
        $known.peerIdentityRecalled | Should -BeTrue
        $absent.peersOnLoad | Should -Be 0
        $absent.peersAfterSave | Should -Be 0
        $absent.peerIdentityRecalled | Should -BeFalse
        $known.nodeStats.client_propagation_messages_received | Should -Be 7
        $absent.nodeStats.client_propagation_messages_received | Should -Be 7
        $known.peerFields | Should -Not -Contain 'public_key'
        $known.peerFields | Should -Not -Contain 'identity'
    }
}
