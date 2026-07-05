param([string]$LogPath)
$ErrorActionPreference='Continue'
$bin='e:\project\qjy\QjySdk\QjySDK.Tests\bin\Release\net9.0'
Add-Type -Path (Join-Path $bin 'QjySDK.dll')
Add-Type -Path (Join-Path $bin 'QjySDK.Tests.dll')
function Log([string]$msg){ $line="[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"; Add-Content -Path $LogPath -Value $line; Write-Output $line }
Log 'START BollRsiShortReversion realtime SIMULATION; no trader connection; no real Polymarket order placement; Polymarket proxy requirement=http://127.0.0.1:7888'
$period=[Model.EnumDef+Period]::TIME_5M
$raw='COIN_FUTURES_ETHUSDT'
$mkt='FUTURES_ETHUSDT'
$stg=[QjySDK.Stg.BollRsiShortReversion]::new('BollRsiShortReversion_RealtimeSim')
$sd=$stg.GetStgDesc()
$argProp=[QjySDK.Stg.StgBase].GetProperty('ArgDic',[System.Reflection.BindingFlags]'NonPublic,Instance')
$argProp.SetValue($stg,$sd.ArgDic)
$args=$argProp.GetValue($stg)
$args['lotsMode']=0
$args['lots']=1.0
$args['money']=10000.0
$args['polyNum']=0.0
$args['sendMode']=0
$args['mode']=2
$tu=[Common.TableUnit]::new()
$tu.MktSymbol=$mkt
$tu.Period=$period
$tu.QuoteList=[System.Collections.Generic.List[Common.SkQuote]]::new()
$rtrField=[QjySDK.Stg.StgBase].GetField('_rtr',[System.Reflection.BindingFlags]'NonPublic,Instance')
$lastFinal=[DateTime]::MinValue
while($true){
  try{
    $quotes=[QjySDK.Tests.TDEngineDataLoader]::LoadKlines($raw,$period,300)
    if($quotes -eq $null -or $quotes.Count -lt 60){ Log "WAIT insufficient bars count=$($quotes.Count)"; Start-Sleep -Seconds 30; continue }
    $final=$quotes[$quotes.Count-1]
    if($final.Date -le $lastFinal){ Start-Sleep -Seconds 30; continue }
    $tu.QuoteList.Clear()
    foreach($q in $quotes){ [void]$tu.QuoteList.Add($q) }
    $stg.OnBar($period,$tu,$true,$null)
    $records=$rtrField.GetValue($stg)
    $signal='none'
    if($records.Count -gt 0){
      foreach($r in @($records)){
        Log "SIM_ORDER time=$($final.Date.ToString('yyyy-MM-dd HH:mm:ss')) symbol=$($r.MktSymbol) ot=$($r.OT) price=$($r.Price) num=$($r.Num) period=$($r.P) sendMode=$($r.SendMode)"
      }
      $records.Clear()
      $signal='order'
    }
    Log "BAR time=$($final.Date.ToString('yyyy-MM-dd HH:mm:ss')) o=$($final.Open) h=$($final.High) l=$($final.Low) c=$($final.Close) signal=$signal"
    $lastFinal=$final.Date
  } catch { Log "ERROR $($_.Exception.GetType().FullName): $($_.Exception.Message)" }
  Start-Sleep -Seconds 30
}
