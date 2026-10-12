using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SvgCheck.Core.Geometry;
using SvgCheck.Core.Parsing;
using SvgCheck.Core.Report;
using SvgCheck.Core.Rules;

namespace SvgCheck.Tests
{
    /// <summary>
    /// 自检。前 9 条从 svgcheck.py 移植过来（它们各自对应一次真实踩坑），
    /// 后面是 C# 侧新增的回归 —— 尤其「相对坐标被当绝对」「穿轮只比 y 不比 x」
    /// 「满画布背景 rect 冒充地面线」这三条，是 2026-10-11 当天实测抓到的。
    /// </summary>
    internal static class SelfCheck
    {
        private static int _pass, _fail;
        private static readonly List<string> Failures = new List<string>();

        public static int Run(bool verbose = true)
        {
            _pass = 0; _fail = 0; Failures.Clear();

            // ============================================================ 移植自 Python 的 9 条
            Section("移植自 svgcheck.py 的既有自检");

            var (rear, front) = WheelsOf("<svg viewBox=\"0 0 100 100\"><circle cx=\"20\" cy=\"70\" r=\"20\"/>" +
                                        "<circle cx=\"80\" cy=\"70\" r=\"20\"/><path d=\"M0 90H100\"/></svg>");
            Ok(Sep(rear, front), "两轮分离 + 轮底贴地 → 两轮分离");

            var (r2, f2) = WheelsOf("<svg viewBox=\"0 0 100 100\"><circle cx=\"40\" cy=\"70\" r=\"20\"/>" +
                                   "<circle cx=\"60\" cy=\"70\" r=\"20\"/></svg>");
            Ok(!Sep(r2, f2), "轮子相交 → 判为不分离");

            var f3 = Fac("<svg viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"40\"/>" +
                         "<path d=\"M0 50H100\"/></svg>");
            // 只有一个圆：Python 侧用 self.wheels（至多两个），所以单轮也要能判穿轮
            Ok(f3.LinesThroughWheels(f3.TopWheels()).Count > 0, "地面线横穿轮心 → 命中",
                "轮 " + f3.TopWheels().Count + " 个，横线 " + f3.Horizontals.Count + " 条");

            var d4 = Doc("<svg viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></svg>",
                "<style>@keyframes spin{to{transform:rotate(360deg)}}.a{animation:spin 1s}</style>");
            Ok(d4.Keyframes.SequenceEqual(new[] { "spin" }) && d4.AnimDecls == 1,
                "<svg> 块外的 @keyframes 也要读到（笔记 3a）",
                "keyframes=[" + string.Join(",", d4.Keyframes) + "] decls=" + d4.AnimDecls);

            var f5 = Fac("<svg viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"40\" r=\"30\"/>" +
                         "<rect x=\"0\" y=\"70\" width=\"100\" height=\"6\"/></svg>");
            Ok(f5.Horizontals.Count == 1 && f5.Horizontals[0].How == "rect(扁长)",
                "扁长矩形当地面也要认出来（笔记 3b）",
                "found=" + string.Join(",", f5.Horizontals.Select(h => h.How)));

            GroundDiff("<svg viewBox=\"0 0 100 100\"><circle cx=\"20\" cy=\"70\" r=\"20\"/>" +
                       "<circle cx=\"80\" cy=\"70\" r=\"20\"/><path d=\"M0 90H100\"/></svg>", 0, "贴地");
            GroundDiff("<svg viewBox=\"0 0 100 100\"><circle cx=\"20\" cy=\"60\" r=\"20\"/>" +
                       "<circle cx=\"80\" cy=\"60\" r=\"20\"/><path d=\"M0 90H100\"/></svg>", 10, "悬空");
            GroundDiff("<svg viewBox=\"0 0 100 100\"><circle cx=\"20\" cy=\"80\" r=\"20\"/>" +
                       "<circle cx=\"80\" cy=\"80\" r=\"20\"/><path d=\"M0 90H100\"/></svg>", -10, "沉入");

            try
            {
                var f9 = Fac("<svg viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></svg>");
                var (x, y) = f9.GuessWheels();
                _ = f9.LinesThroughWheels(new CircleFact[0]);
                _ = f9.GuessGround();
                Ok(true, "无圆 / 无 viewBox 时不崩");
            }
            catch (Exception e) { Ok(false, "无圆 / 无 viewBox 时不崩", e.GetType().Name + ": " + e.Message); }

            // ============================================================ C# 侧新增回归
            Section("新增回归：2026-10-11 实测抓到的三个 bug");

            var pedal = PathParser.Parse("M336 322 h22");
            var pseg = pedal.Subpaths.SelectMany(s => s.Segs).OfType<GeoLine>().FirstOrDefault();
            Ok(pseg != null && Math.Abs(pseg.B.X - 358) < 1e-9 && Math.Abs(pseg.B.Y - 322) < 1e-9,
                "相对坐标 h22 必须转成绝对 x=358（旧正则读成 22）",
                pseg == null ? "没解析出线段" : "终点=(" + pseg.B.X + "," + pseg.B.Y + ")");

            var fRel = Fac("<svg viewBox=\"0 0 640 420\"><circle cx=\"196\" cy=\"300\" r=\"66\"/>" +
                           "<circle cx=\"452\" cy=\"300\" r=\"66\"/>" +
                           "<path d=\"M0 366 H640\"/><path d=\"M130 300 H262\"/></svg>");
            var (rw, fw) = fRel.GuessWheels();
            var hits = fRel.LinesThroughWheels(new[] { rw, fw });
            Ok(hits.All(h => !(h.H.X1 < h.W.Cx - h.W.R || h.H.X0 > h.W.Cx + h.W.R)),
                "穿轮判定必须校验 x 区间重叠（旧实现只比 y）");

            // 踏板 y=322 落在左轮 y 范围内，但 x∈[336,358] 与左轮 x∈[130,262] 不相交
            var fTrap = Fac("<svg viewBox=\"0 0 640 420\"><circle cx=\"196\" cy=\"300\" r=\"66\"/>" +
                            "<circle cx=\"452\" cy=\"300\" r=\"66\"/>" +
                            "<path d=\"M0 366 H640\"/><path d=\"M336 322 h22\"/></svg>");
            var (tw, tf) = fTrap.GuessWheels();
            Ok(fTrap.LinesThroughWheels(new[] { tw, tf }).Count == 0,
                "踏板 M336 322 h22 不得被误报成穿轮地面线",
                "命中 " + fTrap.LinesThroughWheels(new[] { tw, tf }).Count + " 处");

            var fBg = Fac("<svg viewBox=\"0 0 640 420\"><rect width=\"640\" height=\"420\" fill=\"#dff0f2\"/>" +
                          "<circle cx=\"196\" cy=\"300\" r=\"66\"/><circle cx=\"452\" cy=\"300\" r=\"66\"/>" +
                          "<path d=\"M0 366 H640\"/></svg>");
            Ok(fBg.GuessGround() != null && Math.Abs(fBg.GuessGround().Y - 366) < 1e-6,
                "满画布背景 rect 不得冒充地面（曾贡献 y=0 与 y=420）",
                "推定地面 y=" + fBg.GuessGround()?.Y);

            Section("新增回归：几何内核");

            // 旋转 45° 的圆：包围盒半宽 = √((a·r)²+(c·r)²)，不是 r
            var rot = Doc("<svg viewBox=\"0 0 100 100\"><circle cx=\"50\" cy=\"50\" r=\"10\" " +
                          "transform=\"rotate(45 50 50)\"/></svg>");
            var rc = new Facts(rot).Circles.FirstOrDefault();
            Ok(rc != null && Math.Abs(rc.R - 10) < 1e-6,
                "rotate 不改变圆半径（仿射保形）", "r=" + rc?.R);

            var tr = Doc("<svg viewBox=\"0 0 100 100\"><g transform=\"translate(10 20)\">" +
                         "<circle cx=\"5\" cy=\"5\" r=\"2\"/></g></svg>");
            var tc = new Facts(tr).Circles.FirstOrDefault();
            Ok(tc != null && Math.Abs(tc.Cx - 15) < 1e-6 && Math.Abs(tc.Cy - 25) < 1e-6,
                "transform 累积：嵌套 g 的 translate 要生效", "center=(" + tc?.Cx + "," + tc?.Cy + ")");

            // 三次贝塞尔：控制点多边形会把 y 上界抬到 100，真实极值远小于此
            var cub = Doc("<svg viewBox=\"0 0 100 100\"><path d=\"M0 50 C0 100 100 100 100 50\" fill=\"none\" stroke=\"black\"/></svg>");
            var cel = cub.RenderList().FirstOrDefault();
            Ok(cel != null && cel.Bounds.MaxY < 90 && cel.Bounds.MaxY > 80,
                "贝塞尔包围盒用导数根而非控制点多边形（后者会虚报到 y=100）",
                "maxY=" + cel?.Bounds.MaxY);

            var arc = Doc("<svg viewBox=\"0 0 200 200\"><path d=\"M20 100 A80 80 0 0 1 180 100\" fill=\"none\" stroke=\"black\"/></svg>");
            var ael = arc.RenderList().FirstOrDefault();
            Ok(ael != null && Math.Abs(ael.Bounds.MaxY - 100) < 1.0 && Math.Abs(ael.Bounds.MinY - 20) < 1.0,
                "圆弧 A 命令的包围盒（上半圆：y∈[20,100]）",
                "y∈[" + ael?.Bounds.MinY + "," + ael?.Bounds.MaxY + "]");

            Section("新增回归：判据伦理（工程笔记 2 与 4）");

            // 喉囊明明画了、只是没写 class —— 第一版曾报「未检测到」，那是诬告
            var pouch = Score("<svg viewBox=\"0 0 640 420\"><circle cx=\"196\" cy=\"300\" r=\"66\"/>" +
                              "<circle cx=\"452\" cy=\"300\" r=\"66\"/><path d=\"M0 366 H640\"/>" +
                              "<path d=\"M376 82 L470 96 L376 104 Z\" fill=\"#f6c765\"/>" +
                              "<path d=\"M378 100 C404 138 452 128 468 98 Z\" fill=\"#f3b452\"/></svg>");
            var po = pouch.Find("pouch_present");
            Ok(po != null && (po.Verdict == "pass" || po.Verdict == "unknown"),
                "喉囊：拿不准就给 unknown，绝不给 fail（曾被诬告的一次）",
                po?.Verdict + " — " + po?.Text);

            var noPouch = Score("<svg viewBox=\"0 0 640 420\"><circle cx=\"196\" cy=\"300\" r=\"66\"/>" +
                                "<circle cx=\"452\" cy=\"300\" r=\"66\"/><path d=\"M0 366 H640\"/></svg>");
            var np = noPouch.Find("pouch_present");
            Ok(np != null && np.Verdict == "unknown" && np.Score == null,
                "hybrid 规则无正面证据 ⇒ score 为 null，不入分母", np?.Verdict);

            var empty = Score("<svg viewBox=\"0 0 10 10\"></svg>");
            Ok(empty.Coverage < 0.6 && empty.LowCoverage,
                "一条都判不动时覆盖率必须低，不得假装高分",
                "coverage=" + empty.Coverage + " score=" + (empty.Score?.ToString() ?? "null"));

            // 守住打分公式本身：总分 = 已判规则的 Σ(w·s) / 已判权重。
            // unknown 既不进分子也不进分母 —— 若它被当成 0 分计入，
            // 一张语义项全缺角色的好图会从 98 分被拉到 62 分，那是诬告的另一种形态。
            var part = Score("<svg viewBox=\"0 0 640 420\"><circle cx=\"196\" cy=\"300\" r=\"66\"/>" +
                             "<circle cx=\"452\" cy=\"300\" r=\"66\"/><path d=\"M0 366 H640\"/></svg>");
            Ok(part.UnknownCount > 0 && part.KnownWeight > 0 && part.KnownWeight < 100,
                "构造出「部分可判 + 部分 unknown」的场景",
                "judged=" + part.Judged + " unknown=" + part.UnknownCount + " wKnown=" + part.KnownWeight);
            int expect = (int)Math.Round(part.WeightedSum / part.KnownWeight * 100, MidpointRounding.AwayFromZero);
            Ok(part.Score == expect,
                "总分 = 已判加权和 / 已判权重（unknown 不进分子分母）",
                "score=" + part.Score + " 期望 " + expect);
            double ifUnknownWereZero = part.WeightedSum / 100 * 100;
            Ok(part.Score != (int)Math.Round(ifUnknownWereZero) || part.UnknownCount == 0,
                "总分不得等于「把 unknown 当 0 分」算出来的那个值",
                "当 0 分会得 " + (int)Math.Round(ifUnknownWereZero) + "，实际 " + part.Score);

            Section("新增回归：契约与实现不失配");

            var rub = Rubric.LoadOrThrow(null);
            Ok(rub.Rules.Count == 15 && Math.Abs(rub.Rules.Sum(r => r.Weight) - 100) < 1e-9,
                "rubric 加载：15 条规则、权重合计 100", rub.ToString());

            var full = Score("<svg viewBox=\"0 0 640 420\"><circle cx=\"196\" cy=\"300\" r=\"66\"/>" +
                             "<circle cx=\"452\" cy=\"300\" r=\"66\"/><path d=\"M0 366 H640\"/></svg>");
            Ok(full.Unimplemented.Count == 0,
                "rubric 里每条规则都有实现（新增规则忘了实现会被这条拦下）",
                string.Join(",", full.Unimplemented.Select(o => o.Id)));

            var bad = full.Outcomes.Where(o => Regex.IsMatch(o.Text ?? "", @"\{[a-zA-Z_]\w*(:[^}]*)?\}")).ToList();
            Ok(bad.Count == 0,
                "渲染后的文案里不许残留未解析占位符（缺 measured 键会以字面量暴露）",
                string.Join(" | ", bad.Select(o => o.Id + ": " + o.Text)));

            var dj = JsonReport.Build(Doc("<svg viewBox=\"0 0 10 10\"><circle cx=\"5\" cy=\"5\" r=\"2\"/></svg>",
                                          "<svg viewBox=\"0 0 10 10\"><circle cx=\"5\" cy=\"5\" r=\"2\"/></svg>"),
                                     rub, new Roles(), full, "SvgCheck.Tests");
            Ok(dj.Contains("\"rubric_version\"") && dj.Contains("\"summary\"") && dj.Contains("\"coverage\""),
                "契约 JSON 含 rubric_version / summary / coverage 键");

            Console.WriteLine();
            Console.WriteLine("  ⇒ 自检 " + _pass + " 通过 / " + _fail + " 失败");
            if (_fail > 0)
            {
                Console.WriteLine("  失败项：");
                foreach (var s in Failures) Console.WriteLine("    - " + s);
            }
            return _fail == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 设施

        private static Rubric _cachedRubric;
        private static Rubric Rub() => _cachedRubric ??= Rubric.LoadOrThrow(null);

        private static SvgDocument Doc(string svg, string raw = null)
            => SvgDocument.LoadText(raw == null ? svg : raw + svg);

        private static Facts Fac(string svg) => new Facts(Doc(svg));

        private static (CircleFact, CircleFact) WheelsOf(string svg) => new Facts(Doc(svg)).GuessWheels();

        private static bool Sep(CircleFact a, CircleFact b)
        {
            if (a == null || b == null) return false;
            double d = Math.Sqrt(Math.Pow(a.Cx - b.Cx, 2) + Math.Pow(a.Cy - b.Cy, 2));
            return d > a.R + b.R;
        }

        private static void GroundDiff(string svg, double want, string name)
        {
            var f = Fac(svg);
            var (a, b) = f.GuessWheels();
            var g = f.GuessGround();
            double got = g == null ? double.NaN : Math.Round(g.Y - f.WheelBottom(new[] { a, b }));
            Ok(Math.Abs(got - want) < 1e-9, "贴地判定：" + name, "差 " + got + "px（期望 " + want + "）");
        }

        /// <summary>跑一遍判据：自动推定角色 → Scorecard。</summary>
        public static Scorecard Score(string svg, Action<Roles> tweak = null)
        {
            var doc = Doc(svg);
            var f = new Facts(doc);
            var roles = new Roles();
            roles.AutoInfer(f);
            tweak?.Invoke(roles);
            return Analyzer.Run(doc, f, Rub(), roles);
        }

        private static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("  ── " + title);
        }

        private static void Ok(bool cond, string name, string detail = "")
        {
            if (cond) { _pass++; Console.WriteLine("    ✓ " + name); }
            else
            {
                _fail++;
                Console.WriteLine("    ✗ " + name + (detail.Length > 0 ? "   [" + detail + "]" : ""));
                Failures.Add(name + (detail.Length > 0 ? " [" + detail + "]" : ""));
            }
        }
    }
}
