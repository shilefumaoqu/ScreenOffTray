using System;
using System.Runtime.InteropServices;
namespace ScreenOffTray.SettingsUi {
 [StructLayout(LayoutKind.Sequential)]internal struct SizingRect {internal int Left,Top,Right,Bottom;}
 internal struct PixelSize {internal int Width,Height;internal PixelSize(int w,int h){Width=w;Height=h;}}
 internal struct AspectLimits {internal double MinimumWidth,MaximumWidth;}
 internal static class WindowSizingPolicy {
  internal const double Ratio=7.0/5.0;
  internal static AspectLimits Limits(double workWidth,double workHeight,double frameWidth,double frameHeight,double scale) {
   double max=Math.Max(1,Math.Min(workWidth-frameWidth,(workHeight-frameHeight)*Ratio));
   return new AspectLimits{MinimumWidth=Math.Min(980*scale,max),MaximumWidth=max};
  }
  internal static PixelSize Constrain(double width,double height,double previousWidth,double previousHeight,int edge,AspectLimits limits) {
   double proposed;
   if(edge==1 || edge==2)proposed=width;
   else if(edge==3 || edge==6)proposed=height*Ratio;
   else if(edge>=4 && edge<=8)proposed=Math.Abs(width-previousWidth)>=Math.Abs(height-previousHeight)*Ratio?width:height*Ratio;
   else if(Math.Abs(width-previousWidth)<1 && Math.Abs(height-previousHeight)>=1)proposed=height*Ratio;
   else if(Math.Abs(height-previousHeight)<1 && Math.Abs(width-previousWidth)>=1)proposed=width;
   else proposed=Math.Min(width,height*Ratio);
   proposed=Math.Max(limits.MinimumWidth,Math.Min(limits.MaximumWidth,proposed));
   int w=Math.Max(1,(int)Math.Round(proposed));return new PixelSize(w,Math.Max(1,(int)Math.Round(w/Ratio)));
  }
  internal static SizingRect Anchor(SizingRect value,int edge,PixelSize client,int frameWidth,int frameHeight) {
   int width=client.Width+frameWidth,height=client.Height+frameHeight;
   if(edge==1 || edge==4 || edge==7)value.Left=value.Right-width;else value.Right=value.Left+width;
   if(edge==3 || edge==4 || edge==5)value.Top=value.Bottom-height;else value.Bottom=value.Top+height;
   return value;
  }
 }
}
