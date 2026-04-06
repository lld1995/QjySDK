$files = Get-ChildItem -Path 'e:\LocalWorkSpaces\Qjy\QjySDK\Stg' -Filter '*.cs' -Recurse
foreach ($file in $files) {
    $content = [System.IO.File]::ReadAllText($file.FullName)
    if ($content -match 'StgBase') {
        $hasStopLoss = $content -match 'stopLoss' -or 
                       $content -match 'hardStop' -or 
                       ($content -match 'EntryPrice' -and $content -match '0\.9[0-9]m') -or
                       ($content -match 'EntryPrice' -and $content -match '1\.0[0-9]m')
        if (-not $hasStopLoss) {
            Write-Output $file.FullName
        }
    }
}
