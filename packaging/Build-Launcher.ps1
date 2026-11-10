param([Parameter(Mandatory=$true)][string]$PayloadDirectory,[Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference='Stop'
if($Version-notmatch'^\d+\.[0-9]\.[0-9]$'){throw '启动器版本格式无效'}
$payload=(Resolve-Path -LiteralPath $PayloadDirectory).Path
$dll=Join-Path $payload '易行徒步规划制作器.dll'
if([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion-ne($Version+'.0')){throw '载荷与启动器版本不同'}
$config=Get-Content -LiteralPath (Join-Path $payload '易行徒步规划制作器.runtimeconfig.json') -Raw|ConvertFrom-Json
if($config.runtimeOptions.tfm-ne'net10.0'){throw '此启动器只用于Win10/11框架依赖构建'}
$temp=Join-Path $PSScriptRoot ('build\launcher-'+$Version)
New-Item -ItemType Directory -Path $temp -Force|Out-Null
$metadata=Join-Path $temp 'Version.cs'
[IO.File]::WriteAllText($metadata,('[assembly: System.Reflection.AssemblyVersion("'+$Version+'.0")][assembly: System.Reflection.AssemblyFileVersion("'+$Version+'.0")]'),[Text.UTF8Encoding]::new($true))
$source=Join-Path $PSScriptRoot 'launcher\Program.cs'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output=Join-Path $temp '易行徒步规划制作器.exe'
$icon=Join-Path $PSScriptRoot '..\src\HikingGuide.App\Assets\AppIcon.ico'
& $compiler /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ ('/out:'+$output) ('/win32icon:'+$icon) $source $metadata
if($LASTEXITCODE-ne0){throw '启动器编译失败'}
Copy-Item -LiteralPath $output -Destination (Join-Path $payload '易行徒步规划制作器.exe') -Force
[pscustomobject]@{Version=$Version;File=(Join-Path $payload '易行徒步规划制作器.exe');Sha256=(Get-FileHash -LiteralPath $output).Hash;Host='Installed x64 dotnet.exe';GpuFlagsChanged=$false}
