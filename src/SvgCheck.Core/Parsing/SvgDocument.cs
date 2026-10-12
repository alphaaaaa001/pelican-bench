using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SvgCheck.Core.Geometry;

namespace SvgCheck.Core.Parsing
{
    /// <summary>一个 SVG 元素：属性、祖先链、世界矩阵、几何（局部坐标 + 已附世界矩阵）。</summary>
    public sealed class SvgEl
    {
        public string Tag = "";
        public string Id = "";
        public string Class = "";
        public SvgEl Parent;
        public readonly Dictionary<string, string> Attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<SvgEl> Children = new List<SvgEl>();
        public readonly List<Subpath> Subpaths = new List<Subpath>();
        public Mat World = Mat.Identity;
        public bool InDefs;
        public int Order;

        /// <summary>解析后的有效表现属性：继承值 &lt; 表现属性 &lt; CSS 规则。</summary>
        public readonly Dictionary<string, string> Resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Box Bounds;
        public bool HasBounds;

        public string Get(string key, string fallback = null)
            => Resolved.TryGetValue(key, out var v) ? v : fallback;

        public double Num(string key, double fallback = 0)
        {
            var v = Get(key);
            return v != null && TryParseNum(v, out double d) ? d : fallback;
        }

        /// <summary>是否有可参与几何推断的形状。</summary>
        public bool HasGeometry => Subpaths.Count > 0 && Subpaths.Any(s => s.Segs.Count > 0);

        /// <summary>是否应当渲染（不在 defs 内、未被 display:none 隐藏）。</summary>
        public bool Renderable => !InDefs && HasGeometry &&
                                  !string.Equals(Get("display"), "none", StringComparison.OrdinalIgnoreCase) &&
                                  !string.Equals(Get("visibility"), "hidden", StringComparison.OrdinalIgnoreCase);

        public static bool TryParseNum(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            // 去掉单位：SVG 里 stroke-width="2px" 合法
            int end = s.Length;
            if (end > 2 && (s.EndsWith("px", StringComparison.OrdinalIgnoreCase) ||
                            s.EndsWith("pt", StringComparison.OrdinalIgnoreCase)))
                end -= 2;
            return double.TryParse(s.Substring(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }

    /// <summary>
    /// 一份被检文档。加载过程分三步：抽 svg 片段 → 解析成树 → 遍历求几何与世界包围盒。
    /// 任何一步失败都不会抛异常，而是置 Degraded 并给出原因 —— 工具不能因为输入脏就崩，
    /// 但也绝不假装自己解析成功了。
    /// </summary>
    public sealed class SvgDocument
    {
        public string SourcePath = "";
        public long Bytes;
        public string Raw = "";
        public string SvgFragment = "";
        public string AllCss = "";
        public bool Degraded;
        public string DegradedReason;
        public Box? ViewBox;
        public readonly List<string> Unsupported = new List<string>();
        public readonly List<SvgEl> Elements = new List<SvgEl>();   // 文档序，含容器
        public readonly List<string> Keyframes = new List<string>();
        public int AnimDecls;
        public int GradientCount;

        private static readonly string[] ShapeTags =
            { "path", "circle", "ellipse", "rect", "line", "polyline", "polygon" };

        private static readonly HashSet<string> Inheritable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity",
            "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray",
            "stroke-dashoffset", "opacity", "color", "visibility", "shape-rendering",
            "marker-start", "marker-mid", "marker-end", "clip-path", "mask",
            "font-family", "font-size", "font-weight", "font-style", "text-anchor",
            "letter-spacing", "word-spacing", "writing-mode", "direction"
        };

        private static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "fill", "#000000" }, { "stroke", "none" }, { "stroke-width", "1" },
            { "opacity", "1" }, { "fill-opacity", "1" }, { "stroke-opacity", "1" },
            { "stroke-linecap", "butt" }, { "stroke-linejoin", "miter" }, { "visibility", "visible" }
        };

        // ---------------------------------------------------------------- 入口

        public static SvgDocument LoadFile(string path)
        {
            string raw;
            try { raw = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception) { raw = File.ReadAllText(path, Encoding.Default); }
            var doc = LoadText(raw);
            doc.SourcePath = path;
            doc.Bytes = new FileInfo(path).Length;
            return doc;
        }

        public static SvgDocument LoadText(string raw)
        {
            var d = new SvgDocument { Raw = raw ?? "" };
            d.Bytes = Encoding.UTF8.GetByteCount(d.Raw);
            d.SvgFragment = ExtractSvg(d.Raw);
            d.AllCss = CollectStyles(d.Raw);
            d.Keyframes.AddRange(MiniCss.KeyframeNames(d.AllCss + "\n" + d.SvgFragment));
            d.AnimDecls = MiniCss.AnimationDeclCount(d.AllCss + "\n" + d.SvgFragment);

            if (d.SvgFragment.Length == 0)
            {
                d.Degraded = true;
                d.DegradedReason = "文件里找不到 <svg>…</svg> 块";
                return d;
            }
            d.BuildTree();
            d.ComputeGeometry();
            d.PreflightRefs();   // 放在 LoadText 里而不是让调用方记得调：漏了就等于静默降级
            return d;
        }

        /// <summary>抽第一个 &lt;svg&gt; 块，按嵌套深度配对闭合标签（不是懒惰匹配，嵌套 svg 不会被截断）。</summary>
        internal static string ExtractSvg(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var m = Regex.Match(raw, @"<svg[\s>]", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            int start = m.Index;
            int depth = 0, i = start;
            while (i < raw.Length)
            {
                var open = Regex.Match(raw.Substring(i), @"<svg[\s>]", RegexOptions.IgnoreCase);
                var close = Regex.Match(raw.Substring(i), @"</svg\s*>", RegexOptions.IgnoreCase);
                if (!close.Success) return raw.Substring(start);        // 未闭合：吃到结尾
                if (open.Success && open.Index < close.Index)
                {
                    depth++;
                    i += open.Index + open.Length;
                    continue;
                }
                depth--;
                i += close.Index + close.Length;
                if (depth <= 0) return raw.Substring(start, i - start);
            }
            return raw.Substring(start);
        }

        /// <summary>收集**全文档**所有 &lt;style&gt; 文本，包括 svg 块之外的（工程笔记 3a）。</summary>
        internal static string CollectStyles(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder();
            foreach (Match m in Regex.Matches(raw, @"<style\b[^>]*>([\s\S]*?)</style\s*>", RegexOptions.IgnoreCase))
                sb.AppendLine(m.Groups[1].Value);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 建树

        private void BuildTree()
        {
            XDocument xd = null;
            try { xd = XDocument.Parse(Sanitize(SvgFragment), LoadOptions.None); }
            catch (Exception e1)
            {
                // 第二次尝试：把未定义的 HTML 实体（&nbsp; 之类）也降级成字面量
                try { xd = XDocument.Parse(Sanitize(SvgFragment, aggressive: true), LoadOptions.None); }
                catch (Exception) { xd = null; }
                if (xd == null)
                {
                    Degraded = true;
                    DegradedReason = "XML 解析失败（" + e1.Message.Split('\n')[0] + "），已降级为宽松解析：" +
                                     "不认继承与 transform，几何结论置信度降低";
                    BuildFlatFallback();
                    return;
                }
            }
            var root = xd.Root;
            ViewBox = ParseViewBox(root.Attribute("viewBox")?.Value);
            Walk(null, root, Mat.Identity, inDefs: false);
        }

        private void Walk(SvgEl parent, XElement xe, Mat world, bool inDefs)
        {
            var el = new SvgEl
            {
                Tag = xe.Name.LocalName,
                Parent = parent,
                World = world,
                InDefs = inDefs,
                Order = Elements.Count
            };
            foreach (var a in xe.Attributes())
            {
                string k = a.Name.LocalName;
                if (string.Equals(k, "id", StringComparison.OrdinalIgnoreCase)) el.Id = a.Value;
                else if (string.Equals(k, "class", StringComparison.OrdinalIgnoreCase)) el.Class = a.Value;
                el.Attrs[k] = a.Value;
            }
            Elements.Add(el);
            // Children 必须同时维护：渐变取 stop、以及任何"看子元素"的判据都依赖它。
            // 早先只填了扁平的 Elements，导致 grad.Children 恒空 → 取不到 stop → 渐变全回落成中性灰。
            if (parent != null) parent.Children.Add(el);

            // transform 解析失败 ⇒ 按单位矩阵处理，但必须记进 Unsupported，不静默吞掉
            Mat own = Mat.Identity;
            if (el.Attrs.TryGetValue("transform", out string tstr) && !string.IsNullOrWhiteSpace(tstr)
                && !TryParseTransform(tstr, out own))
            {
                own = Mat.Identity;
                Unsupported.Add("无法解析的 transform: " + tstr);
            }
            var childWorld = world.Mul(own);

            bool nowDefs = inDefs || string.Equals(el.Tag, "defs", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(el.Tag, "use", StringComparison.OrdinalIgnoreCase))
                Unsupported.Add("<use> 未支持（引用元素不参与几何推断）");
            if (string.Equals(el.Tag, "svg", StringComparison.OrdinalIgnoreCase) && parent != null)
                Unsupported.Add("嵌套 <svg> 的 viewBox 缩放未支持");

            foreach (var c in xe.Elements()) Walk(el, c, childWorld, nowDefs);
        }

        /// <summary>降级模式：正则抓顶层形状标签，扁平、无继承、无 transform。</summary>
        private void BuildFlatFallback()
        {
            foreach (Match m in Regex.Matches(SvgFragment, @"<(path|circle|ellipse|rect|line|polyline|polygon)\b([^>]*?)/?>",
                         RegexOptions.IgnoreCase))
            {
                var el = new SvgEl { Tag = m.Groups[1].Value.ToLowerInvariant(), World = Mat.Identity, Order = Elements.Count };
                foreach (Match a in Regex.Matches(m.Groups[2].Value, @"([\w:-]+)\s*=\s*(""([^""]*)""|'([^']*)')"))
                {
                    string k = a.Groups[1].Value;
                    string v = a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                    if (string.Equals(k, "id", StringComparison.OrdinalIgnoreCase)) el.Id = v;
                    else if (string.Equals(k, "class", StringComparison.OrdinalIgnoreCase)) el.Class = v;
                    el.Attrs[k] = v;
                }
                Elements.Add(el);
            }
            ViewBox = ParseViewBox(Regex.Match(SvgFragment, @"viewBox\s*=\s*""([^""]*)""").Groups[1].Value);
        }

        /// <summary>XML 里裸 & 与未定义实体是最常见的两类脏输入。先只修裸 &，失败再激进处理全部实体。</summary>
        internal static string Sanitize(string xml, bool aggressive = false)
        {
            string s = Regex.Replace(xml, @"&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)", "&amp;");
            if (aggressive) s = Regex.Replace(s, @"&(?!amp;)([a-zA-Z][\w-]*);", "&amp;$1;");
            return s;
        }

        internal static Box? ParseViewBox(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var nums = Regex.Matches(v, @"-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?")
                            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            if (nums.Count != 4) return null;
            return new Box(nums[0], nums[1], nums[0] + nums[2], nums[1] + nums[3]);
        }

        /// <summary>解析 transform 属性。未知函数返回 false，调用方按单位矩阵处理并记录。</summary>
        internal static bool TryParseTransform(string s, out Mat m)
        {
            m = Mat.Identity;
            if (string.IsNullOrWhiteSpace(s)) return true;
            var cur = Mat.Identity;
            int i = 0;
            while (true)
            {
                while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
                if (i >= s.Length) break;
                int n0 = i;
                while (i < s.Length && char.IsLetter(s[i])) i++;
                string fn = s.Substring(n0, i - n0).ToLowerInvariant();
                while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '(')) i++;
                int p0 = i;
                int depth = 1;
                while (i < s.Length && depth > 0)
                {
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')') { depth--; if (depth == 0) break; }
                    i++;
                }
                string argTxt = s.Substring(p0, Math.Max(0, i - p0));
                if (i < s.Length) i++;    // 跳过 ')'
                var args = argTxt.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : (double?)null)
                                 .Where(t => t.HasValue).Select(t => t.Value).ToArray();

                Mat? step = fn switch
                {
                    "translate" => Mat.Translate(args.Length > 0 ? args[0] : 0, args.Length > 1 ? args[1] : 0),
                    "translatex" => Mat.Translate(args.Length > 0 ? args[0] : 0, 0),
                    "translatey" => Mat.Translate(0, args.Length > 0 ? args[0] : 0),
                    "scale" => Mat.Scale(args.Length > 0 ? args[0] : 1, args.Length > 1 ? args[1] : (args.Length > 0 ? args[0] : 1)),
                    "scalex" => Mat.Scale(args.Length > 0 ? args[0] : 1, 1),
                    "scaley" => Mat.Scale(1, args.Length > 0 ? args[0] : 1),
                    "rotate" => args.Length >= 3 ? Mat.RotateAt(args[0], args[1], args[2])
                                                 : Mat.Rotate(args.Length > 0 ? args[0] : 0),
                    "skewx" => Mat.SkewX(args.Length > 0 ? args[0] : 0),
                    "skewy" => Mat.SkewY(args.Length > 0 ? args[0] : 0),
                    "matrix" => args.Length == 6 ? new Mat(args[0], args[1], args[2], args[3], args[4], args[5]) : null,
                    _ => null
                };
                if (step == null) return false;
                cur = cur.Mul(step.Value);
            }
            m = cur;
            return true;
        }

        // ---------------------------------------------------------------- 几何

        private void ComputeGeometry()
        {
            var flat = MiniCss.Flatten(MiniCss.Parse(AllCss + "\n" + SvgFragment));

            foreach (var el in Elements)
            {
                // 表现属性：继承 < 自身属性 < CSS（按特异度与出现顺序）
                if (el.Parent != null)
                    foreach (var kv in el.Parent.Resolved)
                        if (Inheritable.Contains(kv.Key)) el.Resolved[kv.Key] = kv.Value;

                foreach (var kv in el.Attrs)
                    if (kv.Key != "transform" && kv.Key != "id" && kv.Key != "class")
                        el.Resolved[kv.Key] = kv.Value;

                var winner = new Dictionary<string, (int Spec, int Order)>();
                int ord = 0;
                foreach (var (sel, rule) in flat)
                {
                    ord++;
                    if (!MiniCss.Matches(sel, el.Tag, el.Id, el.Class)) continue;
                    foreach (var d in rule.Declarations)
                    {
                        if (winner.TryGetValue(d.Key, out var prev) &&
                            (prev.Spec > rule.Specificity || (prev.Spec == rule.Specificity && prev.Order > ord)))
                            continue;
                        winner[d.Key] = (rule.Specificity, ord);
                        el.Resolved[d.Key] = d.Value;
                    }
                }
                foreach (var kv in Defaults)
                    if (!el.Resolved.ContainsKey(kv.Key)) el.Resolved[kv.Key] = kv.Value;

                if (string.Equals(el.Tag, "linearGradient", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(el.Tag, "radialGradient", StringComparison.OrdinalIgnoreCase))
                    GradientCount++;

                if (Array.IndexOf(ShapeTags, el.Tag.ToLowerInvariant()) < 0) continue;
                BuildShape(el);

                // 世界包围盒：由各 Geom 的解析式 Bounds 求并（Geom 上已附 el.World）
                var bb = Box.Empty;
                foreach (var sp in el.Subpaths) bb = Box.Union(bb, sp.Bounds());
                el.HasBounds = bb.IsValid;
                el.Bounds = bb;
            }
        }

        private void BuildShape(SvgEl el)
        {
            List<Subpath> subs = null;
            switch (el.Tag.ToLowerInvariant())
            {
                case "path":
                    if (!el.Attrs.TryGetValue("d", out string d)) return;
                    var pr = PathParser.Parse(d);
                    if (!pr.Ok) Unsupported.Add("path d 解析失败：" + pr.Error);
                    subs = pr.Subpaths;
                    break;
                case "circle":
                {
                    double cx = el.Num("cx"), cy = el.Num("cy"), r = el.Num("r");
                    if (r <= 0) return;
                    subs = OneClosed(new GeoEllipse(cx, cy, r, r));
                    break;
                }
                case "ellipse":
                {
                    double rx = el.Num("rx"), ry = el.Num("ry");
                    if (rx <= 0 || ry <= 0) return;
                    subs = OneClosed(new GeoEllipse(el.Num("cx"), el.Num("cy"), rx, ry));
                    break;
                }
                case "rect":
                    subs = BuildRect(el);
                    break;
                case "line":
                {
                    var a = new Pt(el.Num("x1"), el.Num("y1"));
                    var b = new Pt(el.Num("x2"), el.Num("y2"));
                    subs = new List<Subpath> { MakeSub(a, new GeoLine(a, b), closed: false) };
                    break;
                }
                case "polyline":
                case "polygon":
                {
                    var pts = ParsePoints(el.Attrs.TryGetValue("points", out var p) ? p : "");
                    if (pts.Count < 2) return;
                    var sp = new Subpath { Start = pts[0], Last = pts[pts.Count - 1] };
                    for (int k = 0; k + 1 < pts.Count; k++) sp.Segs.Add(new GeoLine(pts[k], pts[k + 1]));
                    if (el.Tag.Equals("polygon", StringComparison.OrdinalIgnoreCase) && pts.Count > 2)
                    {
                        sp.Segs.Add(new GeoLine(pts[pts.Count - 1], pts[0]));
                        sp.Closed = true;
                    }
                    subs = new List<Subpath> { sp };
                    break;
                }
                default: return;
            }
            if (subs == null) return;
            foreach (var sp in subs)
                foreach (var g in sp.Segs) g.World = el.World;
            el.Subpaths.AddRange(subs);
        }

        private static List<Subpath> OneClosed(Geom g)
        {
            var sp = new Subpath { Closed = true };
            sp.Segs.Add(g);
            return new List<Subpath> { sp };
        }

        private static Subpath MakeSub(Pt start, Geom first, bool closed)
        {
            var sp = new Subpath { Start = start, Closed = closed };
            sp.Segs.Add(first);
            return sp;
        }

        private static List<Subpath> BuildRect(SvgEl el)
        {
            double x = el.Num("x"), y = el.Num("y"), w = el.Num("width"), h = el.Num("height");
            if (w <= 0 || h <= 0) return null;
            double rx = el.Num("rx", -1), ry = el.Num("ry", -1);
            if (rx < 0 && ry < 0)
            {
                var sp = new Subpath { Start = new Pt(x, y), Closed = true, Last = new Pt(x, y) };
                sp.Segs.Add(new GeoLine(new Pt(x, y), new Pt(x + w, y)));
                sp.Segs.Add(new GeoLine(new Pt(x + w, y), new Pt(x + w, y + h)));
                sp.Segs.Add(new GeoLine(new Pt(x + w, y + h), new Pt(x, y + h)));
                sp.Segs.Add(new GeoLine(new Pt(x, y + h), new Pt(x, y)));
                return new List<Subpath> { sp };
            }
            if (rx < 0) rx = ry;
            if (ry < 0) ry = rx;
            rx = Math.Min(rx, w / 2); ry = Math.Min(ry, h / 2);
            if (rx <= 0 || ry <= 0) return BuildPlain();

            var list = new List<Subpath>();
            var s = new Subpath { Start = new Pt(x + rx, y), Closed = true };
            s.Segs.Add(new GeoLine(new Pt(x + rx, y), new Pt(x + w - rx, y)));
            s.Segs.Add(new GeoArc(new Pt(x + w - rx, y), new Pt(x + w, y + ry), rx, ry, 0, false, true));
            s.Segs.Add(new GeoLine(new Pt(x + w, y + ry), new Pt(x + w, y + h - ry)));
            s.Segs.Add(new GeoArc(new Pt(x + w, y + h - ry), new Pt(x + w - rx, y + h), rx, ry, 0, false, true));
            s.Segs.Add(new GeoLine(new Pt(x + w - rx, y + h), new Pt(x + rx, y + h)));
            s.Segs.Add(new GeoArc(new Pt(x + rx, y + h), new Pt(x, y + h - ry), rx, ry, 0, false, true));
            s.Segs.Add(new GeoLine(new Pt(x, y + h - ry), new Pt(x, y + ry)));
            s.Segs.Add(new GeoArc(new Pt(x, y + ry), new Pt(x + rx, y), rx, ry, 0, false, true));
            s.Last = s.Start;
            list.Add(s);
            return list;

            List<Subpath> BuildPlain()
            {
                var sp = new Subpath { Start = new Pt(x, y), Closed = true, Last = new Pt(x, y) };
                sp.Segs.Add(new GeoLine(new Pt(x, y), new Pt(x + w, y)));
                sp.Segs.Add(new GeoLine(new Pt(x + w, y), new Pt(x + w, y + h)));
                sp.Segs.Add(new GeoLine(new Pt(x + w, y + h), new Pt(x, y + h)));
                sp.Segs.Add(new GeoLine(new Pt(x, y + h), new Pt(x, y)));
                return new List<Subpath> { sp };
            }
        }

        internal static List<Pt> ParsePoints(string s)
        {
            var pts = new List<Pt>();
            if (string.IsNullOrWhiteSpace(s)) return pts;
            var nums = Regex.Matches(s, @"-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?")
                            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            for (int i = 0; i + 1 < nums.Count; i += 2) pts.Add(new Pt(nums[i], nums[i + 1]));
            return pts;
        }

        // ---------------------------------------------------------------- 查询

        /// <summary>
        /// 所有可渲染元素的世界包围盒 = 「主体」范围。
        ///
        /// bgCoverFrac 用于剔除满画布背景：一块盖住 viewBox 两个方向 ≥ 此比例的实心元素是**天**，
        /// 不是主体。不剔的话 within_canvas 恒过（背景顶着画布边 ⇒ 留白 0 但也不为负），
        /// 真的被裁切的图也照样拿满分 —— 详见 rubric 的 _两条放水教训。
        /// </summary>
        public Box SubjectBounds(double bgCoverFrac)
        {
            var vb = ViewBox;
            var b = Box.Empty;
            foreach (var el in Elements)
            {
                if (!el.Renderable) continue;
                if (vb.HasValue && bgCoverFrac <= 1.0 && el.Bounds.IsValid)
                {
                    double covX = el.Bounds.Width / Math.Max(vb.Value.Width, 1e-6);
                    double covY = el.Bounds.Height / Math.Max(vb.Value.Height, 1e-6);
                    if (covX >= bgCoverFrac && covY >= bgCoverFrac) continue;
                }
                b = Box.Union(b, el.Bounds);
            }
            return b;
        }

        /// <summary>不剔背景的旧口径，仅供 --dump 这类观察用。判据一律走 SubjectBounds(bgCoverFrac)。</summary>
        public Box AllBounds()
        {
            var b = Box.Empty;
            foreach (var el in Elements)
                if (el.Renderable) b = Box.Union(b, el.Bounds);
            return b;
        }

        public int CountOf(string tag) => Elements.Count(e => string.Equals(e.Tag, tag, StringComparison.OrdinalIgnoreCase));

        /// <summary>按 id 找元素（含 defs 里的）。供渐变解析用。</summary>
        public SvgEl FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var e in Elements) if (e.Id == id) return e;
            return null;
        }

        /// <summary>
        /// 预检所有 url(#id) 引用，把断链记进 Unsupported。
        /// 必须在渲染**之前**跑：渲染时发现引用断了再报告，横幅已经画完了，等于没说。
        /// </summary>
        public void PreflightRefs()
        {
            foreach (var el in Elements)
            {
                if (!el.Renderable) continue;
                foreach (var prop in new[] { "fill", "stroke" })
                {
                    var v = el.Get(prop);
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    v = v.Trim();
                    if (!v.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) continue;
                    string id = v.Substring(4).TrimEnd(')').Trim().Trim('"', '\'');
                    if (id.StartsWith("#", StringComparison.Ordinal)) id = id.Substring(1);
                    var g = FindById(id);
                    if (g == null) { Unsupported.Add("引用了不存在的渐变 #" + id + "（" + el.Tag + " 的 " + prop + "）"); continue; }
                    string tag = g.Tag.ToLowerInvariant();
                    if (tag != "lineargradient" && tag != "radialgradient")
                        Unsupported.Add("#" + id + " 不是渐变而是 <" + tag + ">，未支持");
                    if (g.Children.Count(c => string.Equals(c.Tag, "stop", StringComparison.OrdinalIgnoreCase)) == 0)
                        Unsupported.Add("#" + id + " 没有 stop 子元素（可能靠 xlink:href 继承，本工具不追引用链）");
                }
            }
        }

        /// <summary>渲染与测量共用的元素序列（文档序，跳过 defs 与无几何者）。</summary>
        public IEnumerable<SvgEl> RenderList() => Elements.Where(e => e.Renderable);
    }
}
