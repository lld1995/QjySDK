# Script to add stopLoss param, EntryPrice field, entry recording, and exit reset.
# Stop-loss check logic (step 6) will be inserted via line-by-line processing.

$files = @(
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Oscillator\RSI_Fourier.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Oscillator\WR_Fourier.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Resonance\CCI_MACD_Fourier.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Resonance\MACD_KDJ_Cross.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Resonance\RSI_Boll_Fourier.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Resonance\ThreeFactorResonance.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Reversal\DonchianReverse.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Reversal\MACD_Deviate.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Reversal\MACD_Deviate_Boll.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Reversal\Reverse.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Reversal\ReverseShadow.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Reversal\RSI_Deviate.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Reversal\RSI_Deviate_Boll.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Poly\ExtremeReversal.cs",
    "e:\LocalWorkSpaces\Qjy\QjySDK\Stg\Poly\MoEPredict.cs"
)

foreach ($file in $files) {
    $content = [System.IO.File]::ReadAllText($file)
    $fileName = [System.IO.Path]::GetFileName($file)
    
    if ($content -match 'stopLoss') {
        Write-Output "SKIP (already has stopLoss): $fileName"
        continue
    }
    if ($content -notmatch 'StgBase') {
        Write-Output "SKIP (not a strategy): $fileName"
        continue
    }
    
    $modified = $false
    
    # ============ 1. Add stopLoss parameter ============
    if ($content -match '(?m)^([ \t]+)(//\s*手数控制)') {
        $content = $content -replace '(?m)^([ \t]+)(//\s*手数控制)', "`$1sd.ArgDic[""stopLoss""] = 5.0m;          // 止损百分比`r`n`r`n`$1`$2"
        $modified = $true
    } elseif ($content -match '(?m)^([ \t]+)(sd\.ArgDic\["lotsMode"\])') {
        $content = $content -replace '(?m)^([ \t]+)(sd\.ArgDic\["lotsMode"\])', "`$1sd.ArgDic[""stopLoss""] = 5.0m;          // 止损百分比`r`n`r`n`$1`$2"
        $modified = $true
    } elseif ($content -match '(?m)^([ \t]+)(// =+ 交易参数)') {
        $content = $content -replace '(?m)^([ \t]+)(// =+ 交易参数)', "`$1sd.ArgDescDic[""stopLoss""] = new ArgDesc { Text = ""止损%"", Explain = ""固定止损百分比，0为不启用"" };`r`n`$1sd.ArgDic[""stopLoss""] = 5.0m;`r`n`r`n`$1`$2"
        $modified = $true
    }
    
    # ============ 2. Add stopLoss ArgDesc ============
    if ($content -notmatch 'ArgDescDic\["stopLoss"\]') {
        if ($content -match '(?m)^([ \t]+)(sd\.ArgDescDic\["lotsMode"\])') {
            $content = $content -replace '(?m)^([ \t]+)(sd\.ArgDescDic\["lotsMode"\])', "`$1sd.ArgDescDic[""stopLoss""] = new ArgDesc() { Text = ""止损%"", Explain = ""固定止损百分比，0为不启用"" };`r`n`$1`$2"
            $modified = $true
        }
    }
    
    # ============ 3. Add EntryPrice to State class ============
    if ($content -notmatch 'EntryPrice') {
        $content = $content -replace '(public decimal Num \{ get;\s*set;\s*\}[^\r\n]*)', "`$1`r`n            public decimal EntryPrice { get; set; }"
        $modified = $true
    }
    
    # ============ 4. Add s.EntryPrice = q.Close at entry points ============
    $lines = $content -split "`r`n"
    $newLines = @()
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $newLines += $lines[$i]
        if ($lines[$i] -match '^\s+s\.Num = num;' -and ($i + 1) -lt $lines.Length) {
            $nextLine = $lines[$i + 1]
            if ($nextLine -match 'Trade\(' -and $nextLine -match 'OrderType\.(BUY|SELL)[^_]') {
                $alreadySet = $false
                for ($j = [Math]::Max(0, $i-2); $j -le [Math]::Min($lines.Length-1, $i+2); $j++) {
                    if ($lines[$j] -match 'EntryPrice') { $alreadySet = $true; break }
                }
                if (-not $alreadySet) {
                    $indent = ""
                    if ($lines[$i] -match '^(\s+)') { $indent = $matches[1] }
                    $newLines += "${indent}s.EntryPrice = q.Close;"
                    $modified = $true
                }
            }
        }
    }
    $content = $newLines -join "`r`n"
    
    # ============ 5. Add s.EntryPrice = 0 at exit-to-zero points ============
    $lines = $content -split "`r`n"
    $newLines = @()
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $newLines += $lines[$i]
        if ($lines[$i] -match '^\s+s\.Num = 0;') {
            $prevHasStatus0 = ($i -gt 0 -and $lines[$i-1] -match 's\.Status = 0')
            $alreadySet = (($i + 1) -lt $lines.Length -and $lines[$i + 1] -match 'EntryPrice = 0')
            if ($prevHasStatus0 -and -not $alreadySet) {
                $indent = ""
                if ($lines[$i] -match '^(\s+)') { $indent = $matches[1] }
                $newLines += "${indent}s.EntryPrice = 0;"
                $modified = $true
            }
        }
    }
    $content = $newLines -join "`r`n"
    
    # ============ 6. Add stop-loss check blocks (line-by-line) ============
    if ($content -notmatch '止损检查') {
        $lines = $content -split "`r`n"
        $newLines = @()
        $slCount = 0
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $newLines += $lines[$i]
            # Detect: "else if (s.Status == 1)" followed by "{" then a comment line
            # We look for the comment line AFTER the opening brace
            if ($i -ge 2 -and $lines[$i] -match '^\s+//' -and $lines[$i-1] -match '^\s+\{' -and $lines[$i-2] -match 'else if \(s\.Status == 1\)') {
                $indent = ""
                if ($lines[$i] -match '^(\s+)') { $indent = $matches[1] }
                # Determine sendMode: check if file uses sendMode variable or literal 0
                $smVar = "sendMode"
                # Insert stop-loss block BEFORE the comment (replace last added line, insert before it)
                $commentLine = $newLines[$newLines.Length - 1]
                $newLines[$newLines.Length - 1] = "${indent}// 止损检查"
                $newLines += "${indent}var _sl = (decimal)ArgDic[""stopLoss""];"
                $newLines += "${indent}if (_sl > 0 && s.EntryPrice > 0 && q.Close < s.EntryPrice * (1 - _sl / 100m))"
                $newLines += "${indent}{"
                $newLines += "${indent}    Trade(tu.MktSymbol, OrderType.SELL_TO_COVER, q.Close, s.Num, period, ${smVar});"
                $newLines += "${indent}    s.Status = 0; s.Num = 0; s.EntryPrice = 0;"
                $newLines += "${indent}    return;"
                $newLines += "${indent}}"
                $newLines += ""
                $newLines += $commentLine
                $slCount++
                $modified = $true
            }
            elseif ($i -ge 2 -and $lines[$i] -match '^\s+//' -and $lines[$i-1] -match '^\s+\{' -and $lines[$i-2] -match 'else if \(s\.Status == 2\)') {
                $indent = ""
                if ($lines[$i] -match '^(\s+)') { $indent = $matches[1] }
                $smVar = "sendMode"
                $commentLine = $newLines[$newLines.Length - 1]
                $newLines[$newLines.Length - 1] = "${indent}// 止损检查"
                $newLines += "${indent}var _sl2 = (decimal)ArgDic[""stopLoss""];"
                $newLines += "${indent}if (_sl2 > 0 && s.EntryPrice > 0 && q.Close > s.EntryPrice * (1 + _sl2 / 100m))"
                $newLines += "${indent}{"
                $newLines += "${indent}    Trade(tu.MktSymbol, OrderType.BUY_TO_COVER, q.Close, s.Num, period, ${smVar});"
                $newLines += "${indent}    s.Status = 0; s.Num = 0; s.EntryPrice = 0;"
                $newLines += "${indent}    return;"
                $newLines += "${indent}}"
                $newLines += ""
                $newLines += $commentLine
                $slCount++
                $modified = $true
            }
        }
        $content = $newLines -join "`r`n"
        if ($slCount -gt 0) {
            Write-Output "  -> Inserted $slCount stop-loss check blocks"
        } else {
            Write-Output "  -> WARNING: Could not auto-insert stop-loss checks (non-standard pattern)"
        }
    }
    
    # Fix sendMode in stop-loss Trade calls if file uses literal 0
    if ($content -match 'Trade\([^)]+period, 0\)' -and $content -notmatch 'int sendMode') {
        $content = $content -replace 'period, sendMode\)', 'period, 0)'
    }
    
    if ($modified) {
        [System.IO.File]::WriteAllText($file, $content, [System.Text.Encoding]::UTF8)
        Write-Output "UPDATED: $fileName"
    } else {
        Write-Output "NO CHANGE: $fileName"
    }
}

Write-Output "`nScript completed."
