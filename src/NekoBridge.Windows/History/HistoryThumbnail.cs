using System;
using System.Buffers.Binary;
using System.IO;
using System.Windows.Media.Imaging;

namespace NekoBridge.Windows.History;

internal static class HistoryThumbnail
{
    // The store contains validated PNGs. Inspect the dimensions before decoding
    // so tall/narrow images are bounded too, not just their width.
    internal static BitmapSource Decode(Stream stream)
    {
        Span<byte> header=stackalloc byte[24];stream.ReadExactly(header);
        if(!header[..8].SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))throw new InvalidDataException("历史图片格式无效。");
        int width=BinaryPrimitives.ReadInt32BigEndian(header[16..20]),height=BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if(width<1||height<1||width>16384||height>16384||(long)width*height>16777216)throw new InvalidDataException("历史图片尺寸无效。");
        stream.Position=0;
        var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
        if(width>320||height>192)
        {
            if(width/320d>=height/192d)bitmap.DecodePixelWidth=320;
            else bitmap.DecodePixelHeight=192;
        }
        bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;
    }
}
