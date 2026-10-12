using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SvgCheck.Core.Geometry;
using SvgCheck.Core.Parsing;

namespace SvgCheck.Core.Rules
{
    /// <summary>一条规则的判决结果。</summary>
    public sealed class RuleOutcome
    {
        public string Id = "";
        public string Group = "";
        public string Name = "";
        public string Verdict = "unknown";      // pass | warn | fail | unknown
        public string Text = "";
        public double Weight;
        public double? Score;
        public readonly Dictionary<string, object> Measured = new Dictionary<string, object>();
        /// <summary>触发该结论的元素序号，供 UI 高亮。空 = 无可高亮对象。</summary>
        public readonly List<int> Highlight = new List<int>();
        /// <summary>true = rubric 声明了这条规则但代码没实现。必须大声暴露，不许伪装成 unknown。</summary>
        public bool Unimplemented;
    }

    /// <summary>一份体检单：逐条判决 + 加权总分 + 覆盖率。</summary>
    public sealed class Scorecard
    {
        public int? Score;                       // null = 一条都没判得动
        public double Coverage;
        public int Judged;
        public int UnknownCount;
        public bool LowCoverage;
        /// <summary>已判规则的权重和（= 分母）。unknown 不在其中。</summary>
        public double KnownWeight;
        /// <summary>已判规则的 Σ(weight·score)（= 分子）。</summary>
        public double WeightedSum;
        public bool Degraded;
        public string DegradedReason = "";
        public readonly List<RuleOutcome> Outcomes = new List<RuleOutcome>();

        public IEnumerable<RuleOutcome> InGroup(string g) => Outcomes.Where(o => o.Group == g);
        public RuleOutcome Find(string id) => Outcomes.FirstOrDefault(o => o.Id == id);
        public List<RuleOutcome> Unimplemented => Outcomes.Where(o => o.Unimplemented).ToList();

        public double GroupScore(string g)
        {
            var xs = InGroup(g).Where(o => o.Score.HasValue).ToList();
            return xs.Sum(o => o.Weight * o.Score.Value);
        }
        public double GroupWeight(string g) => InGroup(g).Sum(o => o.Weight);
    }

    /// <summary>
    /// 判据引擎。rubric.json 出【策略】（权重/容差/文案/依赖），这里出【实现】（怎么算）。
    /// 分派按 rule.Id；新增规则必须同时实现，否则进 Unimplemented 被测试拦下。
    /// </summary>
    public static class Analyzer
    {
        public static Scorecard Run(SvgDocument doc, Facts f, Rubric rub, Roles roles)
        {
            var ctx = new Ctx(doc, f, rub, roles);
            var sc = new Scorecard { Degraded = doc.Degraded, DegradedReason = doc.DegradedReason ?? "" };

            foreach (var rule in rub.Rules)
            {
                var o = new RuleOutcome
                {
                    Id = rule.Id, Group = rule.Group, Name = rule.Name, Weight = rule.Weight
                };

                var miss = roles.Missing(rule.Requires);
                Ev ev;
                if (miss.Count > 0)
                {
                    ev = Ev.Un();
                    ev.M["missing"] = string.Join("、", miss);
                }
                else ev = Dispatch(ctx, rule);

                if (ev.Unimpl)
                {
                    o.Unimplemented = true;
                    o.Verdict = "unknown";
                    o.Score = null;
                    o.Text = "【实现缺失】rubric 声明了 " + rule.Id + " 但本工具没有实现它 —— 这不是「图里没画」，是「尺子缺刻度」";
                }
                else if (ev.Unknown)
                {
                    o.Verdict = "unknown";
                    o.Score = null;
                    foreach (var kv in ev.M) o.Measured[kv.Key] = kv.Value;
                    if (!o.Measured.ContainsKey("missing")) o.Measured["missing"] = "（见实现）";
                    o.Text = Rubric.Render(rule.UnknownText, o.Measured);
                }
                else
                {
                    var crit = Rubric.Pick(rule, ev.Value);
                    o.Verdict = string.IsNullOrEmpty(crit.Verdict) ? "fail" : crit.Verdict;
                    o.Score = crit.Score;
                    foreach (var kv in ev.M) o.Measured[kv.Key] = kv.Value;
                    o.Text = Rubric.Render(crit.Text, o.Measured);
                    o.Highlight.AddRange(ev.Hi);
                }
                sc.Outcomes.Add(o);
            }

            // 加权 + 覆盖率：unknown 从分子与分母同时剔除（工程笔记 2 与 4）
            double wAll = rub.Rules.Sum(r => r.Weight);
            double wKnown = sc.Outcomes.Where(o => o.Score.HasValue).Sum(o => o.Weight);
            double num = sc.Outcomes.Where(o => o.Score.HasValue).Sum(o => o.Weight * o.Value());
            sc.Judged = sc.Outcomes.Count(o => o.Score.HasValue);
            sc.UnknownCount = sc.Outcomes.Count - sc.Judged;
            sc.KnownWeight = wKnown;
            sc.WeightedSum = num;
            sc.Coverage = wAll <= 0 ? 0 : Round4(wKnown / wAll);
            sc.Score = wKnown <= 0 ? (int?)null : (int)Math.Round(num / wKnown * 100);
            sc.LowCoverage = sc.Coverage < rub.LowCoverage;
            return sc;
        }

        private static double Round4(double v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);

        private static double Value(this RuleOutcome o) => o.Score ?? 0;

        private static Ev Dispatch(Ctx ctx, RubricRule rule)
        {
            switch (rule.Id)
            {
                case "wheel_ground_contact": return ctx.WheelGroundContact();
                case "ground_clear_of_wheels": return ctx.GroundClearOfWheels();
                case "wheels_separated": return ctx.WheelsSeparated();
                case "heading_consistent": return ctx.HeadingConsistent();
                case "feet_on_pedals": return ctx.FeetOnPedals();
                case "seat_on_seat_tube": return ctx.SeatOnSeatTube();
                case "spokes_present": return ctx.SpokesPresent();
                case "frame_triangles": return ctx.FrameTriangles();
                case "crank_pedals": return ctx.CrankPedals();
                case "pouch_present": return ctx.PouchPresent();
                case "feather_strokes": return ctx.FeatherStrokes();
                case "beak_length": return ctx.BeakLength();
                case "eye_present": return ctx.EyePresent();
                case "within_canvas": return ctx.WithinCanvas();
                case "gradient_depth": return ctx.GradientDepth();
                default: return Ev.NotBuilt();
            }
        }

        // ---------------------------------------------------------------- 求值载体

        internal sealed class Ev
        {
            public bool Unknown;
            public bool Unimpl;
            public double Value;
            public readonly Dictionary<string, object> M = new Dictionary<string, object>();
            public readonly List<int> Hi = new List<int>();

            public static Ev Un() => new Ev { Unknown = true };
            public static Ev NotBuilt() => new Ev { Unknown = true, Unimpl = true };
            public static Ev Of(double v, params int[] hi)
            {
                var e = new Ev { Value = v };
                e.Hi.AddRange(hi);
                return e;
            }
            internal Ev With(string k, object v) { M[k] = v; return this; }
        }

        // ---------------------------------------------------------------- 实现

        internal sealed class Ctx
        {
            private readonly SvgDocument _doc;
            private readonly Facts _f;
            private readonly Rubric _rub;
            private readonly Roles _roles;

            public Ctx(SvgDocument doc, Facts f, Rubric rub, Roles roles)
            { _doc = doc; _f = f; _rub = rub; _roles = roles; }

            private double Tol(string k) => _rub.Tol.TryGetValue(k, out var v) ? v : 0;
            private CircleFact[] WheelsArr()
            {
                var (a, b) = _roles.Wheels();
                return a == null || b == null ? null : new[] { a, b };
            }

            /// <summary>把 diff 的口语化描述塞进 measured：{sign} 与 {absdiff}。</summary>
            private static Ev SignOf(Ev e, double diff, string sunk = "沉入", string floatUp = "悬于")
            {
                e.M["sign"] = Math.Abs(diff) < 0.5 ? "贴合" : (diff > 0 ? floatUp : sunk);
                e.M["absdiff"] = Math.Abs(diff);
                return e;
            }

            internal Ev WheelGroundContact()
            {
                var ws = WheelsArr(); var g = _roles.Ground();
                double bottom = _f.WheelBottom(ws);
                double diff = g.Y - bottom;
                return SignOf(Ev.Of(diff, g.El.Order, ws[0].El.Order, ws[1].El.Order)
                    .With("wheel_bottom", bottom).With("ground", g.Y).With("diff", diff)
                    .With("ground_touch_px", Tol("ground_touch_px")), diff);
            }

            internal Ev GroundClearOfWheels()
            {
                var ws = WheelsArr();
                var hits = _f.LinesThroughWheels(ws);
                var e = Ev.Of(hits.Count, hits.Select(h => h.H.El.Order).Distinct().ToArray())
                    .With("count", hits.Count);
                if (hits.Count > 0)
                    e.With("detail", string.Join("；", hits.Select(h =>
                        $"线 y={h.H.Y.ToString("0", CultureInfo.InvariantCulture)} x∈[{h.H.X0:0},{h.H.X1:0}] × 轮 ({h.W.Cx:0},{h.W.Cy:0}) r={h.W.R:0}")));
                return e;
            }

            internal Ev WheelsSeparated()
            {
                var ws = WheelsArr();
                double dist = Math.Sqrt(Math.Pow(ws[1].Cx - ws[0].Cx, 2) + Math.Pow(ws[1].Cy - ws[0].Cy, 2));
                double sumr = ws[0].R + ws[1].R;
                double v = dist - sumr;
                return SignOf(Ev.Of(v, ws[0].El.Order, ws[1].El.Order)
                    .With("dist", dist).With("sumr", sumr).With("gap", v)
                    .With("r1", ws[0].R).With("r2", ws[1].R), v);
            }

            internal Ev HeadingConsistent()
            {
                var beak = _roles.Get("beak_tip");
                var eye = _roles.Get("eye");
                var ws = WheelsArr();
                double bird = beak.X - eye.X;
                double bike = ws[1].Cx - ws[0].Cx;
                double eps = Tol("heading_eps");
                if (Math.Abs(bird) < eps || Math.Abs(bike) < eps)
                {
                    var e0 = Ev.Un().With("missing", "喙尖与眼的 x 差过小（" + bird.ToString("0.##", CultureInfo.InvariantCulture) + "px），朝向无定义");
                    return e0;
                }
                bool same = Math.Sign(bird) == Math.Sign(bike);
                return Ev.Of(same ? 1 : 0, beak.El?.Order ?? -1, eye.El?.Order ?? -1, ws[1].El.Order)
                    .With("beak", beak.X).With("eye", eye.X).With("front", ws[1].Cx).With("rear", ws[0].Cx)
                    .With("bird", Dir(bird)).With("bike", Dir(bike));
            }

            private static string Dir(double dx) => dx > 0 ? "右" : "左";

            internal Ev FeetOnPedals()
            {
                var feet = _roles.AllOf("foot");
                var pedals = _roles.AllOf("pedal");
                if (feet.Count == 0 || pedals.Count == 0) return Ev.Un().With("missing", "脚或踏板");

                // 取最差的那只脚：min 会在「一只踩着、一只悬空」时误判合格
                double worst = 0;
                Pt worstFoot = default;
                RoleBinding worstPedal = null;
                foreach (var ft in feet)
                {
                    double best = double.PositiveInfinity; RoleBinding bp = null;
                    foreach (var pd in pedals)
                    {
                        double d = DistPointToEl(ft.Point, pd.El);
                        if (d < best) { best = d; bp = pd; }
                    }
                    if (best > worst) { worst = best; worstFoot = ft.Point; worstPedal = bp; }
                }
                return SignOf(Ev.Of(worst, worstPedal?.El.Order ?? -1)
                    .With("dist", worst).With("foot", $"({worstFoot.X:0},{worstFoot.Y:0})")
                    .With("feet", feet.Count).With("pedals", pedals.Count), worst);
            }

            internal Ev SeatOnSeatTube()
            {
                var seat = _roles.Get("seat");
                var tube = _roles.Get("seat_tube_top");
                double dx = seat.X - tube.X;
                return SignOf(Ev.Of(dx, seat.El?.Order ?? -1, tube.El?.Order ?? -1)
                    .With("seat", seat.X).With("tube", tube.X).With("dx", dx), dx);
            }

            internal Ev SpokesPresent()
            {
                var ws = WheelsArr();
                int n = _f.CountSpokes(ws[0]) + _f.CountSpokes(ws[1]);
                return Ev.Of(n, ws[0].El.Order, ws[1].El.Order)
                    .With("count", n).With("rear", _f.CountSpokes(ws[0])).With("front", _f.CountSpokes(ws[1]));
            }

            internal Ev FrameTriangles()
            {
                double merge = Tol("vertex_merge_px");
                var ws = WheelsArr();
                var (total, frame) = _f.CountFrameTriangles(ws == null ? null : (IReadOnlyList<CircleFact>)ws, merge);
                // 无轮可锚时退而用全图三角数，并在 measured 里如实标注是退化口径
                double v = ws == null ? total : frame;
                var e = Ev.Of(v).With("count", v).With("total", total).With("frame", frame)
                    .With("vertex_merge_px", merge);
                if (ws == null) e.With("口径", "无轮可锚，退化为全图三角数（含鸟零件三角，偏松）");
                return e;
            }

            /// <summary>
            /// 曲柄 + 踏板。判据（与 Python 侧同源，比例全部来自 rubric）：
            /// 找一个"五通"候选节点 —— 位于两轮之间、轴心高度附近，且从它引出
            /// **两条近似反向、等长、末端各带一段横向平台**的短段。
            /// </summary>
            internal Ev CrankPedals()
            {
                var ws = WheelsArr();
                double merge = Tol("vertex_merge_px");
                if (ws == null)
                {
                    int n0 = NamedCount(@"pedal|crank");
                    if (n0 > 0) return Ev.Of(n0).With("count", n0).With("geo", 0).With("named", n0)
                        .With("how", "class 名（轮子认不出，几何无从下手）");
                    return Ev.Un().With("missing", "轮子（定不出五通的位置）");
                }

                double rmean = (ws[0].R + ws[1].R) / 2.0;
                double lo = ws[0].Cx, hi = ws[1].Cx, span = hi - lo;
                double axy = (ws[0].Cy + ws[1].Cy) / 2.0;
                if (span <= 0) return Ev.Un().With("missing", "两轮中心距为 0");

                var g = _f.SegmentGraph(merge);
                double inset = Tol("crank_span_inset_frac") * span;
                double armLo = Tol("crank_arm_min_frac") * rmean;
                double armHi = Tol("crank_arm_max_frac") * rmean;
                double oppCos = Tol("crank_opposite_cos");
                double lenRatio = Tol("crank_len_ratio");

                int bestArms = 0;
                for (int vi = 0; vi < g.Verts.Count; vi++)
                {
                    var v = g.Verts[vi];
                    if (v.X < lo + inset || v.X > hi - inset) continue;
                    if (Math.Abs(v.Y - axy) > Tol("crank_axle_frac") * rmean) continue;
                    if (!g.Incident.TryGetValue(vi, out var inc) || inc.Count < 2) continue;

                    // 曲柄臂：一端挂在候选节点上、长度在 rmean 的合理区间内
                    var arms = new List<(double Len, Pt Tip, SegFact Seg)>();
                    foreach (var (_, seg) in inc)
                    {
                        if (seg.Len < armLo || seg.Len > armHi) continue;
                        bool aAtNode = Math.Abs(seg.A.X - v.X) <= merge && Math.Abs(seg.A.Y - v.Y) <= merge;
                        arms.Add((seg.Len, aAtNode ? seg.B : seg.A, seg));
                    }
                    if (arms.Count < 2) continue;

                    var qualified = arms.Where(a => HasPlatform(_f, a.Tip, a.Len, a.Seg, merge)).ToList();
                    int armsOk = 0;
                    for (int i = 0; i < qualified.Count; i++)
                        for (int j = i + 1; j < qualified.Count; j++)
                        {
                            var a = qualified[i]; var b = qualified[j];
                            double ua_x = (a.Tip.X - v.X) / a.Len, ua_y = (a.Tip.Y - v.Y) / a.Len;
                            double ub_x = (b.Tip.X - v.X) / b.Len, ub_y = (b.Tip.Y - v.Y) / b.Len;
                            if (ua_x * ub_x + ua_y * ub_y > oppCos) continue;                  // 曲柄是反向的一对
                            if (Math.Max(a.Len, b.Len) > lenRatio * Math.Min(a.Len, b.Len)) continue;  // 两边一般等长
                            armsOk = 2;
                        }
                    bestArms = Math.Max(bestArms, armsOk);
                }

                int named = NamedCount(@"pedal|crank");
                if (bestArms > 0)
                    return Ev.Of(bestArms).With("count", bestArms).With("geo", bestArms).With("named", named)
                        .With("how", "几何：五通处两条反向等长短段，末端各带一段短平台");
                if (named > 0)
                    return Ev.Of(named).With("count", named).With("geo", 0).With("named", named).With("how", "class 名");
                return Ev.Un().With("missing", "五通处的曲柄臂（几何与 class 名都拿不出正面证据）");
            }

            /// <summary>
            /// 踏板平台。**不要求平台与曲柄臂垂直** —— 踏板受重力影响总是接近水平，
            /// 而臂的方向随曲柄转角变，两者夹角从 0 到 180 都可能；拿垂直当判据会把真踏板全拒掉
            /// （实测：sample-pelican.html 的臂指向 (0.8,−0.6)、踏板 (1,0)，夹角 37°）。
            /// 也不要求共端点 —— 作者常常另起一条 h 短线。所以只在 tip 附近半径内找一段长度相当的短段。
            /// </summary>
            private bool HasPlatform(Facts f, Pt tip, double armLen, SegFact armSeg, double merge)
            {
                double rad = Math.Max(Tol("crank_platform_rad_merge_mult") * merge,
                                      Tol("crank_platform_rad_arm_mult") * armLen);
                double lo = Tol("crank_platform_min_frac") * armLen;
                double hi = Tol("crank_platform_max_frac") * armLen;
                foreach (var seg in f.Segments)
                {
                    if (ReferenceEquals(seg, armSeg)) continue;
                    if (seg.Len < lo || seg.Len > hi) continue;
                    double d = Math.Min(Dist2(seg.A, tip), Dist2(seg.B, tip));
                    if (d <= rad * rad) return true;
                }
                return false;
            }

            private int NamedCount(string pattern)
            {
                var rx = new System.Text.RegularExpressions.Regex(pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return _doc.Elements.Count(e => e.Renderable && rx.IsMatch((e.Class ?? "") + " " + (e.Id ?? "")));
            }

            /// <summary>
            /// 喉囊。四条并列条件才算正面证据（缺一即"无定论"）：
            ///  1. 实心（有效 fill ≠ none）—— 描边笔画不是囊
            ///  2. 包围盒 高/宽 ≥ @pouch_min_aspect —— 下垂，而不是平摊的一片
            ///  3. 挂在头部那一带 —— 一直垂到身子以下的，是脖子或腿，不是囊
            ///  4. 它头顶上有别的实心形体压着它 —— 那就是喙。囊不会长在天灵盖上
            /// </summary>
            internal Ev PouchPresent()
            {
                var bound = _roles.Get("pouch");
                if (bound != null) return Ev.Of(1, bound.El.Order).With("count", 1).With("how", "用户指派");

                int named = NamedCount(@"pouch|gular|throat|囊");
                if (named > 0) return Ev.Of(named).With("count", named).With("how", "class/id 名命中 pouch/gular/throat/囊");

                var subj = Subject();
                if (!subj.IsValid) return Ev.Un().With("missing", "主体包围盒（无从判断囊该挂在哪）");
                double fw = subj.Width, fh = subj.Height;
                if (fw <= 0 || fh <= 0) return Ev.Un().With("missing", "主体退化成一个点");

                var hulls = _f.FilledBounds();
                int hits = 0;
                for (int i = 0; i < hulls.Count; i++)
                {
                    var b = hulls[i];
                    double w = b.Width, h = b.Height;
                    if (w <= 0 || h / w < Tol("pouch_min_aspect")) continue;
                    if (w < Tol("pouch_min_width_frac") * fw) continue;
                    if ((b.MinY + b.MaxY) / 2 > subj.MinY + Tol("pouch_center_top_frac") * fh) continue;
                    if (b.MaxY > subj.MinY + Tol("pouch_bottom_max_frac") * fh) continue;

                    bool above = false;
                    for (int j = 0; j < hulls.Count && !above; j++)
                    {
                        if (j == i) continue;
                        var a = hulls[j];
                        if (!(a.MinY < b.MinY && a.MaxY <= b.MaxY)) continue;
                        double ov = Math.Min(b.MaxX, a.MaxX) - Math.Max(b.MinX, a.MinX);
                        if (ov >= Tol("pouch_above_overlap_frac") * Math.Min(w, a.Width)) above = true;
                    }
                    if (above) hits++;
                }
                if (hits > 0)
                    return Ev.Of(hits).With("count", hits).With("how", "几何：喙下方的下垂实心体");
                return Ev.Un().With("missing", "喉囊（几何四条条件无一定论，class 名也没搜到 —— 不能当作没画）");
            }

            internal Ev FeatherStrokes()
            {
                var named = _doc.Elements.Count(e =>
                    System.Text.RegularExpressions.Regex.IsMatch(e.Class + " " + e.Id, "feather",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                if (named > 0) return Ev.Of(named, _doc.Elements.Where(e =>
                    System.Text.RegularExpressions.Regex.IsMatch(e.Class + " " + e.Id, "feather",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Select(e => e.Order).ToArray())
                    .With("count", named).With("how", "class 名");

                // 几何法：短小的、只描边的**细**曲线笔画。
                // 细是必要的：脖子也是一条曲线（stroke-width 26），不限粗细就会被当成羽毛白送分。
                double maxStroke = _rub.Tol.TryGetValue("feather_max_stroke_px", out var ms) ? ms : 6;
                double diag = _doc.ViewBox.HasValue
                    ? Math.Sqrt(Math.Pow(_doc.ViewBox.Value.Width, 2) + Math.Pow(_doc.ViewBox.Value.Height, 2))
                    : 1000;
                var strokes = _doc.RenderList().Where(e =>
                {
                    if (!e.Bounds.IsValid) return false;
                    var fill = e.Get("fill");
                    if (fill != null && !string.Equals(fill, "none", StringComparison.OrdinalIgnoreCase)) return false;
                    if (e.Num("stroke-width", 1) > maxStroke) return false;
                    if (Math.Sqrt(Math.Pow(e.Bounds.Width, 2) + Math.Pow(e.Bounds.Height, 2)) > diag * 0.25) return false;
                    return e.Subpaths.Any(sp => sp.Segs.Any(g => g is GeoQuad || g is GeoCubic));
                }).ToList();

                if (strokes.Count == 0) return Ev.Un().With("missing", "羽毛笔画（hybrid：画风差异过大）");
                return Ev.Of(strokes.Count, strokes.Select(e => e.Order).ToArray())
                    .With("count", strokes.Count).With("how", "几何：短小、只描边、细描边的曲线笔画");
            }

            internal Ev BeakLength()
            {
                var beak = _roles.Get("beak_tip"); var eye = _roles.Get("eye");
                var head = HeadCircle(eye);
                if (head == null) return Ev.Un().With("missing", "头部（找不到与眼最近的圆，量不出头径）");
                double beakLen = Math.Sqrt(Math.Pow(beak.X - eye.X, 2) + Math.Pow(beak.Y - eye.Y, 2));
                double headDia = head.R * 2;
                double ratio = headDia <= 0 ? 0 : beakLen / headDia;
                return Ev.Of(ratio, beak.El?.Order ?? -1, head.El.Order)
                    .With("beak", beakLen).With("head", headDia).With("ratio", ratio);
            }

            internal Ev EyePresent()
            {
                var eye = _roles.Get("eye");
                if (eye != null) return Ev.Of(1, eye.El.Order).With("count", 1).With("how", "用户指派");

                var subj = Subject();
                if (!subj.IsValid) return Ev.Un().With("missing", "主体包围盒（无从限定眼的大小与位置）");
                double width = subj.Width;
                var ws = WheelsArr();
                double rmax = ws != null
                    ? Tol("eye_rmax_of_wheel") * ((ws[0].R + ws[1].R) / 2.0)
                    : Tol("eye_rmax_of_width") * width;
                double rmin = Tol("eye_rmin_of_width") * width;
                double top = subj.MinY + Tol("eye_top_frac") * subj.Height;
                double maxLum = Tol("eye_max_luminance");

                // 深色是必要的：只靠『小 + 在上半区』会把月亮环形山、花鼓帽数成眼睛（实测『3 只眼』）
                var cands = _f.Circles.Where(c =>
                    c.R >= rmin && c.R <= rmax && c.Cy <= top &&
                    Luminance(c.El.Get("fill")) is double lum && lum < maxLum).ToList();
                var named = _f.Circles.Where(c =>
                    System.Text.RegularExpressions.Regex.IsMatch(c.Class ?? "", @"eye|pupil|iris|瞳",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
                foreach (var c in named) if (!cands.Contains(c)) cands.Add(c);

                // 瞳孔 + 高光常画在同一处：圆心落在节点合并容差内的算**一只**眼，不然会数成两只
                double merge = Tol("vertex_merge_px");
                var clusters = new List<List<CircleFact>>();
                foreach (var c in cands.OrderByDescending(x => x.R))
                {
                    var host = clusters.FirstOrDefault(cl =>
                        Math.Sqrt(Math.Pow(cl[0].Cx - c.Cx, 2) + Math.Pow(cl[0].Cy - c.Cy, 2)) <= merge);
                    if (host != null) host.Add(c); else clusters.Add(new List<CircleFact> { c });
                }
                if (clusters.Count == 0)
                    return Ev.Un().With("missing", "眼（上部无 r∈[" + F(rmin) + ", " + F(rmax) +
                                                  "] 的圆；也可能是画风如此，不作没画）");
                return Ev.Of(clusters.Count, clusters.SelectMany(cl => cl.Select(c => c.El.Order)).ToArray())
                    .With("count", clusters.Count).With("rmin", rmin).With("rmax", rmax)
                    .With("how", named.Count > 0 ? "几何 + class 名" : "几何：上部小圆，瞳孔与高光并成一只");
            }

            private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

            internal Ev WithinCanvas()
            {
                var vb = _doc.ViewBox;
                if (vb == null) return Ev.Un().With("missing", "viewBox");
                var sb = Subject();
                if (!sb.IsValid) return Ev.Un().With("missing", "可测的主体");
                double margin = Math.Min(Math.Min(sb.MinX - vb.Value.MinX, sb.MinY - vb.Value.MinY),
                                         Math.Min(vb.Value.MaxX - sb.MaxX, vb.Value.MaxY - sb.MaxY));
                return SignOf(Ev.Of(margin).With("margin", margin), margin, "超出画布", "超出画布");
            }

            /// <summary>主体包围盒，已剔满画布背景（见 rubric 的 bg_cover_frac）。</summary>
            private Box Subject() => _doc.SubjectBounds(Tol("bg_cover_frac"));

            internal Ev GradientDepth()
            {
                int gradients = _doc.GradientCount;
                int layers = _f.DistinctFills();
                // 合成值：>100 表示真有渐变；≤100 时它就是纯色层数，让 warn 档可达（见 rubric 的 measure_注）
                double v = gradients > 0 ? 100 + gradients : layers;
                return Ev.Of(v).With("gradients", gradients).With("layers", layers);
            }

            // ---------------------------------------------------------------- 小工具

            private CircleFact HeadCircle(RoleBinding eye)
                => eye == null ? null : HeadCircleAt(new Pt(eye.X, eye.Y));

            private CircleFact HeadCircleAt(Pt p)
            {
                var ws = WheelsArr();
                return _f.Circles
                    .Where(c => ws == null || (c != ws[0] && c != ws[1]))
                    .Where(c => c.R > 2)
                    .OrderBy(c => Math.Sqrt(Math.Pow(c.Cx - p.X, 2) + Math.Pow(c.Cy - p.Y, 2)))
                    .FirstOrDefault();
            }

            private static double Dist(Pt p, CircleFact c) => Math.Sqrt(Dist2(p, new Pt(c.Cx, c.Cy)));
            private static double Dist2(Pt a, Pt b) => Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2);

            private static double DistPointToEl(Pt p, SvgEl el)
            {
                double best = double.PositiveInfinity;
                foreach (var sp in el.Subpaths)
                    foreach (var g in sp.Segs)
                        best = Math.Min(best, g.NearestDist(p));
                return best;
            }

            /// <summary>
            /// Rec.709 相对亮度：(0.2126R + 0.7152G + 0.0722B)/255，R/G/B 取 0..255。
            /// 公式由 rubric 的 _两条放水教训 钉死 —— 两侧必须同式，用 Rec.601 会给出不同 verdict。
            /// 解析不了（颜色名未知、渐变、none）返回 null，调用方按"无定论"处理，不当深色。
            /// </summary>
            private static double? Luminance(string color)
            {
                var c = Core.Parsing.ColorParse.ToRgb(color);
                if (c == null) return null;
                return (0.2126 * c.Value.R + 0.7152 * c.Value.G + 0.0722 * c.Value.B) / 255.0;
            }
        }
    }
}
