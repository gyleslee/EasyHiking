[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [ValidateSet('Function','Packaging')][string]$ChangeType='Packaging',
    [string]$Version,
    [string[]]$AdditionalSourceRoot=@()
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ReleaseVersion.psm1') -Force
$sourceRoot=Split-Path -Parent $PSScriptRoot
$roots=@($sourceRoot)+$AdditionalSourceRoot
$propsPath=Join-Path $sourceRoot 'Directory.Build.props'
$props=[xml](Get-Content -LiteralPath $propsPath -Raw)
$current=[string]$props.Project.PropertyGroup.Version
if($Version){
    if($Version-notmatch'^(0|[1-9][0-9]*)\.([0-9])\.([0-9])$'){throw '指定版本必须为三段数字，后两段为0至9。'}
    $next=$Version
}elseif($ChangeType-eq'Function'){$next=Get-NextSoftwareVersion $current}else{$next=$current}
$changes=@()
foreach($root in $roots){
    $root=(Resolve-Path -LiteralPath $root).Path
    $propFile=Join-Path $root 'Directory.Build.props'
    $xml=[xml](Get-Content -LiteralPath $propFile -Raw)
    $old=[string]$xml.Project.PropertyGroup.Version
    if($old-ne $current){throw '两套源码版本不一致，请先核对，未写入任何文件。'}
    $files=@($propFile)
    foreach($relative in @('src\HikingGuide.Core\Software\SoftwareIdentity.cs','tests\HikingGuide.Core.Tests\SoftwareVersionTests.cs','src\HikingGuide.App\App.InstallationSmoke.cs','src\HikingGuide.App\App.T10Smoke.cs')){
        $p=Join-Path $root $relative;if(Test-Path -LiteralPath $p){$files+=$p}
    }
    $manual='F:\Hiking Guide\易行徒步规划制作器\docs'
    # 操作说明使用固定无版本标题；软件升版不改写用户手册。
    foreach($file in $files){
        $text=[IO.File]::ReadAllText($file)
        if($file-eq$propFile){
            $updated=[regex]::Replace($text,'<Version>[^<]+</Version>',('<Version>'+$next+'</Version>'))
            $updated=[regex]::Replace($updated,'<(AssemblyVersion|FileVersion)>[^<]+</\1>',{param($m)'<'+$m.Groups[1].Value+'>'+$next+'.0</'+$m.Groups[1].Value+'>'})
            $updated=[regex]::Replace($updated,'<InformationalVersion>[^<]+</InformationalVersion>',('<InformationalVersion>V'+$next+'</InformationalVersion>'))
        }else{
            $updated=$text.Replace('T'+$old,'V'+$next).Replace('V'+$old,'V'+$next).Replace('"'+$old+'.0"','"'+$next+'.0"')
            if([IO.Path]::GetExtension($file)-in @('.html','.md')){
                $updated=[regex]::Replace($text,('(?<![\d.])(?<prefix>[TV]?)'+[regex]::Escape($old)+'(?![\d.])'),{param($m)if($m.Groups['prefix'].Value){'V'+$next}else{$next}})
            }
            if($file.EndsWith('SoftwareVersionTests.cs')){
                $oldParts=$old.Split('.');$newParts=$next.Split('.')
                $pattern='new Version\(\s*'+$oldParts[0]+'\s*,\s*'+$oldParts[1]+'\s*,\s*'+$oldParts[2]+'\s*,\s*0\s*\)'
                $updated=[regex]::Replace($updated,$pattern,('new Version('+($newParts-join',')+',0)'))
            }
        }
        if($updated-ne$text){$changes+=[pscustomobject]@{Path=$file;Before=$text;After=$updated}}
    }
}
if($changes.Count-and $PSCmdlet.ShouldProcess(($roots-join'; '),('版本 '+$current+' → '+$next))){
    $backup=Join-Path $sourceRoot ('artifacts\version-backups\'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
    New-Item -ItemType Directory -Path $backup -Force|Out-Null
    $i=0;foreach($entry in $changes){[IO.File]::WriteAllText((Join-Path $backup ($i.ToString()+'.txt')),$entry.Before);$i++}
    $changes|Select-Object Path|ConvertTo-Json|Set-Content (Join-Path $backup 'files.json') -Encoding utf8
    foreach($entry in $changes){[IO.File]::WriteAllText($entry.Path,$entry.After,[Text.UTF8Encoding]::new($true))}
}
[pscustomobject]@{ChangeType=$ChangeType;PreviousVersion=$current;Version=$next;ChangedFiles=$changes.Count;NextFunctionVersion=(Get-NextSoftwareVersion $next)}


