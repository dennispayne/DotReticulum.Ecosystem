Set-StrictMode -Version Latest

function Get-ProductionModules {
    param([string] $Root)
    foreach ($project in Get-ChildItem -LiteralPath (Join-Path $Root 'src') -Recurse -Filter '*.csproj' -File) {
        $xml = [xml]::new()
        $xml.Load($project.FullName)
        $assembly = $xml.SelectSingleNode('/Project/PropertyGroup/AssemblyName')
        if ($assembly) { $assembly.InnerText } else { $project.BaseName }
    }
}

function Merge-ProductionCoverage {
    param([IO.FileInfo[]] $Reports, [string[]] $ExpectedModules)
    $modules = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($report in $Reports) {
        $xml = [xml]::new()
        $xml.Load($report.FullName)
        foreach ($module in $xml.SelectNodes('/CoverageSession/Modules/Module')) {
            $name = $module.SelectSingleNode('ModuleName').InnerText
            if ($name -cnotin $ExpectedModules) { continue }
            if (!$modules.ContainsKey($name)) {
                $modules[$name] = @{
                    Lines = [Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::Ordinal)
                    Branches = [Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::Ordinal)
                }
            }
            $files = @{}
            foreach ($file in $module.SelectNodes('Files/File')) {
                $path = [IO.Path]::GetFullPath($file.GetAttribute('fullPath'))
                if ($IsWindows) { $path = $path.ToUpperInvariant() }
                $files[$file.GetAttribute('uid')] = $path
            }
            foreach ($method in $module.SelectNodes('Classes/Class/Methods/Method')) {
                $methodName = $method.SelectSingleNode('Name').InnerText
                $fileRef = $method.SelectSingleNode('FileRef')
                $defaultFile = if ($null -ne $fileRef) { $fileRef.GetAttribute('uid') } else { '' }
                foreach ($kind in @('Lines', 'Branches')) {
                    $selector = if ($kind -eq 'Lines') { 'SequencePoints/SequencePoint' } else { 'BranchPoints/BranchPoint' }
                    foreach ($point in $method.SelectNodes($selector)) {
                        $fileId = if ($point.HasAttribute('fileid')) { $point.GetAttribute('fileid') } else { $defaultFile }
                        if (!$files.ContainsKey($fileId)) { throw "Unknown coverage source '$fileId' in $($report.FullName)." }
                        $parts = @($files[$fileId], $point.GetAttribute('sl'))
                        if ($kind -eq 'Branches') {
                            $parts += @($methodName, $point.GetAttribute('offset'), $point.GetAttribute('path'), $point.GetAttribute('offsetend'))
                        }
                        $key = ConvertTo-Json -InputObject $parts -Compress
                        $hit = [long]::Parse($point.GetAttribute('vc'), [cultureinfo]::InvariantCulture) -gt 0
                        $points = $modules[$name][$kind]
                        $points[$key] = $hit -or ($points.ContainsKey($key) -and $points[$key])
                    }
                }
            }
        }
    }
    $missing = @($ExpectedModules | Where-Object { !$modules.ContainsKey($_) -or $modules[$_].Lines.Count -eq 0 })
    if ($missing.Count) { throw "Incomplete production coverage; missing instrumented modules: $($missing -join ', ')" }
    return ,$modules
}

Export-ModuleMember -Function Get-ProductionModules, Merge-ProductionCoverage
