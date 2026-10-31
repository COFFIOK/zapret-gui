// Создано автоматически из старой версии: вставка меню выбора групп в «test zapret.ps1» при переносе
namespace ZapretCS
{
    static class MigData
    {
        public const string OldParse = @"    $rawTargets = New-OrderedDict
    if (Test-Path $targetsFile) {
        Get-Content $targetsFile | ForEach-Object {
            if ($_ -match '^\s*(\w+)\s*=\s*""(.+)""\s*$') {
                Add-OrSet -dict $rawTargets -key $matches[1] -val $matches[2]
            }
        }
    }
";
        public const string NewParse = @"    $rawTargets = New-OrderedDict
    $targetGroupOf = @{}
    $groupNames = New-Object System.Collections.Generic.List[string]
    $currentGroup = ""Other""
    if (Test-Path $targetsFile) {
        Get-Content $targetsFile -Encoding UTF8 | ForEach-Object {
            if ($_ -match '^\s*###\s*(.+?)\s*$') {
                $currentGroup = $matches[1]
            }
            elseif ($_ -match '^\s*(\w+)\s*=\s*""(.+)""\s*$') {
                Add-OrSet -dict $rawTargets -key $matches[1] -val $matches[2]
                $targetGroupOf[$matches[1]] = $currentGroup
                if (-not $groupNames.Contains($currentGroup)) { [void]$groupNames.Add($currentGroup) }
            }
        }
    }

    # Let the user choose which groups (### headers in targets.txt) to test
    if ($rawTargets.Count -gt 0 -and $groupNames.Count -gt 1) {
        $selectedGroups = Read-GroupSelection -groups $groupNames.ToArray() -groupOf $targetGroupOf
        if ($selectedGroups.Count -lt $groupNames.Count) {
            $filtered = New-OrderedDict
            foreach ($key in $rawTargets.Keys) {
                if ($selectedGroups -contains $targetGroupOf[$key]) { $filtered[$key] = $rawTargets[$key] }
            }
            $rawTargets = $filtered
        }
    }
";
        public const string GroupFunc = @"# Select which target groups to test (groups come from ### headers in targets.txt)
function Read-GroupSelection {
    param([array]$groups, [hashtable]$groupOf)

    $picked = New-Object 'System.Collections.Generic.HashSet[int]'
    while ($true) {
        Write-Host """"
        Write-Host ""What to test:"" -ForegroundColor Cyan
        Write-Host ""  [0] All (start now)"" -ForegroundColor Gray
        for ($i = 0; $i -lt $groups.Count; $i++) {
            $g = $groups[$i]
            $cnt = @($groupOf.Keys | Where-Object { $groupOf[$_] -eq $g }).Count
            if ($picked.Contains($i + 1)) {
                Write-Host ""  [$($i + 1)] [+] $g ($cnt)"" -ForegroundColor Green
            } else {
                Write-Host ""  [$($i + 1)] [ ] $g ($cnt)"" -ForegroundColor Gray
            }
        }
        $selectionInput = Read-Host ""Number to add/remove (e.g. 1 or 1,3). '0' = all. Enter = start""
        $trimmed = $selectionInput.Trim()
        if ($trimmed -eq '0') { return $groups }

        if ($trimmed -eq '') {
            if ($picked.Count -eq 0) {
                Write-Host ""Nothing selected yet. Pick groups by number, or '0' for all."" -ForegroundColor Yellow
                continue
            }
            $result = @($picked | Sort-Object | ForEach-Object { $groups[$_ - 1] })
            Write-Host ""Testing: $($result -join ', ')"" -ForegroundColor Green
            return $result
        }

        foreach ($part in ($trimmed -split '[,\s]+')) {
            $nums = @()
            if ($part -match '^(\d+)-(\d+)$') {
                for ($n = [int]$matches[1]; $n -le [int]$matches[2]; $n++) { $nums += $n }
            } elseif ($part -match '^\d+$') {
                $nums += [int]$part
            } else {
                Write-Host ""  [WARN] '$part' is not a number. Skipping."" -ForegroundColor Yellow
                continue
            }
            foreach ($n in $nums) {
                if ($n -lt 1 -or $n -gt $groups.Count) {
                    Write-Host ""  [WARN] No group number $n. Skipping."" -ForegroundColor Yellow
                } elseif ($picked.Contains($n)) {
                    [void]$picked.Remove($n)
                } else {
                    [void]$picked.Add($n)
                }
            }
        }
    }
}

";
    }
}
