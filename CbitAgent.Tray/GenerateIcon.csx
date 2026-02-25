// Generates a minimal 16x16 + 32x32 ICO file with a blue "C" on white
using System;
using System.IO;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

void CreateIcon(string path)
{
    using var ms = new MemoryStream();
    var sizes = new[] { 16, 32 };
    
    // ICO header: reserved(2) + type=1(2) + count(2)
    ms.Write(new byte[] { 0, 0, 1, 0, (byte)sizes.Length, 0 }, 0, 6);
    
    var imageDataList = new List<byte[]>();
    int offset = 6 + sizes.Length * 16; // header + directory entries
    
    foreach (var size in sizes)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.FromArgb(0, 102, 204)); // CBIT blue
        
        using var font = new Font("Arial", size * 0.6f, FontStyle.Bold);
        using var brush = new SolidBrush(Color.White);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("C", font, brush, new RectangleF(0, 0, size, size), sf);
        
        using var pngMs = new MemoryStream();
        bmp.Save(pngMs, ImageFormat.Png);
        var pngData = pngMs.ToArray();
        imageDataList.Add(pngData);
        
        // Directory entry: width, height, colors, reserved, planes, bpp, size, offset
        ms.WriteByte(size == 256 ? (byte)0 : (byte)size);
        ms.WriteByte(size == 256 ? (byte)0 : (byte)size);
        ms.WriteByte(0); // color palette
        ms.WriteByte(0); // reserved
        ms.Write(BitConverter.GetBytes((short)1), 0, 2); // planes
        ms.Write(BitConverter.GetBytes((short)32), 0, 2); // bpp
        ms.Write(BitConverter.GetBytes(pngData.Length), 0, 4); // size
        ms.Write(BitConverter.GetBytes(offset), 0, 4); // offset
        offset += pngData.Length;
    }
    
    foreach (var data in imageDataList)
        ms.Write(data, 0, data.Length);
    
    File.WriteAllBytes(path, ms.ToArray());
}

CreateIcon("cbit.ico");
Console.WriteLine("Icon created: " + new FileInfo("cbit.ico").Length + " bytes");
