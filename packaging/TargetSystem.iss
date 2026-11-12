// Arm64需要Windows11的x64模拟；不按系统版本名称或Insider/LTSC名称排除。
function PackageArchitectureError(ArmPackage, NativeArm: Boolean): String;
begin
 Result := '';
 if ArmPackage and not NativeArm then Result := '此为ARM64专用包，普通Intel/AMD电脑请使用x64安装包。';
 if not ArmPackage and NativeArm then Result := '此为普通x64包，ARM64 Windows11请使用ARM64专用兼容包。';
end;

function TargetArchitectureError(Legacy, NativeArm, X64Compatible: Boolean; Major, Build: Cardinal): String;
begin
  Result := '';
  if Legacy and NativeArm then begin Result := 'Windows 7安装包仅适用于原生x64系统。'; Exit; end;
  if not Legacy and (not X64Compatible or (NativeArm and ((Major<10) or (Build<22000)))) then
    Result := '此安装包需要x64 Windows 10/11，或支持x64模拟的ARM64 Windows 11。Windows 10 ARM及32位系统不受支持。';
end;

function TargetSystemError(Legacy: Boolean; Major, Minor, Build, ServicePack: Cardinal; Win64: Boolean): String;
var Detected: String;
begin
  Result := '';
  Detected := IntToStr(Major)+'.'+IntToStr(Minor)+'.'+IntToStr(Build);
  if not Win64 then begin
    Result := '检测到32位Windows。此安装包和程序仅支持64位系统，请在64位Windows上安装。'; Exit;
  end;
  if Legacy then begin
    if (Major<>6) or (Minor<>1) or (ServicePack<1) then
      Result := '检测到系统版本'+Detected+'。此安装包仅适用于Windows 7 SP1 64位；Windows 10/11请使用对应安装包。';
    Exit;
  end;
  if Major<10 then begin
    Result := '检测到系统版本'+Detected+'。此安装包适用于Windows 10/11 64位。若实际为Windows 10/11，请取消安装包属性中的兼容模式后重试；Windows 7请使用Windows 7安装包。'; Exit;
  end;
  if (Major=10) and (Build<14393) then
    Result := '检测到系统版本'+Detected+'。运行环境至少需要Windows 10 1607（内部版本14393）或更高版本。请更新Windows后重试。';
end;
