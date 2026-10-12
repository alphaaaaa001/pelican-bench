using System;
using System.Collections.Generic;
using System.Globalization;

namespace SvgCheck.Core.Parsing
{
    /// <summary>一个不带 alpha 的 RGB 三元组。</summary>
    public readonly struct Rgb
    {
        public readonly byte R, G, B;
        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
    }

    /// <summary>
    /// 颜色字面量解析。判据（亮度）与渲染（画刷）**共用这一份语法**，
    /// 否则会出现"渲染画得出来、判据说不清"或反过来的一致性问题。
    ///
    /// 解析不了就返回 null，含义是【无定论】而不是【不是那个颜色】—— 与工程笔记 2 同调。
    /// 命名色只收机器生成 SVG 里高频的一小撮；表外的名字交给渲染层的 ColorConverter 兜，
    /// 判据侧则按无定论处理（宁可漏判一只眼，也不把月亮认成眼）。
    /// </summary>
    public static class ColorParse
    {
        /// <summary>
        /// 命名色表。只收 SVG 1.0 / CSS 颜色关键字里**确实存在**的那些，且只取机器生成
        /// SVG 高频的一撮 —— 表外的名字交给渲染层的 ColorConverter 兜，判据侧按无定论处理。
        /// 宁缺毋滥：编一个不存在的名字进去，等于把垃圾输入认成合法颜色。
        /// </summary>
        private static readonly Dictionary<string, Rgb> Named = new Dictionary<string, Rgb>(StringComparer.OrdinalIgnoreCase)
        {
            { "black", new Rgb(0, 0, 0) },            { "white", new Rgb(255, 255, 255) },
            { "red", new Rgb(255, 0, 0) },            { "lime", new Rgb(0, 255, 0) },
            { "blue", new Rgb(0, 0, 255) },           { "yellow", new Rgb(255, 255, 0) },
            { "cyan", new Rgb(0, 255, 255) },         { "aqua", new Rgb(0, 255, 255) },
            { "magenta", new Rgb(255, 0, 255) },      { "fuchsia", new Rgb(255, 0, 255) },
            { "gray", new Rgb(128, 128, 128) },       { "grey", new Rgb(128, 128, 128) },
            { "silver", new Rgb(192, 192, 192) },     { "maroon", new Rgb(128, 0, 0) },
            { "navy", new Rgb(0, 0, 128) },           { "teal", new Rgb(0, 128, 128) },
            { "olive", new Rgb(128, 128, 0) },        { "purple", new Rgb(128, 0, 128) },
            { "green", new Rgb(0, 128, 0) },          { "orange", new Rgb(255, 165, 0) },
            { "brown", new Rgb(165, 42, 42) },        { "pink", new Rgb(255, 192, 203) },
            { "gold", new Rgb(255, 215, 0) },         { "ivory", new Rgb(255, 255, 240) },
            { "khaki", new Rgb(240, 230, 140) },      { "salmon", new Rgb(250, 128, 114) },
            { "tan", new Rgb(210, 180, 140) },        { "beige", new Rgb(245, 245, 220) },
            { "coral", new Rgb(255, 127, 80) },       { "wheat", new Rgb(245, 222, 179) },
            { "snow", new Rgb(255, 250, 250) },       { "crimson", new Rgb(220, 20, 60) },
            { "indigo", new Rgb(75, 0, 130) },        { "violet", new Rgb(238, 130, 238) },
            { "plum", new Rgb(221, 160, 221) },       { "orchid", new Rgb(218, 112, 214) },
            { "lavender", new Rgb(230, 230, 250) },   { "thistle", new Rgb(216, 191, 216) },
            { "linen", new Rgb(250, 240, 230) },      { "azure", new Rgb(240, 255, 255) },
            { "seashell", new Rgb(255, 245, 238) },   { "honeydew", new Rgb(240, 255, 240) },
            { "mintcream", new Rgb(245, 255, 250) },  { "oldlace", new Rgb(253, 245, 230) },
            { "floralwhite", new Rgb(255, 250, 240) },{ "cornsilk", new Rgb(255, 248, 220) },
            { "lemonchiffon", new Rgb(255, 250, 205) },{ "papayawhip", new Rgb(255, 239, 213) },
            { "blanchedalmond", new Rgb(255, 235, 205) }, { "bisque", new Rgb(255, 228, 196) },
            { "peachpuff", new Rgb(255, 218, 185) },  { "navajowhite", new Rgb(255, 222, 173) },
            { "moccasin", new Rgb(255, 228, 181) },   { "sandybrown", new Rgb(244, 164, 96) },
            { "burlywood", new Rgb(222, 184, 135) },  { "goldenrod", new Rgb(218, 165, 32) },
            { "darkgoldenrod", new Rgb(184, 134, 11) },{ "rosybrown", new Rgb(188, 143, 143) },
            { "saddlebrown", new Rgb(139, 69, 19) },  { "sienna", new Rgb(160, 82, 45) },
            { "chocolate", new Rgb(210, 105, 30) },   { "firebrick", new Rgb(178, 34, 34) },
            { "darkred", new Rgb(139, 0, 0) },        { "tomato", new Rgb(255, 99, 71) },
            { "orangered", new Rgb(255, 69, 0) },     { "hotpink", new Rgb(255, 105, 180) },
            { "deeppink", new Rgb(255, 20, 147) },    { "palevioletred", new Rgb(219, 112, 147) },
            { "mediumvioletred", new Rgb(199, 21, 133) }, { "rebeccapurple", new Rgb(102, 51, 153) },
            { "darkmagenta", new Rgb(139, 0, 139) },  { "darkorchid", new Rgb(153, 50, 204) },
            { "blueviolet", new Rgb(138, 43, 226) },  { "mediumpurple", new Rgb(147, 112, 219) },
            { "mediumorchid", new Rgb(186, 85, 211) },{ "darkviolet", new Rgb(148, 0, 211) },
            { "darkslateblue", new Rgb(72, 61, 139) },{ "slateblue", new Rgb(106, 90, 205) },
            { "mediumslateblue", new Rgb(123, 104, 238) }, { "midnightblue", new Rgb(25, 25, 112) },
            { "darkblue", new Rgb(0, 0, 139) },       { "mediumblue", new Rgb(0, 0, 205) },
            { "deepskyblue", new Rgb(0, 191, 255) },  { "dodgerblue", new Rgb(30, 144, 255) },
            { "cornflowerblue", new Rgb(100, 149, 237) }, { "steelblue", new Rgb(70, 130, 180) },
            { "royalblue", new Rgb(65, 105, 225) },   { "skyblue", new Rgb(135, 206, 235) },
            { "lightskyblue", new Rgb(135, 206, 250) },{ "powderblue", new Rgb(176, 224, 230) },
            { "lightsteelblue", new Rgb(176, 196, 222) }, { "aliceblue", new Rgb(240, 248, 255) },
            { "ghostwhite", new Rgb(248, 248, 255) }, { "gainsboro", new Rgb(220, 220, 220) },
            { "lightgray", new Rgb(211, 211, 211) },  { "lightgrey", new Rgb(211, 211, 211) },
            { "darkgray", new Rgb(169, 169, 169) },   { "darkgrey", new Rgb(169, 169, 169) },
            { "dimgray", new Rgb(105, 105, 105) },    { "dimgrey", new Rgb(105, 105, 105) },
            { "slategray", new Rgb(112, 128, 144) },  { "slategrey", new Rgb(112, 128, 144) },
            { "lightslategray", new Rgb(119, 136, 153) }, { "lightslategrey", new Rgb(119, 136, 153) },
            { "darkslategray", new Rgb(47, 79, 79) }, { "darkslategrey", new Rgb(47, 79, 79) },
            { "cadetblue", new Rgb(95, 158, 160) },   { "turquoise", new Rgb(64, 224, 208) },
            { "mediumturquoise", new Rgb(72, 209, 204) }, { "darkturquoise", new Rgb(0, 206, 209) },
            { "paleturquoise", new Rgb(175, 238, 238) }, { "aquamarine", new Rgb(127, 255, 212) },
            { "mediumaquamarine", new Rgb(102, 205, 170) }, { "lightseagreen", new Rgb(32, 178, 170) },
            { "darkcyan", new Rgb(0, 139, 139) },     { "forestgreen", new Rgb(34, 139, 34) },
            { "darkgreen", new Rgb(0, 100, 0) },      { "seagreen", new Rgb(46, 139, 87) },
            { "mediumseagreen", new Rgb(60, 179, 113) }, { "springgreen", new Rgb(0, 255, 127) },
            { "lawngreen", new Rgb(124, 252, 0) },    { "chartreuse", new Rgb(127, 255, 0) },
            { "yellowgreen", new Rgb(154, 205, 50) }, { "darkolivegreen", new Rgb(85, 107, 47) },
            { "olivedrab", new Rgb(107, 142, 35) },   { "greenyellow", new Rgb(173, 255, 47) },
            { "palegreen", new Rgb(152, 251, 152) },  { "lightgreen", new Rgb(144, 238, 144) },
            { "darkseagreen", new Rgb(143, 188, 143) }, { "mediumspringgreen", new Rgb(0, 250, 154) },
            { "lightyellow", new Rgb(255, 255, 224) },{ "lightgoldenrodyellow", new Rgb(250, 250, 210) },
            { "palegoldenrod", new Rgb(238, 232, 170) }, { "lavenderblush", new Rgb(255, 240, 245) },
            { "mistyrose", new Rgb(255, 228, 225) },  { "peru", new Rgb(205, 133, 63) },
            { "whitesmoke", new Rgb(245, 245, 245) },
        };

        public static Rgb? ToRgb(string value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return null;   // 渐变：无定论
            if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(value, "currentColor", StringComparison.OrdinalIgnoreCase)) return null;

            if (value[0] == '#') return FromHex(value.Substring(1));
            if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return FromRgbFn(value);
            if (Named.TryGetValue(value, out var n)) return n;
            return null;
        }

        private static Rgb? FromHex(string h)
        {
            if (h.Length == 3 || h.Length == 4)
            {
                if (!byte.TryParse(new string(new[] { h[0], h[0] }), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)) return null;
                if (!byte.TryParse(new string(new[] { h[1], h[1] }), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)) return null;
                if (!byte.TryParse(new string(new[] { h[2], h[2] }), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return null;
                return new Rgb(r, g, b);
            }
            if (h.Length == 6 || h.Length == 8)
            {
                if (!byte.TryParse(h.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)) return null;
                if (!byte.TryParse(h.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)) return null;
                if (!byte.TryParse(h.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return null;
                return new Rgb(r, g, b);
            }
            return null;
        }

        private static Rgb? FromRgbFn(string value)
        {
            int lp = value.IndexOf('(');
            if (lp < 0) return null;
            var body = value.Substring(lp + 1).TrimEnd(')');
            var parts = body.Split(new[] { ',', ' ', '/', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var comps = new List<int>();
            foreach (var p in parts)
            {
                if (comps.Count >= 3) break;
                if (p.EndsWith("%", StringComparison.Ordinal))
                {
                    if (double.TryParse(p.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        comps.Add((int)Math.Round(Math.Max(0, Math.Min(100, pct)) / 100.0 * 255));
                    continue;
                }
                if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv)) comps.Add(iv);
            }
            if (comps.Count < 3) return null;
            return new Rgb(Clamp(comps[0]), Clamp(comps[1]), Clamp(comps[2]));
        }

        private static byte Clamp(int v) => (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));
    }
}
