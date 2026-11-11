using System;
using ScreenOffTray.SettingsUi;
internal static class WindowSizingPolicyTests {
 static int count;static void Check(bool ok,string text){if(!ok)throw new Exception(text);count++;}
 static int Main(){try{
  foreach(double scale in new[]{1.0,1.25,1.5,1.75,2.0})foreach(var work in new[]{new[]{1920.0,1080.0},new[]{1366.0,768.0},new[]{2880.0,1800.0}}){
   var limits=WindowSizingPolicy.Limits(work[0],work[1],16*scale,38*scale,scale);
   for(int edge=1;edge<=8;edge++)foreach(var desired in new[]{new[]{300.0,200.0},new[]{1600.0,500.0},new[]{800.0,1400.0}}){
    var size=WindowSizingPolicy.Constrain(desired[0],desired[1],1000*scale,700*scale,edge,limits);
    Check(Math.Abs(size.Width*5-size.Height*7)<=4,"aspect rounding");Check(size.Width>=Math.Round(limits.MinimumWidth)-1 && size.Width<=Math.Round(limits.MaximumWidth)+1,"min/max range");
    var rect=new SizingRect{Left=100,Top=200,Right=1300,Bottom=1000};var fixedRect=WindowSizingPolicy.Anchor(rect,edge,size,(int)(16*scale),(int)(38*scale));
    Check(fixedRect.Right-fixedRect.Left==size.Width+(int)(16*scale) && fixedRect.Bottom-fixedRect.Top==size.Height+(int)(38*scale),"client versus frame sizing");
    Check(edge==1 || edge==4 || edge==7?fixedRect.Right==rect.Right:fixedRect.Left==rect.Left,"opposite horizontal anchor");
    Check(edge==3 || edge==4 || edge==5?fixedRect.Bottom==rect.Bottom:fixedRect.Top==rect.Top,"opposite vertical anchor");
   }
  }
  var normal=WindowSizingPolicy.Limits(1536,864,13,38,1);var min=WindowSizingPolicy.Constrain(1,1,0,0,2,normal);Check(min.Width==980 && min.Height==700,"normal minimum is client 980x700");
  var exact=WindowSizingPolicy.Limits(1280,760,16,38,1);var exactMin=WindowSizingPolicy.Constrain(1,1,0,0,2,exact);Check(exactMin.Width==980 && exactMin.Height==700,"minimum must not shrink when the actual work area fits it");
  var tiny=WindowSizingPolicy.Limits(680,380,13,38,1);var fit=WindowSizingPolicy.Constrain(2000,2000,0,0,2,tiny);Check(fit.Width+13<=680 && fit.Height+38<=380,"tiny work area overrides unattainable minimum without changing ratio");
  Console.WriteLine("PASS "+count+" pure aspect/minimum/edge/frame cases; no native windows or display APIs.");return 0;
 }catch(Exception ex){Console.WriteLine("FAIL "+ex);return 1;}}
}
