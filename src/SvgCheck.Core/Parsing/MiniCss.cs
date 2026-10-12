using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SvgCheck.Core.Parsing
{
    /// <summary>一条 CSS 规则：选择器 + 声明。</summary>
    public sealed class CssRule
    {
        public string Selector;
        public int Specificity;
        public int Order;
        public readonly Dictionary<string, string> Declarations =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 极简 CSS 解析器。只支持 SVG 表现属性会用到的选择器形态：
    ///   .cls   #id   tag   tag.cls   逗号分隔的选择器列表
    /// 不支持后代/子代/伪类。够用即可 —— 机器生成的 SVG 基本只用类选择器。
    ///
    /// 为什么要它：动画与配色常写在 &lt;head&gt;&lt;style&gt; 里，而那在 &lt;svg&gt; 块之外。
    /// svgcheck.py 第一版只扫 svg 块，把「有动画」报成「无动画」（工程笔记 3a）。
    /// 渲染侧同理：漏掉 style，翅膀会掉色成黑块。所以 style 文本必须**全文档**收集后交给这里。
    /// </summary>
    public static class MiniCss
    {
        /// <summary>解析 CSS 文本，返回普通规则（@keyframes / @media 的内容被跳过，另由 KeyframeNames 提取）。</summary>
        public static List<CssRule> Parse(string css)
        {
            var rules = new List<CssRule>();
            if (string.IsNullOrEmpty(css)) return rules;

            int i = 0, order = 0;
            var sel = new StringBuilder();
            while (i < css.Length)
            {
                i = SkipComments(css, i);
                if (i >= css.Length) break;
                char c = css[i];
                if (c == '@')
                {
                    // at-rule：跳到匹配的 } ，并跳过其内部的一层嵌套花括号
                    i = SkipAtRule(css, i);
                    continue;
                }
                if (c == '{')
                {
                    string selector = sel.ToString().Trim();
                    sel.Clear();
                    i++;
                    int bodyStart = i;
                    i = MatchBrace(css, i);
                    string body = css.Substring(bodyStart, Math.Max(0, i - bodyStart));
                    i = Math.Min(i + 1, css.Length);
                    var rule = Build(selector, body);
                    if (rule != null) { rule.Order = order++; rules.Add(rule); }
                    continue;
                }
                sel.Append(c);
                i++;
            }
            return rules;
        }

        /// <summary>逗号列表展开成多条规则；此处只返回第一条以便调用方按选择器逐个建规则。</summary>
        private static CssRule Build(string selector, string body)
        {
            if (string.IsNullOrWhiteSpace(selector)) return null;
            var r = new CssRule { Selector = selector };
            foreach (var decl in body.Split(';'))
            {
                int colon = decl.IndexOf(':');
                if (colon <= 0) continue;
                string k = decl.Substring(0, colon).Trim();
                string v = decl.Substring(colon + 1).Trim();
                if (k.Length == 0 || v.Length == 0) continue;
                if (k.StartsWith("--", StringComparison.Ordinal)) continue;   // CSS 变量：不支持
                r.Declarations[k] = v;
            }
            r.Specificity = SpecificityOf(selector);
            return r.Declarations.Count > 0 ? r : null;
        }

        /// <summary>把规则按选择器拆成 (简单选择器, 规则) 对，供元素匹配。</summary>
        public static List<(string Sel, CssRule Rule)> Flatten(IEnumerable<CssRule> rules)
        {
            var outp = new List<(string, CssRule)>();
            foreach (var r in rules)
                foreach (var part in r.Selector.Split(','))
                {
                    var s = part.Trim();
                    if (s.Length > 0) outp.Add((s, r));
                }
            return outp;
        }

        /// <summary>简单选择器是否命中元素。tag 大小写不敏感（SVG 里 path/Path 都可能）。</summary>
        public static bool Matches(string sel, string tag, string id, string classList)
        {
            if (sel.Length == 0) return false;
            if (sel.IndexOfAny(new[] { ' ', '>', '+', '~', ':', '[', '*' }) >= 0) return false;

            string wantTag = null, wantId = null;
            var wantCls = new List<string>();
            int k = 0;
            var sb = new StringBuilder();
            // 形如 tag#id.cls1.cls2，任一部分可缺省
            while (k < sel.Length)
            {
                char c = sel[k];
                if (c == '#' || c == '.')
                {
                    if (sb.Length > 0) { wantTag = sb.ToString(); sb.Clear(); }
                    k++;
                    int s0 = k;
                    while (k < sel.Length && sel[k] != '#' && sel[k] != '.') k++;
                    string name = sel.Substring(s0, k - s0);
                    if (c == '#') wantId = name; else wantCls.Add(name);
                    continue;
                }
                sb.Append(c);
                k++;
            }
            if (sb.Length > 0 && wantTag == null) wantTag = sb.ToString();

            if (wantTag != null && !string.Equals(wantTag, tag, StringComparison.OrdinalIgnoreCase)) return false;
            if (wantId != null && wantId != id) return false;
            if (wantCls.Count > 0)
            {
                if (string.IsNullOrEmpty(classList)) return false;
                foreach (var w in wantCls)
                {
                    bool hit = false;
                    foreach (var tok in classList.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                        if (tok == w) { hit = true; break; }
                    if (!hit) return false;
                }
            }
            return wantTag != null || wantId != null || wantCls.Count > 0;
        }

        public static int SpecificityOf(string sel)
        {
            int ids = Regex.Matches(sel, @"#[\w-]").Count;
            int cls = Regex.Matches(sel, @"\.[\w-]").Count;
            string stripped = Regex.Replace(sel, @"#[\w-]|\.[\w-]", "");
            int tags = Regex.Matches(stripped.Trim(), @"^[a-zA-Z][\w-]*$").Count;
            return ids * 100 + cls * 10 + tags;
        }

        /// <summary>提取 @keyframes 名字（全文档，含 svg 块之外）。</summary>
        public static List<string> KeyframeNames(string css)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(css)) return list;
            foreach (Match m in Regex.Matches(css, @"@keyframes\s+([\w\-]+)"))
                if (!list.Contains(m.Groups[1].Value)) list.Add(m.Groups[1].Value);
            return list;
        }

        /// <summary>统计 animation 声明出现次数（简写 animation: 与 animation-name: 都算）。</summary>
        public static int AnimationDeclCount(string css)
        {
            if (string.IsNullOrEmpty(css)) return 0;
            return Regex.Matches(css, @"(?<![-\w])animation(-name)?\s*:").Count;
        }

        // ---------------------------------------------------------------- 词法

        private static int SkipComments(string s, int i)
        {
            while (true)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
                {
                    int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 2;
                    continue;
                }
                return i;
            }
        }

        private static int MatchBrace(string s, int i)
        {
            int depth = 1;
            while (i < s.Length && depth > 0)
            {
                char c = s[i];
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { i = SkipComments(s, i); continue; }
                if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return i; }
                i++;
            }
            return i;
        }

        private static int SkipAtRule(string s, int i)
        {
            // 找到 { 或直接到 ;（如 @charset）
            while (i < s.Length && s[i] != '{' && s[i] != ';') i++;
            if (i >= s.Length) return i;
            if (s[i] == ';') return i + 1;
            return Math.Min(MatchBrace(s, i + 1) + 1, s.Length);
        }
    }
}
