using System.Windows;
using System.Windows.Interop;

namespace Microsoft.Win32;

/// <summary>使用Win7自带的系统文件夹选择器，保持DEM挂载交互。</summary>
public sealed class OpenFolderDialog
{
    public string Title {get;set;}="选择文件夹";
    public string FolderName {get;private set;}="";
    public bool? ShowDialog(Window owner)
    {
        using var dialog=new System.Windows.Forms.FolderBrowserDialog{Description=Title,UseDescriptionForTitle=true};
        if(dialog.ShowDialog(new OwnerWindow(new WindowInteropHelper(owner).Handle))!=System.Windows.Forms.DialogResult.OK)return false;
        FolderName=dialog.SelectedPath;return true;
    }
    private sealed class OwnerWindow(IntPtr handle):System.Windows.Forms.IWin32Window {public IntPtr Handle=>handle;}
}
