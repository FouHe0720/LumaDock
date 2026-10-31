$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\_common.ps1"
$LumaAssetPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LumaDock.App\Assets\LumaDock.ico'
$LumaIconSource = @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
public static class LumaIconWriter {
 public static void Write(string path) {
  var images = new byte[7][]; var sizes = new[]{16,24,32,48,64,128,256}; int imageIndex=0;
  foreach (var size in sizes) {
   using var bitmap = new Bitmap(size,size,PixelFormat.Format32bppArgb);
   using var graphics = Graphics.FromImage(bitmap);
   graphics.SmoothingMode = SmoothingMode.AntiAlias;
   graphics.ScaleTransform(size/256f,size/256f);
   using var shape = new GraphicsPath();
   shape.AddArc(8,8,104,104,180,90); shape.AddArc(144,8,104,104,270,90);
   shape.AddArc(144,144,104,104,0,90); shape.AddArc(8,144,104,104,90,90); shape.CloseFigure();
   using var fill = new LinearGradientBrush(new Point(0,8),new Point(210,256),Color.FromArgb(255,142,189,198),Color.FromArgb(255,54,98,128));
   graphics.FillPath(fill,shape);
   using var rim = new Pen(Color.FromArgb(170,255,255,255),3); graphics.DrawPath(rim,shape);
   using var gloss = new Pen(Color.FromArgb(110,255,255,255),2);
   graphics.DrawArc(gloss,18,18,98,98,184,86); graphics.DrawLine(gloss,66,18,190,18);
   using var stem = new Pen(Color.FromArgb(247,255,255,255),23) { StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round };
   graphics.DrawLines(stem,new[]{new Point(96,77),new Point(96,179),new Point(167,179)});
   using var stream = new MemoryStream(); bitmap.Save(stream,ImageFormat.Png); images[imageIndex++] = stream.ToArray();
  }
  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  using var file = File.Create(path); using var writer = new BinaryWriter(file);
  writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
  int offset=6+16*sizes.Length;
  for(int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)(sizes[i]==256?0:sizes[i]));writer.Write((byte)0);writer.Write((byte)0);writer.Write((ushort)1);writer.Write((ushort)32);writer.Write(images[i].Length);writer.Write(offset);offset+=images[i].Length; }
  foreach(var data in images) writer.Write(data);
 }
}
class Program { [STAThread] static void Main(string[] args) => LumaIconWriter.Write(args[0]); }
'@
$LumaIconWork = Join-Path $LumaRoot 'work\icon-generation'
New-Item -ItemType Directory -Path $LumaIconWork -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $LumaIconWork 'Program.cs'), $LumaIconSource)
[IO.File]::WriteAllText((Join-Path $LumaIconWork 'Icon.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><OutputType>Exe</OutputType><UseWindowsForms>true</UseWindowsForms></PropertyGroup></Project>')
Invoke-LumaDotnet -Arguments @('run', '--project', (Join-Path $LumaIconWork 'Icon.csproj'), '-c', 'Release', '--', $LumaAssetPath)
Write-Host $LumaAssetPath
