using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace ScreenOffTray
{
    // Deliberately narrow: the installed MttVDD UTF-8 schema, one monitor.
    // Insert bytes only; never serialize an external document or replace options.
    internal static class VirtualModeConfig
    {
        internal const int Limit = 1024 * 1024;
        internal static void Dimensions(uint width, uint height)
        {
            if (width < 320 || height < 200 || width > 32768 || height > 32768)
                throw new InvalidOperationException("实体源尺寸不受支持 / Unsupported physical source dimensions.");
        }
        internal static byte[] Add(byte[] original, uint width, uint height)
        {
            Dimensions(width, height);
            if (original == null || original.Length == 0 || original.Length > Limit)
                throw new InvalidOperationException("虚拟屏配置为空或过大 / Empty or oversized virtual display configuration.");
            int bom = original.Length >= 3 && original[0] == 239 && original[1] == 187 && original[2] == 191 ? 3 : 0;
            string text = new UTF8Encoding(false, true).GetString(original, bom, original.Length - bom);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = Limit };
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            using (var reader = XmlReader.Create(new StringReader(text), settings)) doc.Load(reader);
            var declaration = doc.FirstChild as XmlDeclaration;
            if (declaration != null && !String.IsNullOrEmpty(declaration.Encoding) && !String.Equals(declaration.Encoding, "utf-8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("仅支持 UTF-8 配置 / Only UTF-8 configurations are supported.");
            if (doc.DocumentElement == null || doc.DocumentElement.Name != "vdd_settings" || doc.DocumentElement.NamespaceURI.Length != 0 ||
                doc.SelectNodes("/vdd_settings/resolutions").Count != 1 || doc.SelectNodes("/vdd_settings/monitors/count").Count != 1 ||
                doc.SelectSingleNode("/vdd_settings/monitors/count").InnerText.Trim() != "1")
                throw new InvalidOperationException("配置结构或监视器数量不受支持 / Unsupported configuration schema or monitor count.");
            var resolutions = doc.SelectSingleNode("/vdd_settings/resolutions");
            bool exists = false;
            foreach (XmlNode node in resolutions.ChildNodes) {
                if (node.NodeType != XmlNodeType.Element) continue;
                if (node.Name != "resolution" || node.NamespaceURI.Length != 0 || node.SelectNodes("width").Count != 1 || node.SelectNodes("height").Count != 1)
                    throw new InvalidOperationException("不支持的模式定义 / Unsupported resolution definition.");
                uint w, h;
                if (!UInt32.TryParse(node.SelectSingleNode("width").InnerText, out w) || !UInt32.TryParse(node.SelectSingleNode("height").InnerText, out h))
                    throw new InvalidOperationException("模式尺寸损坏 / Invalid resolution dimensions.");
                Dimensions(w, h);
                if (w == width && h == height) exists = true;
            }
            if (exists) return original;
            // Keep every global refresh rate. The driver may propagate all of them
            // to the new resolution; this is not a restriction to a single rate.
            XmlNode rate = doc.SelectSingleNode("/vdd_settings/global/g_refresh_rate") ?? doc.SelectSingleNode("/vdd_settings/resolutions/resolution/refresh_rate");
            decimal hz;
            if (rate == null || !Decimal.TryParse(rate.InnerText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out hz) || hz < 1 || hz > 1000)
                throw new InvalidOperationException("无法核实刷新率格式 / Cannot verify refresh-rate format.");
            string insertion = "<resolution><width>" + width.ToString(CultureInfo.InvariantCulture) + "</width><height>" +
                height.ToString(CultureInfo.InvariantCulture) + "</height><refresh_rate>" + hz.ToString(CultureInfo.InvariantCulture) + "</refresh_rate></resolution>";
            int position = -1;
            using (var reader = XmlReader.Create(new StringReader(text), settings)) {
                while (reader.Read()) if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "resolutions" && reader.Depth == 1) {
                    var line = (IXmlLineInfo)reader; int start = 0;
                    for (int n = 1; n < line.LineNumber; n++) { int end = text.IndexOf('\n', start); if (end < 0) throw new InvalidOperationException("XML line mapping failed."); start = end + 1; }
                    // EndElement's line position points to the name, after </.
                    position = start + line.LinePosition - 3;
                }
            }
            if (position < 0 || text.Substring(position, 2) != "</")
                throw new InvalidOperationException("不支持空缩写模式容器 / Unsupported empty resolution container.");
            byte[] addition = Encoding.UTF8.GetBytes(insertion);
            int offset = bom + Encoding.UTF8.GetByteCount(text.Substring(0, position));
            if (original.Length + addition.Length > Limit) throw new InvalidOperationException("Configuration limit exceeded.");
            byte[] result = new byte[original.Length + addition.Length];
            Buffer.BlockCopy(original, 0, result, 0, offset);
            Buffer.BlockCopy(addition, 0, result, offset, addition.Length);
            Buffer.BlockCopy(original, offset, result, offset + addition.Length, original.Length - offset);
            return result;
        }
        internal static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
