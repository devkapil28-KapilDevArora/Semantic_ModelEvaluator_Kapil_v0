param(
    [string]$Executable = "$PSScriptRoot\..\src\SemanticModelEvaluator.App\bin\Debug\net10.0-windows\Semantic_ModelEvaluator_Kapil_V0.exe"
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Join-Path $PSScriptRoot ('..\artifacts\smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$root = (Resolve-Path $root).Path
$process = Start-Process -FilePath (Resolve-Path $Executable).Path -ArgumentList @('--data-root', "`"$root`"") -PassThru
function Find-Control([string]$id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Until([scriptblock]$condition, [int]$seconds = 45) {
    $until = (Get-Date).AddSeconds($seconds)
    do { if (& $condition) { return }; Start-Sleep -Milliseconds 250 } while ((Get-Date) -lt $until)
    throw 'UI smoke step timed out.'
}
function Click([string]$id) {
    $control = Find-Control $id
    if (!$control) { throw "Missing control: $id" }
    $control.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Select-Tab([string]$name) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $name)
    $tab = $script:window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if (!$tab) { throw "Missing tab: $name" }
    $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
try {
    Write-Output 'Waiting for main window'
    Wait-Until { $process.Refresh(); $process.MainWindowHandle -ne 0 }
    $script:window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-Until { (Find-Control 'MainNavigation').Current.IsEnabled }

    Write-Output 'Two-model comparison: demo, run, review, download HTML'
    Select-Tab '00  Compare two models'
    Click 'PairDemo'
    Wait-Until { (Find-Control 'PairDatasetInfo').Current.Name -ne 'No dataset loaded.' -and (Find-Control 'PairRun').Current.IsEnabled }
    Click 'PairRun'
    Wait-Until { $h = Find-Control 'PairHeadline'; $h -and $h.Current.Name.Length -gt 0 } 90
    Write-Output ('Pair headline: ' + (Find-Control 'PairHeadline').Current.Name)
    if (!(Find-Control 'PairMetrics')) { throw 'Pair metric grid was not rendered.' }
    $report = Join-Path $root 'pair-report.html'
    Click 'PairDownload'
    $desktop = [Windows.Automation.AutomationElement]::RootElement
    $dialogCondition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty, '#32770'))
    $dialog = $null
    Wait-Until { $script:dialog = $desktop.FindFirst([Windows.Automation.TreeScope]::Descendants, $dialogCondition); $script:dialog -ne $null } 30
    Write-Output ('Dialog: ' + $script:dialog.Current.Name)
    $nameCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, '1001')
    Wait-Until { $script:nameBox = $script:dialog.FindFirst([Windows.Automation.TreeScope]::Descendants, $nameCondition); $script:nameBox -ne $null } 30
    Add-Type -AssemblyName System.Windows.Forms
    try { $script:dialog.SetFocus() } catch { }
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('%n')
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait(($report -replace '([+^%~(){}\[\]])', '{$1}'))
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Wait-Until { Test-Path -LiteralPath $report } 30
    $html = Get-Content -LiteralPath $report -Raw
    if ($html -notmatch 'Executive summary' -or $html -match '(src|href)\s*=\s*["'']https?://') { throw 'Detailed HTML report is incomplete or not self-contained.' }
    Write-Output "Pair report written: $report ($([math]::Round((Get-Item $report).Length / 1KB)) KB)"

    Write-Output 'Opening Dataset Studio'
    Select-Tab '02  Dataset Studio'
    Click 'LoadDemo'
    Write-Output 'Waiting for demo dataset and runner'
    Wait-Until { (Find-Control 'RunEvaluation') -and (Find-Control 'RunEvaluation').Current.IsEnabled }
    $checks = $script:window.FindAll([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::CheckBox))
    foreach ($check in $checks) { Write-Output ($check.Current.Name + ': ' + $check.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState) }
    $before = @(Get-ChildItem -LiteralPath (Join-Path $root 'runs') -Filter '*.json' -ErrorAction SilentlyContinue).Count
    Click 'RunEvaluation'
    Write-Output 'Waiting for completed run file'
    Wait-Until { @(Get-ChildItem -LiteralPath (Join-Path $root 'runs') -Filter '*.json' -ErrorAction SilentlyContinue).Count -gt $before }
    Wait-Until { (Find-Control 'MainNavigation').Current.IsEnabled }
    $run = Get-ChildItem -LiteralPath (Join-Path $root 'runs') -Filter '*.json' | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content -Raw | ConvertFrom-Json
    if ($run.models.Count -ne 2 -or $run.status -ne 'Completed') { throw "Expected two completed model runs; got $($run.status)" }
    if (!(Find-Control 'SideBySideResults') -and !(Find-Control 'ExplorerMetrics')) { throw 'Explorer results were not rendered.' }
    $check = Find-Control 'EnableThreshold'
    $check.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    (Find-Control 'ThresholdSlider').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(0.4)
    Click 'SaveScenario'
    Select-Tab '05  Comparator'
    Wait-Until { (Find-Control 'ComparisonGrid') -ne $null }
    Write-Output "PASS: compared two models and downloaded the detailed HTML; launched, loaded demo, ran two models, changed threshold, saved scenario, opened comparator. Isolated data: $root"
} finally {
    if ($script:window) {
        $status = Find-Control 'OperationStatus'
        if ($status) { Write-Output ('Last UI status: ' + $status.Current.Name) }
    }
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
}
