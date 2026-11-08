// 与隔离探针共用的系统检查，不把产品名称或是否Windows 11当作判断条件。
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
