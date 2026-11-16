using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NekoBridge.Windows;

internal static class ImageMemorySmoke
{
    internal static void Run()
    {
        const int width=1920,height=1080;
        var pixels=new byte[width*height*4];new Random(7).NextBytes(pixels);
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,pixels,width*4);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(source));
        using var output=new MemoryStream();encoder.Save(output);var png=output.ToArray();
        string Expected()
        {
            using var stream=new MemoryStream(png,false);var decoder=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var image=new FormatConvertedBitmap(decoder.Frames[0],PixelFormats.Bgra32,null,0);
            var scratch=new byte[width*height*4];image.CopyPixels(scratch,width*4,0);
            return RichClipboard.Digest($"image:{width}:{height}:"+Convert.ToHexString(SHA256.HashData(scratch)));
        }
        string expected=Expected();var result=RichClipboard.Image(png);
        Check(result.Fingerprint==expected,"bounded strips must preserve the original image fingerprint");
        long before=GC.GetAllocatedBytesForCurrentThread();Expected();long oldBytes=GC.GetAllocatedBytesForCurrentThread()-before;
        before=GC.GetAllocatedBytesForCurrentThread();RichClipboard.Image(png);long newBytes=GC.GetAllocatedBytesForCurrentThread()-before;
        Console.WriteLine($"1920x1080 image identity managed allocation: {oldBytes:N0} -> {newBytes:N0} bytes");
        Check(newBytes<oldBytes-width*height*3L,"full scratch array removed");
        var native=result.CreateImageData();
        var v5=native.GetData(System.Windows.DataFormats.GetDataFormat(17).Name,false) as MemoryStream??throw new Exception("Missing DIBV5");
        var dib=native.GetData("DeviceIndependentBitmap",false) as MemoryStream??throw new Exception("Missing DIB");
        Check(v5.Length==124+pixels.Length&&dib.Length==40+pixels.Length,"native formats not dropped");
        var a=v5.ToArray();var b=dib.ToArray();
        for(int row=0;row<height;row++)
        {
            Check(a.AsSpan(124+row*width*4,width*4).SequenceEqual(pixels.AsSpan(row*width*4,width*4)),"DIBV5 pixels/alpha unchanged");
            Check(b.AsSpan(40+(height-row-1)*width*4,width*4).SequenceEqual(pixels.AsSpan(row*width*4,width*4)),"legacy DIB row orientation unchanged");
        }
        Console.WriteLine($"PASS bounded image hash, preserved BGRA/alpha and native formats; write scratch eliminated: {pixels.Length:N0} bytes per 1080p image");
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
