using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SkiaSharp;
namespace HikingGuide.App.Services;
public static class ImageSaveService
{
    public static async Task SaveAsync(Window owner,BitmapSource source,int rotation,string name)
    {
        var dialog=new SaveFileDialog{Title="图片另存为",Filter="JPEG图片 (*.jpg)|*.jpg|PNG图片 (*.png)|*.png",FileName=string.Concat(Path.GetFileNameWithoutExtension(name).Take(100).Select(c=>Path.GetInvalidFileNameChars().Contains(c)?'_':c)),AddExtension=true,DefaultExt=".jpg",OverwritePrompt=true};
        if(dialog.ShowDialog(owner)!=true)return;
        try{var image=rotation==0?source:new TransformedBitmap(source,new RotateTransform(rotation));if(image.CanFreeze)image.Freeze();var bytes=await Task.Run(()=>Encode(image,Path.GetExtension(dialog.FileName)));await File.WriteAllBytesAsync(dialog.FileName,bytes);}
        catch(Exception error){MessageBox.Show(owner,"图片保存失败："+error.Message,"图片另存为",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    internal static byte[] Encode(BitmapSource source,string extension)
    {
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));using var stream=new MemoryStream();encoder.Save(stream);
        if(extension.Equals(".png",StringComparison.OrdinalIgnoreCase))return stream.ToArray();
        using var input=SKBitmap.Decode(stream.ToArray());using var output=new SKBitmap(input.Width,input.Height);using var canvas=new SKCanvas(output);canvas.Clear(SKColors.White);canvas.DrawBitmap(input,0,0);using var image=SKImage.FromBitmap(output);using var data=image.Encode(SKEncodedImageFormat.Jpeg,95);return data.ToArray();
    }
}
