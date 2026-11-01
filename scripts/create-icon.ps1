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
   using var canvas = new Bitmap(size*4,size*4,PixelFormat.Format32bppArgb);
   using var graphics = Graphics.FromImage(canvas);
   graphics.SmoothingMode = SmoothingMode.AntiAlias;
   graphics.ScaleTransform(size*4/256f,size*4/256f);
   using var shape = new GraphicsPath();
   shape.AddLine(73,9,178,9); shape.AddBezier(178,9,221,9,247,35,247,78);
   shape.AddLine(247,78,247,178); shape.AddBezier(247,178,247,222,221,247,178,247);
   shape.AddLine(178,247,78,247); shape.AddBezier(78,247,34,247,9,221,9,178);
   shape.AddLine(9,178,9,74); shape.AddBezier(9,74,9,33,32,9,73,9); shape.CloseFigure();
   using var fill = new LinearGradientBrush(new Point(0,0),new Point(205,256),Color.FromArgb(70,108,125),Color.FromArgb(20,39,56));
   fill.InterpolationColors = new ColorBlend { Colors = new[]{Color.FromArgb(70,108,125),Color.FromArgb(36,61,80),Color.FromArgb(20,39,56)}, Positions=new[]{0f,.52f,1f} };
   graphics.FillPath(fill,shape);
   using var rimFill = new LinearGradientBrush(new Point(0,0),new Point(205,256),Color.FromArgb(220,210,248,240),Color.FromArgb(135,157,194,209));
   rimFill.InterpolationColors=new ColorBlend {Colors=new[]{Color.FromArgb(220,210,248,240),Color.FromArgb(30,192,221,232),Color.FromArgb(135,157,194,209)},Positions=new[]{0f,.46f,1f}};
   using var rim = new Pen(rimFill,2); graphics.DrawPath(rim,shape);
   using var sail = new GraphicsPath();
   sail.AddLine(22,111,22,77); sail.AddBezier(22,77,22,42,41,22,77,22); sail.AddLine(77,22,173,22);
   sail.AddBezier(173,22,129,41,110,75,106,124); sail.AddBezier(106,124,77,110,49,107,22,111); sail.CloseFigure();
   using var sailFill = new LinearGradientBrush(new Point(22,22),new Point(170,130),Color.FromArgb(90,211,255,240),Color.FromArgb(4,121,203,201)); graphics.FillPath(sailFill,sail);
   using var ribbon = new GraphicsPath();
   ribbon.AddLine(78,65,105,65); ribbon.AddLine(105,65,105,143);
   ribbon.AddBezier(105,143,105,159,112,167,128,167); ribbon.AddLine(128,167,184,167);
   ribbon.AddLine(184,167,184,193); ribbon.AddLine(184,193,122,193);
   ribbon.AddBezier(122,193,93,193,78,177,78,148); ribbon.CloseFigure();
   using var shadow = new SolidBrush(Color.FromArgb(85,7,31,46));
   graphics.TranslateTransform(0,4); graphics.FillPath(shadow,ribbon); graphics.TranslateTransform(0,-4);
   using var ribbonFill = new LinearGradientBrush(new Point(78,65),new Point(184,193),Color.FromArgb(240,255,249),Color.FromArgb(119,201,197));
   ribbonFill.InterpolationColors=new ColorBlend {Colors=new[]{Color.FromArgb(240,255,249),Color.FromArgb(201,245,233),Color.FromArgb(119,201,197)},Positions=new[]{0f,.57f,1f}};
   graphics.FillPath(ribbonFill,ribbon);
   using var fold=new GraphicsPath(); fold.AddBezier(105,143,105,159,112,167,128,167); fold.AddLine(128,167,184,167); fold.AddLine(184,167,161,193); fold.AddLine(161,193,122,193); fold.AddBezier(122,193,113,193,106,191,100,187); fold.AddBezier(100,187,121,190,133,183,142,173); fold.AddLine(142,173,128,173); fold.AddBezier(128,173,111,173,101,163,105,143); fold.CloseFigure();
   using var foldFill=new SolidBrush(Color.FromArgb(105,124,205,198)); graphics.FillPath(foldFill,fold);
   using var gloss = new Pen(Color.FromArgb(115,231,255,249),2){StartCap=LineCap.Round,EndCap=LineCap.Round};
   using var light = new GraphicsPath(); light.AddLine(24,89,24,76); light.AddBezier(24,76,24,43,42,24,77,24); light.AddLine(77,24,156,24); graphics.DrawPath(gloss,light);
   using var lower = new Pen(Color.FromArgb(89,146,204,201),2){StartCap=LineCap.Round,EndCap=LineCap.Round};
   using var edge = new GraphicsPath(); edge.AddLine(166,224,179,224); edge.AddBezier(179,224,207,224,226,205,229,180); graphics.DrawPath(lower,edge);
   using var bitmap=new Bitmap(size,size,PixelFormat.Format32bppArgb);
   using(var output=Graphics.FromImage(bitmap)){output.InterpolationMode=InterpolationMode.HighQualityBicubic;output.CompositingMode=CompositingMode.SourceCopy;output.DrawImage(canvas,new Rectangle(0,0,size,size));}
   if(size==256) bitmap.Save(Path.ChangeExtension(path,".png"),ImageFormat.Png);
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
