$ErrorActionPreference='Stop'
$path=Join-Path $PSScriptRoot 'Common.iss'
$s=(Get-Content -LiteralPath $path -Raw).Replace("`r`n","`n")
$s=$s.Replace('MinVersion=10.0.17763','MinVersion=6.1sp1')
if(!$s.Contains('#include "TargetSystem.iss"')){$s=$s.Replace('function UseLegacyBuild: Boolean;','#include "TargetSystem.iss"'+"`n`n"+'function UseLegacyBuild: Boolean;')}
$s=$s.Replace('var Report, PreviousDirectory: String; Version: TWindowsVersion;','var Report, PreviousDirectory, SystemError: String; Version: TWindowsVersion;')
$s=[regex]::Replace($s,"^  if UseLegacyBuild and \(\(Version\.Major<>6\).*?Exit;end;\n",'',[Text.RegularExpressions.RegexOptions]::Multiline)
$s=[regex]::Replace($s,"^  if \(Version\.Major=10\) and \(Version\.Build<17763\).*?Exit;end;\n",'',[Text.RegularExpressions.RegexOptions]::Multiline)
if(!$s.Contains('SystemError := TargetSystemError')){
 $s=$s.Replace('  GetWindowsVersionEx(Version);',@'
  GetWindowsVersionEx(Version);
  Log('系统检测：'+IntToStr(Version.Major)+'.'+IntToStr(Version.Minor)+'.'+IntToStr(Version.Build)+'；64位='+IntToStr(Ord(IsWin64)));
  SystemError := TargetSystemError(UseLegacyBuild,Version.Major,Version.Minor,Version.Build,Version.ServicePackMajor,IsWin64);
  if SystemError<>'' then begin SuppressibleMsgBox(SystemError,mbError,MB_OK,IDOK);Result:=False;Exit;end;
'@)
}
if(!$s.Contains('[Messages]')){
 $s=$s.Replace('[Tasks]',@'
[Messages]
#if !TargetLegacy
WindowsVersionNotSupported=此安装包需要64位x64 Windows 10（1607/14393或更高版本）或Windows 11。请核对系统版本和位数，并取消兼容模式后重试。
#endif
[Tasks]
'@)
}
[IO.File]::WriteAllText($path,$s,[Text.UTF8Encoding]::new($true))
