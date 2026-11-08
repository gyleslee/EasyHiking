const
  DataAppId = 'EasyHiking-Installed-0EE343F3-212F-4B44-ABBF-7FF0B80AB4E7';
  ProfileListKey = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList';
var
  ProfileRoots, ProfileSids, DeleteRoots, DeleteSids: TArrayOfString;
  UserDataDrive: String;

function GetFileAttributesForData(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function ConvertDataSecurity(const Sddl: String; Revision: LongWord; var Descriptor: LongWord; var Size: LongWord): Boolean;
  external 'ConvertStringSecurityDescriptorToSecurityDescriptorW@advapi32.dll stdcall';
function SetDataSecurity(const FileName: String; Information: LongWord; Descriptor: LongWord): Boolean;
  external 'SetFileSecurityW@advapi32.dll stdcall';
function FreeDataSecurity(Descriptor: LongWord): LongWord;
  external 'LocalFree@kernel32.dll stdcall';

function IsUserSid(const Sid: String): Boolean;
var I: Integer;
begin
  Result := Pos('S-1-5-21-', Sid) = 1;
  if not Result then Exit;
  for I := 1 to Length(Sid) do
    if not ((Sid[I] >= '0') and (Sid[I] <= '9')) and (Sid[I] <> '-') and (Sid[I] <> 'S') then begin Result := False; Exit; end;
end;

function DataPathHasLink(const Path: String): Boolean;
var P: String; Attributes: LongWord;
begin
  P := RemoveBackslashUnlessRoot(ExpandFileName(Path)); Result := False;
  while Length(P) > 3 do begin
    Attributes := GetFileAttributesForData(P);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then begin Result := True; Exit; end;
    P := RemoveBackslashUnlessRoot(ExtractFileDir(P));
  end;
end;

function ProfileDataPath(const Drive, Sid, Profile: String): String;
var LocalPath, Value: String;
begin
  LocalPath := AddBackslash(Profile) + 'AppData\Local';
  if RegQueryStringValue(HKU, Sid + '\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders', 'Local AppData', Value) then begin
    if Pos('%userprofile%', Lowercase(Value)) = 1 then Value := Profile + Copy(Value, 14, MaxInt);
    if (Pos('%', Value) = 0) and (Length(Value) > 3) and (Value[2] = ':') then LocalPath := Value;
  end;
  Result := Drive + Copy(LocalPath, 3, MaxInt) + '\EasyHiking';
end;

procedure AddProfileRoot(const Sid, Root: String);
var N: Integer;
begin
  N := GetArrayLength(ProfileRoots); SetArrayLength(ProfileRoots, N+1); SetArrayLength(ProfileSids, N+1);
  ProfileRoots[N] := Root; ProfileSids[N] := Sid;
end;

procedure EnumerateDataProfiles;
var Sids: TArrayOfString; I: Integer; Profile, Root: String;
begin
  UserDataDrive := ExtractFileDrive(ExpandConstant('{app}'));
  SetArrayLength(ProfileRoots, 0); SetArrayLength(ProfileSids, 0);
#ifdef DATA_POLICY_TEST
#ifdef DATA_POLICY_ACL_TEST
  AddProfileRoot('{#ProbeUserSid}', ExpandConstant('{app}') + '\fixture\EasyHiking');
#else
  AddProfileRoot('S-1-5-21-100-100-100-1001', ExpandConstant('{app}') + '\fixture\EasyHiking');
#endif
#else
  if RegGetSubkeyNames(HKLM64, ProfileListKey, Sids) then
    for I := 0 to GetArrayLength(Sids)-1 do
      if IsUserSid(Sids[I]) and RegQueryStringValue(HKLM64, ProfileListKey + '\' + Sids[I], 'ProfileImagePath', Profile) then begin
        if Pos('%SystemDrive%', Profile) = 1 then Profile := ExpandConstant('{sd}') + Copy(Profile, 14, MaxInt);
        if (Length(Profile)>3) and (Profile[2]=':') and (Pos('%',Profile)=0) then begin
          Root := ProfileDataPath(UserDataDrive, Sids[I], Profile);
          if not DataPathHasLink(Root) then AddProfileRoot(Sids[I],Root);
        end;
      end;
#endif
end;

procedure PrepareUserDataPermissions;
var I: Integer; Descriptor, Size: LongWord; Root, Sddl: String;
begin
  EnumerateDataProfiles;
  for I := 0 to GetArrayLength(ProfileRoots)-1 do begin
    Root := ProfileRoots[I];
    if DataPathHasLink(Root) then RaiseException('用户配置目录包含链接，无法安全初始化：' + Root);
    if not ForceDirectories(Root) then RaiseException('无法创建用户配置目录：' + Root);
#if !Defined(DATA_POLICY_TEST) || Defined(DATA_POLICY_ACL_TEST)
    Sddl := 'D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1301bf;;;' + ProfileSids[I] + ')';
    Descriptor := 0; Size := 0;
    if not ConvertDataSecurity(Sddl, 1, Descriptor, Size) then RaiseException('用户配置目录权限描述无效。');
    try
      if not SetDataSecurity(Root, $80000004, Descriptor) then RaiseException('未能设置个人配置目录写入权限：' + Root);
    finally FreeDataSecurity(Descriptor); end;
#endif
    // 此标记仅声明软件所属目录；后续卸载仍要核对路径、用户和用户选择。
    if not SetIniString('Data','AppId',DataAppId,Root + '\EasyHiking.Data.ini') or
       not SetIniString('Data','OwnerSid',ProfileSids[I],Root + '\EasyHiking.Data.ini') then
      RaiseException('用户配置目录标记未能保存：' + Root);
  end;
end;

function IsOwnedDataRoot(const Root, Sid: String): Boolean;
var I: Integer; Expected: Boolean;
begin
  Expected := False;
  for I := 0 to GetArrayLength(ProfileRoots)-1 do
    if (CompareText(Root,ProfileRoots[I])=0) and (Sid=ProfileSids[I]) then Expected := True;
  Result := Expected and IsUserSid(Sid) and not DataPathHasLink(Root) and
    (CompareText(ExtractFileName(Root),'EasyHiking')=0) and
    (CompareText(ExtractFileDrive(Root),UserDataDrive)=0) and
    (GetIniString('Data','AppId','',Root + '\EasyHiking.Data.ini')=DataAppId) and
    (GetIniString('Data','OwnerSid','',Root + '\EasyHiking.Data.ini')=Sid);
end;

function ChooseUserDataRemoval: Boolean;
var Form: TSetupForm; Text: TNewStaticText; List: TNewCheckListBox; KeepButton, CancelButton: TNewButton;
    I, N, P: Integer; OwnedRoots, OwnedSids: TArrayOfString;
begin
  Result := True; EnumerateDataProfiles;
  SetArrayLength(DeleteRoots,0); SetArrayLength(DeleteSids,0);
  for I := 0 to GetArrayLength(ProfileRoots)-1 do
    if IsOwnedDataRoot(ProfileRoots[I],ProfileSids[I]) then begin
      N:=GetArrayLength(OwnedRoots);SetArrayLength(OwnedRoots,N+1);SetArrayLength(OwnedSids,N+1);
      OwnedRoots[N]:=ProfileRoots[I];OwnedSids[N]:=ProfileSids[I];
    end;
  if GetArrayLength(OwnedRoots)=0 then Exit;
  if UninstallSilent then begin
    if ExpandConstant('{param:DELETEUSERDATA|0}')='1' then begin DeleteRoots:=OwnedRoots;DeleteSids:=OwnedSids;end;
    Exit;
  end;
  Form:=CreateCustomForm(ScaleX(690),ScaleY(350),False,True);Form.Caption:='卸载易行徒步规划制作器';Form.ClientWidth:=ScaleX(690);Form.ClientHeight:=ScaleY(350);
  try
    Text:=TNewStaticText.Create(Form);Text.Parent:=Form;Text.SetBounds(ScaleX(18),ScaleY(18),ScaleX(650),ScaleY(76));Text.WordWrap:=True;
    Text.Caption:='是否同时删除用户配置文件夹？默认全部保留。' + #13#10 +
      '勾选下列用户目录后，将删除其中的配置、收藏点与图片、下载的路由数据和缓存。外部挂载DEM及外部项目文件不在删除范围内。';
    List:=TNewCheckListBox.Create(Form);List.Parent:=Form;List.SetBounds(ScaleX(18),ScaleY(104),ScaleX(650),ScaleY(165));
    for I:=0 to GetArrayLength(OwnedRoots)-1 do List.AddCheckBox(OwnedRoots[I],'',0,False,True,False,True,nil);
    KeepButton:=TNewButton.Create(Form);KeepButton.Parent:=Form;KeepButton.Caption:='按所选项继续卸载';KeepButton.SetBounds(ScaleX(432),ScaleY(298),ScaleX(145),ScaleY(30));KeepButton.ModalResult:=mrOk;KeepButton.Default:=True;
    CancelButton:=TNewButton.Create(Form);CancelButton.Parent:=Form;CancelButton.Caption:='取消卸载';CancelButton.SetBounds(ScaleX(588),ScaleY(298),ScaleX(80),ScaleY(30));CancelButton.ModalResult:=mrCancel;CancelButton.Cancel:=True;
    Result:=Form.ShowModal=mrOk;if not Result then Exit;
    for I:=0 to GetArrayLength(OwnedRoots)-1 do if List.Checked[I] then begin
      P:=GetArrayLength(DeleteRoots);SetArrayLength(DeleteRoots,P+1);SetArrayLength(DeleteSids,P+1);
      DeleteRoots[P]:=OwnedRoots[I];DeleteSids[P]:=OwnedSids[I];
    end;
  finally Form.Free;end;
end;

procedure RemoveSelectedUserData;
var I: Integer; Failed: String;
begin
  Failed:='';
  for I:=0 to GetArrayLength(DeleteRoots)-1 do begin
    if not IsOwnedDataRoot(DeleteRoots[I],DeleteSids[I]) then Failed:=Failed+DeleteRoots[I]+'（路径或归属核对失败）'+#13#10
    else if not DelTree(DeleteRoots[I],True,True,True) then Failed:=Failed+DeleteRoots[I]+'（可能有文件占用或权限不足）'+#13#10;
  end;
  if Failed<>'' then SuppressibleMsgBox('以下用户配置目录未能完全清理，请关闭相关程序后手动检查：'+#13#10+Failed,mbInformation,MB_OK,IDOK);
end;
