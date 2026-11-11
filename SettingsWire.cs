using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ScreenOffTray.SettingsTransport
{
    internal sealed class FormValues
    {
        public int Minutes {get;set;}
        public string Hotkey {get;set;}
        public bool HotkeyEdited {get;set;}
        public string Language {get;set;}
        public bool Startup {get;set;}
        public bool AutomaticUpdates {get;set;}
        public bool LimitEnabled {get;set;}
        public int LimitMinutes {get;set;}
        public string InterfaceTheme {get;set;}
    }
    internal sealed class PlayingSource
    {
        public string Path {get;set;}
        public string Name {get;set;}
    }
    internal sealed class ViewState
    {
        public long Version {get;set;}
        public string StatusZh {get;set;}
        public string StatusEn {get;set;}
        public bool Ready {get;set;}
        public bool Checking {get;set;}
        public bool Preparing {get;set;}
        public bool CanPrepare {get;set;}
        public bool CanClose {get;set;}
        public bool Saving {get;set;}
        public bool SavingWhitelist {get;set;}
        public int[] MigrationChoices {get;set;}
        public bool AudioKnown {get;set;}
        public string DetailZh {get;set;}
        public string DetailEn {get;set;}
        public string Diagnostics {get;set;}
        public string ProtectionZh {get;set;}
        public string ProtectionEn {get;set;}
        public string UpdateZh {get;set;}
        public string UpdateEn {get;set;}
        public bool UpdateAvailable {get;set;}
        public string[] Whitelist {get;set;}
        public PlayingSource[] Sources {get;set;}
        public FormValues Applied {get;set;}
    }
    internal sealed class Packet
    {
        public int Protocol {get;set;}
        public string Kind {get;set;}
        public long RequestId {get;set;}
        public FormValues Values {get;set;}
        public string[] Paths {get;set;}
        public ViewState State {get;set;}
        public string Error {get;set;}
        public string Code {get;set;}
        public bool Flag {get;set;}
        public int Key {get;set;}
        public uint Modifiers {get;set;}
        public bool Up {get;set;}
        public Packet() {Protocol=1;}
    }
    internal static class Frames
    {
        internal const int MaximumBytes=4*1024*1024;
        private static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
        private static JavaScriptSerializer Serializer(){return new JavaScriptSerializer{MaxJsonLength=MaximumBytes,RecursionLimit=20};}
        internal static void Write(Stream stream,Packet packet)
        {
            string json=Serializer().Serialize(packet);
            if(Utf8.GetByteCount(json)>MaximumBytes)throw new InvalidDataException("UI message is too large");
            byte[] payload=Utf8.GetBytes(json);
            int length=payload.Length;
            byte[] header={(byte)length,(byte)(length>>8),(byte)(length>>16),(byte)(length>>24)};
            stream.Write(header,0,4);stream.Write(payload,0,payload.Length);stream.Flush();
        }
        internal static Packet Read(Stream stream)
        {
            byte[] header=new byte[4];ReadExact(stream,header);
            int count=header[0]|header[1]<<8|header[2]<<16|header[3]<<24;
            if(count<=0 || count>MaximumBytes)throw new InvalidDataException("Invalid UI frame length");
            byte[] payload=new byte[count];ReadExact(stream,payload);
            var packet=Serializer().Deserialize<Packet>(Utf8.GetString(payload));
            if(packet==null || packet.Protocol!=1 || String.IsNullOrEmpty(packet.Kind))throw new InvalidDataException("Unsupported UI protocol");
            return packet;
        }
        private static void ReadExact(Stream stream,byte[] bytes)
        {int read=0;while(read<bytes.Length){int count=stream.Read(bytes,read,bytes.Length-read);if(count==0)throw new EndOfStreamException();read+=count;}}
    }
}
