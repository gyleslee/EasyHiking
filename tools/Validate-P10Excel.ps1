param([Parameter(Mandatory=$true)][string]$Folder)
$ErrorActionPreference = 'Stop'
$excel = $null
$checks = @()
try {
    $excel = New-Object -ComObject Excel.Application
    $excel.Visible = $false
    $excel.DisplayAlerts = $false
    foreach ($file in @('F:\Hiking Guide\测试文件\攻略数据模板.xlsx', (Join-Path $Folder '环船百大-2方案.xlsx'), (Join-Path $Folder '环船百大-3方案6地点.xlsx'))) {
        $book = $null
        try {
            $book = $excel.Workbooks.Open($file, 0, $true)
            $sheet = $book.Worksheets.Item('攻略数据')
            $headers = @()
            for ($column=1; $column -le $sheet.UsedRange.Columns.Count; $column++) { $headers += [string]$sheet.Cells.Item(3,$column).Text }
            $planCount = @($headers | Where-Object { $_ -like '行进方案*' }).Count
            $arrivalCount = @($headers | Where-Object { $_ -like '*计划抵达时间*' }).Count
            $formulaCount = 0
            for ($row=5; $row -le 58; $row++) { for ($column=1; $column -le $sheet.UsedRange.Columns.Count; $column++) { if ($sheet.Cells.Item($row,$column).HasFormula) { $formulaCount++ } } }
            $template = $file -like '*攻略数据模板.xlsx'
            $nodeCount = if ($template) { 51 } else { 54 }
            $sheet.PageSetup.PrintArea = $sheet.UsedRange.Address()
            $sheet.PageSetup.Zoom = $false
            $sheet.PageSetup.FitToPagesWide = 1
            $sheet.PageSetup.FitToPagesTall = $false
            $pdf = Join-Path $Folder (([System.IO.Path]::GetFileNameWithoutExtension($file)) + '-Excel实体.pdf')
            $sheet.ExportAsFixedFormat(0, $pdf)
            $checks += [pscustomobject]@{ File=$file; ReadOnly=$book.ReadOnly; SheetCount=$book.Worksheets.Count; Columns=$sheet.UsedRange.Columns.Count; Rows=$sheet.UsedRange.Rows.Count; NodeCount=$nodeCount; Plans=$planCount; Arrivals=$arrivalCount; Formulas=$formulaCount; FirstNode=$sheet.Cells.Item(5,2).Text; FirstElevation=$sheet.Cells.Item(5,3).Value2; PDF=$pdf; Version=$excel.Version }
        } finally { if ($null -ne $book) { $book.Close($false); [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($book) } }
    }
    $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Folder 'Excel实体验证.json') -Encoding utf8
    $checks | ConvertTo-Json -Depth 4
} finally {
    if ($null -ne $excel) { $excel.Quit(); [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($excel) }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}
