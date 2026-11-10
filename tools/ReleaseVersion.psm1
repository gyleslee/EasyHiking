function Get-NextSoftwareVersion {
    param([Parameter(Mandatory=$true)][string]$Version)
    if($Version -notmatch '^(0|[1-9][0-9]*)\.([0-9])\.([0-9])$'){throw '版本应为三段数字，后两段各为0至9，例如1.8.0。'}
    $parts=$Version.Split('.')|ForEach-Object{[int]$_}
    $parts[2]++
    if($parts[2]-ge 10){$parts[2]=0;$parts[1]++}
    if($parts[1]-ge 10){$parts[1]=0;$parts[0]++}
    $parts -join '.'
}
Export-ModuleMember -Function Get-NextSoftwareVersion
