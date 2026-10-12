using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SvgCheck.Core.Geometry;

namespace SvgCheck.Core.Parsing
{
    public sealed class PathParseResult
    {
        public readonly List<Subpath> Subpaths = new List<Subpath>();
        public bool Ok = true;
        public string Error;

        public void Fail(string msg) { Ok = false; Error = msg; }
    }

    /// <summary>
    /// SVG path 的 d 属性解析器，支持全套命令 M m L l H h V v C c S s Q q T t A a Z z。
    ///
    /// 为什么要有它：svgcheck.py 早期用一个正则
    ///     re.match(r'\s*M\s*(-?[\d.]+)[\s,]+(-?[\d.]+)\s*[Hh]\s*(-?[\d.]+)', d)
    /// 接受小写 h，却把后面的数当**绝对** x 解读。于是 22px 长的踏板
    ///     M336 322 h22
    /// 被解析成「从 x=336 到 x=22」——长度虚报成 314，还被当成可能的地面线，
    /// 最终误报成「地面线横穿轮子」。那不是坐标系的锅，是把增量读成了坐标。
    ///
    /// 本解析器维护 current point 与 subpath start，逐段把相对命令转成绝对坐标。
    /// </summary>
    public static class PathParser
    {
        public static PathParseResult Parse(string d)
        {
            var res = new PathParseResult();
            if (string.IsNullOrWhiteSpace(d)) return res;

            int i = 0;
            int n = d.Length;
            char cmd = '\0';
            char lastCmd = '\0';
            var cur = new Pt(0, 0);
            var start = new Pt(0, 0);
            var prevCtrl = new Pt(0, 0);
            Subpath sub = null;

            Subpath NewSub()
            {
                var s = new Subpath();
                res.Subpaths.Add(s);
                return s;
            }

            while (true)
            {
                SkipSep(d, ref i);
                if (i >= n) break;

                char c = d[i];
                if (IsCmd(c)) { cmd = c; i++; }
                else if (cmd == '\0') { res.Fail("d 以非法字符开头: " + c); return res; }
                else if (cmd == 'M' || cmd == 'm') cmd = 'L';          // M 后跟多组坐标 ⇒ 隐式 L
                else if (cmd == 'Z' || cmd == 'z') { res.Fail("z 之后还有多余参数"); return res; }
                // 其余命令：沿用上一个命令字母（SVG 允许省略重复命令字母）

                bool rel = char.IsLower(cmd);
                char up = char.ToUpperInvariant(cmd);

                switch (up)
                {
                    case 'M':
                    {
                        if (!TryNum(d, ref i, out double x) || !TryNum(d, ref i, out double y))
                        { res.Fail("M 参数不足"); return res; }
                        cur = Rel(x, y, cur, rel);
                        start = cur;
                        sub = NewSub();
                        sub.Start = cur; sub.Last = cur;
                        break;
                    }
                    case 'L':
                    {
                        if (!TryNum(d, ref i, out double x) || !TryNum(d, ref i, out double y))
                        { res.Fail("L 参数不足"); return res; }
                        var to = Rel(x, y, cur, rel);
                        Add(sub, cur, to); cur = to;
                        break;
                    }
                    case 'H':
                    {
                        if (!TryNum(d, ref i, out double x)) { res.Fail("H 参数不足"); return res; }
                        var to = new Pt(rel ? cur.X + x : x, cur.Y);
                        Add(sub, cur, to); cur = to;
                        break;
                    }
                    case 'V':
                    {
                        if (!TryNum(d, ref i, out double y)) { res.Fail("V 参数不足"); return res; }
                        var to = new Pt(cur.X, rel ? cur.Y + y : y);
                        Add(sub, cur, to); cur = to;
                        break;
                    }
                    case 'C':
                    {
                        if (!ReadNums(d, ref i, 6, out var v)) { res.Fail("C 参数不足"); return res; }
                        var c1 = Rel(v[0], v[1], cur, rel);
                        var c2 = Rel(v[2], v[3], cur, rel);
                        var to = Rel(v[4], v[5], cur, rel);
                        if (sub != null) sub.Segs.Add(new GeoCubic(cur, c1, c2, to));
                        prevCtrl = c2; cur = to;
                        break;
                    }
                    case 'S':
                    {
                        if (!ReadNums(d, ref i, 4, out var v)) { res.Fail("S 参数不足"); return res; }
                        var c1 = LastIs(lastCmd, 'C', 'S') ? Mirror(cur, prevCtrl) : cur;
                        var c2 = Rel(v[0], v[1], cur, rel);
                        var to = Rel(v[2], v[3], cur, rel);
                        if (sub != null) sub.Segs.Add(new GeoCubic(cur, c1, c2, to));
                        prevCtrl = c2; cur = to;
                        break;
                    }
                    case 'Q':
                    {
                        if (!ReadNums(d, ref i, 4, out var v)) { res.Fail("Q 参数不足"); return res; }
                        var c1 = Rel(v[0], v[1], cur, rel);
                        var to = Rel(v[2], v[3], cur, rel);
                        if (sub != null) sub.Segs.Add(new GeoQuad(cur, c1, to));
                        prevCtrl = c1; cur = to;
                        break;
                    }
                    case 'T':
                    {
                        if (!TryNum(d, ref i, out double x) || !TryNum(d, ref i, out double y))
                        { res.Fail("T 参数不足"); return res; }
                        var c1 = LastIs(lastCmd, 'Q', 'T') ? Mirror(cur, prevCtrl) : cur;
                        var to = Rel(x, y, cur, rel);
                        if (sub != null) sub.Segs.Add(new GeoQuad(cur, c1, to));
                        prevCtrl = c1; cur = to;
                        break;
                    }
                    case 'A':
                    {
                        // SVG 弧的参数是 7 个：rx ry rotation large-arc-flag sweep-flag x y
                        // 前 3 个是数，中间 2 个是标志位，最后 2 个是数。
                        // 早先这里读 4 个数字，把 large-arc 标志当数字吞了，导致后续全部错位。
                        if (!ReadNums(d, ref i, 3, out var v)) { res.Fail("A 参数不足"); return res; }
                        if (!ReadFlag(d, ref i, out bool large) || !ReadFlag(d, ref i, out bool sweep))
                        { res.Fail("A 标志位非法"); return res; }
                        if (!TryNum(d, ref i, out double ex) || !TryNum(d, ref i, out double ey))
                        { res.Fail("A 终点参数不足"); return res; }
                        var to = Rel(ex, ey, cur, rel);
                        double rx = v[0], ry = v[1];
                        // SVG 规范：半径为 0 或两端点重合 ⇒ 这一段按直线处理 / 直接跳过
                        if (rx == 0 || ry == 0) { Add(sub, cur, to); cur = to; break; }
                        if (Math.Abs(to.X - cur.X) < 1e-9 && Math.Abs(to.Y - cur.Y) < 1e-9) { cur = to; break; }
                        if (sub != null) sub.Segs.Add(new GeoArc(cur, to, rx, ry, v[2], large, sweep));
                        cur = to;
                        break;
                    }
                    case 'Z':
                    {
                        if (sub != null && (!Nearly(sub.Last, start)))
                        {
                            Add(sub, sub.Last, start);
                            sub.Closed = true;
                        }
                        else if (sub != null) sub.Closed = true;
                        cur = start;
                        if (sub != null) sub.Last = cur;
                        break;
                    }
                    default:
                        res.Fail("未支持的命令: " + up);
                        return res;
                }
                lastCmd = up;
            }

            if (res.Subpaths.Count == 0) res.Fail("d 里没有任何有效段");
            return res;
        }

        // ---------------------------------------------------------------- 辅助

        private static Pt Rel(double x, double y, Pt cur, bool rel)
            => rel ? new Pt(cur.X + x, cur.Y + y) : new Pt(x, y);

        private static Pt Mirror(Pt about, Pt ctrl)
            => new Pt(2 * about.X - ctrl.X, 2 * about.Y - ctrl.Y);

        private static bool Nearly(Pt a, Pt b)
            => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

        private static bool LastIs(char last, params char[] set)
        {
            foreach (var s in set) if (last == s) return true;
            return false;
        }

        private static void Add(Subpath sub, Pt a, Pt b)
        {
            if (sub == null) return;
            sub.Segs.Add(new GeoLine(a, b));
            sub.Last = b;
        }

        private static bool IsCmd(char c) => "MmLlHhVvCcSsQqTtAaZz".IndexOf(c) >= 0;

        private static void SkipSep(string s, ref int i)
        {
            while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == ',')) i++;
        }

        internal static bool TryNum(string s, ref int i, out double val)
        {
            val = 0;
            SkipSep(s, ref i);
            int start = i;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            bool digits = false;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits = true; }
            if (i < s.Length && s[i] == '.')
            {
                int dot = i;
                i++;
                bool frac = false;
                while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; frac = true; }
                if (!frac && !digits) i = dot + 1;              // 孤立的 '.' 不是数
                else digits |= frac;
            }
            if (!digits) { i = start; return false; }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))     // 指数：1e-3 之类
            {
                int save = i;
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                bool ed = false;
                while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; ed = true; }
                if (!ed) i = save;
            }
            return double.TryParse(s.Substring(start, i - start),
                                   NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        }

        private static bool ReadNums(string s, ref int i, int count, out double[] v)
        {
            v = new double[count];
            for (int k = 0; k < count; k++)
                if (!TryNum(s, ref i, out v[k])) return false;
            return true;
        }

        /// <summary>A 命令的两个标志位。SVG 允许它们紧贴且不加分隔（"0 01" / "001"），所以逐字符读。</summary>
        private static bool ReadFlag(string s, ref int i, out bool val)
        {
            val = false;
            SkipSep(s, ref i);
            if (i >= s.Length) return false;
            char c = s[i];
            if (c != '0' && c != '1') return false;
            i++;
            val = c == '1';
            return true;
        }

        /// <summary>把解析结果转成人类可读的规范化 d（调试与报告用）。</summary>
        public static string Normalize(List<Subpath> subs)
        {
            var sb = new StringBuilder();
            foreach (var sp in subs)
            {
                sb.Append("M ").Append(Fmt.N(sp.Start.X)).Append(' ').Append(Fmt.N(sp.Start.Y)).Append(' ');
                foreach (var g in sp.Segs)
                {
                    switch (g)
                    {
                        case GeoLine l:
                            sb.Append("L ").Append(Fmt.N(l.B.X)).Append(' ').Append(Fmt.N(l.B.Y)).Append(' ');
                            break;
                        case GeoQuad q:
                            sb.Append("Q ").Append(Fmt.N(q.P1.X)).Append(' ').Append(Fmt.N(q.P1.Y)).Append(' ')
                              .Append(Fmt.N(q.P2.X)).Append(' ').Append(Fmt.N(q.P2.Y)).Append(' ');
                            break;
                        case GeoCubic c:
                            sb.Append("C ").Append(Fmt.N(c.P1.X)).Append(' ').Append(Fmt.N(c.P1.Y)).Append(' ')
                              .Append(Fmt.N(c.P2.X)).Append(' ').Append(Fmt.N(c.P2.Y)).Append(' ')
                              .Append(Fmt.N(c.P3.X)).Append(' ').Append(Fmt.N(c.P3.Y)).Append(' ');
                            break;
                        case GeoArc a:
                            sb.Append("A(弧) ");
                            break;
                    }
                }
                if (sp.Closed) sb.Append("Z ");
            }
            return sb.ToString().Trim();
        }
    }
}
