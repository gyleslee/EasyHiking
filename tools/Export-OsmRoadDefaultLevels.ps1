[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$PayloadRoot,
 [ValidateSet('Modern','Win7')][string[]]$Branches=@('Modern','Win7'),
 [string]$SettingsFile='F:\Hiking Guide\易行徒步规划制作器\Data\settings.json'
)
$ErrorActionPreference='Stop'
$sourceHash=(Get-FileHash -LiteralPath $SettingsFile -Algorithm SHA256).Hash
$settings=Get-Content -LiteralPath $SettingsFile -Raw|ConvertFrom-Json -AsHashtable
$roads=$settings.osmRoads
if(!$roads -or !$roads.styles -or $roads.styles.Count-gt1000){throw 'F盘OSM道路设置缺失或无效，请先在现用软件保存路网设置。'}
if([int]$roads.displayRulesVersion-lt3){throw 'F盘道路设置尚未升级为每类型一行，请先打开新版软件并保存OSM路网设置。'}
$levels=[ordered]@{}
foreach($s in $roads.styles){
 if(!$s.id -or $levels.Contains($s.id)){throw 'OSM道路规则标识缺失或重复。'}
 $min=[int]$s.minZoom;$max=[int]$s.maxZoom
 if($min-lt0-or$max-lt$min-or$max-gt22){throw "OSM道路层级无效：$($s.id)"}
 $levels[$s.id]=[ordered]@{MinZoom=$min;MaxZoom=$max}
}
$json=$levels|ConvertTo-Json -Depth 4
foreach($branch in $Branches){
 $payload=Join-Path $PayloadRoot ('Payload'+$branch)
 if(!(Test-Path -LiteralPath $payload -PathType Container)){throw "程序载荷不存在：$payload"}
}
foreach($branch in $Branches){
 $folder=Join-Path $PayloadRoot ('Payload'+$branch+'\Defaults')
 New-Item -ItemType Directory -Path $folder -Force|Out-Null
 $file=Join-Path $folder 'osm-road-default-levels.json'
 [IO.File]::WriteAllText($file+'.tmp',$json,[Text.UTF8Encoding]::new($false))
 Move-Item -LiteralPath ($file+'.tmp') -Destination $file -Force
}
if((Get-FileHash -LiteralPath $SettingsFile -Algorithm SHA256).Hash-ne$sourceHash){throw '读取期间F盘设置发生变化，请重新制作默认层级。'}
[pscustomobject]@{Rules=$levels.Count;SourceSettings=$SettingsFile;SourceUnchanged=$true;Files=@($Branches|ForEach-Object{'Payload'+$_+'\Defaults\osm-road-default-levels.json'})}
