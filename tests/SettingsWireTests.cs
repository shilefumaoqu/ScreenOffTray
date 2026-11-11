using System;
using System.IO;
using ScreenOffTray.SettingsTransport;
internal static class SettingsWireTests
{
    static int checks;
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
    sealed class Fragmented : MemoryStream {internal Fragmented(byte[] data):base(data){}public override int Read(byte[] buffer,int offset,int count){return base.Read(buffer,offset,Math.Min(count,3));}}
    static void Refuse(byte[] bytes,string name){try{Frames.Read(new Fragmented(bytes));throw new Exception("Accepted "+name);}catch(InvalidDataException){checks++;}catch(EndOfStreamException){checks++;}}
    static int Main()
    {
        try{
            byte[] bytes;
            using(var stream=new MemoryStream()){
                Frames.Write(stream,new Packet{Kind="save",RequestId=19,Values=new FormValues{Minutes=3,Hotkey="Ctrl+Alt+F10",Language="zh-CN",InterfaceTheme="black"},Paths=new[]{@"C:\应用\a.exe"}});bytes=stream.ToArray();
            }
            var value=Frames.Read(new Fragmented(bytes));Check(value.RequestId==19 && value.Values.Minutes==3 && value.Values.InterfaceTheme=="black" && value.Paths[0]==@"C:\应用\a.exe","fragmented UTF8 frame roundtrip preserves typed values and appearance");
            using(var stream=new MemoryStream()){
                Frames.Write(stream,new Packet{Kind="state",State=new ViewState{Version=7,AudioKnown=true,PlaybackLimitReached=true,
                    Sources=new[]{new PlayingSource{Path=@"C:\应用\player.exe",Name="Player",Whitelisted=true,Protected=true}}}});
                stream.Position=0;value=Frames.Read(stream);
                Check(value.State.PlaybackLimitReached && value.State.Sources[0].Whitelisted && value.State.Sources[0].Protected,"playback protection state survives the framed protocol");
            }
            Refuse(new byte[]{0,0,0,0},"zero length");Refuse(new byte[]{255,255,255,255},"negative length");Refuse(new byte[]{1,0,64,0},"oversized length");Refuse(new byte[]{3,0,0,0,65},"truncated payload");
            using(var stream=new MemoryStream()){Frames.Write(stream,new Packet{Protocol=2,Kind="hello"});Refuse(stream.ToArray(),"unsupported protocol");}
            using(var stream=new MemoryStream()){Frames.Write(stream,new Packet{Kind="a"});Frames.Write(stream,new Packet{Kind="b"});stream.Position=0;Check(Frames.Read(stream).Kind=="a" && Frames.Read(stream).Kind=="b","adjacent frames retain boundaries");}
            Console.WriteLine("Checks="+checks+"; memory streams only; no product process or IPC server");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
