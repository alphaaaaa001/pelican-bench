using System;
using System.Collections.Generic;
using System.Linq;
using SvgCheck.Core.Geometry;
using SvgCheck.Core.Parsing;

namespace SvgCheck.Core.Rules
{
    /// <summary>一个圆（世界坐标）。只有 &lt;circle&gt; 参与轮子推定 —— 与 Python 侧保持一致，见下方注。</summary>
    public sealed class CircleFact
    {
        public SvgEl El;
        public double Cx, Cy, R;
        public Box Bounds;
        public string Class => (El?.Class ?? "") + " " + (El?.Id ?? "");
    }

    /// <summary>一条世界坐标下的水平线段。</summary>
    public sealed class HorizFact
    {
        public SvgEl El;
        public double Y, X0, X1;
        public string How;
        public double Len => X1 - X0;
        public Box AsBox => new Box(X0, Y, X1, Y);
    }

    /// <summary>一条世界坐标下的直线段（车架三角、辐条计数都靠它）。</summary>
    public sealed class SegFact
    {
        public SvgEl El;
        public Pt A, B;
        public double Len => Math.Sqrt((B.X - A.X) * (B.X - A.X) + (B.Y - A.Y) * (B.Y - A.Y));
        public bool Horizontal => Math.Abs(B.Y - A.Y) < 1e-6;
        public bool Vertical => Math.Abs(B.X - A.X) < 1e-6;
    }

    /// <summary>
    /// 从文档里抽出的、可供判据消费的几何事实。
    ///
    /// 与 Python 侧的口径约定（对拍的前提）：
    ///  - 轮子候选只认 &lt;circle&gt;，不认 rx≈ry 的椭圆 —— 两边同规则，否则初始分数就对不上
    ///  - 扁长 &lt;rect&gt; 当地面时，两条横边合并成一条、取垂直中心、标 "rect(扁长)"
    ///    （不合并的话一个矩形产出两条横线，与 Python 的单条中心永远差一项）
    ///  - 横线长度阈值 0.4×viewBox 宽，与 Python 同
    /// 差异是允许的：C# 认 transform，Python 不认。含 transform 的样本对拍时需按 rubric 降级再比。
    /// </summary>
    public sealed class Facts
    {
        public readonly SvgDocument Doc;
        public readonly List<CircleFact> Circles = new List<CircleFact>();
        public readonly List<HorizFact> Horizontals = new List<HorizFact>();
        public readonly List<SegFact> Segments = new List<SegFact>();

        public Box? ViewBox => Doc.ViewBox;
        public double? VbWidth => Doc.ViewBox?.Width;
        public double? VbHeight => Doc.ViewBox?.Height;

        public Facts(SvgDocument doc)
        {
            Doc = doc;
            Gather();
        }

        private void Gather()
        {
            foreach (var el in Doc.Elements)
            {
                if (!el.Renderable) continue;
                string tag = el.Tag.ToLowerInvariant();

                if (tag == "circle")
                {
                    double lr = el.Num("r");
                    if (lr <= 0) continue;
                    var c = el.World.Apply(new Pt(el.Num("cx"), el.Num("cy")));
                    // 仿射下圆变成椭圆；半径取解析式半轴，与 GeoEllipse.Bounds 同式
                    double hw = Math.Sqrt(GeoMath.Sq(el.World.A * lr) + GeoMath.Sq(el.World.C * lr));
                    double hh = Math.Sqrt(GeoMath.Sq(el.World.B * lr) + GeoMath.Sq(el.World.D * lr));
                    Circles.Add(new CircleFact
                    {
                        El = el, Cx = c.X, Cy = c.Y, R = (hw + hh) / 2, Bounds = el.Bounds
                    });
                }

                // 扁长矩形当地面：合并成一条中心横线，特判在通用线段收集之前
                if (tag == "rect")
                {
                    double w = el.Num("width"), h = el.Num("height");
                    if (w <= 0 || h <= 0) continue;
                    bool wide = VbWidth == null || w > 0.4 * VbWidth.Value;
                    bool flat = VbHeight == null || h < 0.08 * VbHeight.Value;
                    if (wide && flat)
                    {
                        var top = el.World.Apply(new Pt(el.Num("x"), el.Num("y")));
                        var bot = el.World.Apply(new Pt(el.Num("x") + w, el.Num("y") + h));
                        double y = (top.Y + bot.Y) / 2;
                        Horizontals.Add(new HorizFact
                        {
                            El = el, Y = y,
                            X0 = Math.Min(top.X, bot.X), X1 = Math.Max(top.X, bot.X),
                            How = "rect(扁长)"
                        });
                    }
                }

                foreach (var sp in el.Subpaths)
                    foreach (var g in sp.Segs)
                    {
                        if (g is GeoLine gl)
                        {
                            var a = el.World.Apply(gl.A);
                            var b = el.World.Apply(gl.B);
                            Segments.Add(new SegFact { El = el, A = a, B = b });
                        }
                    }
            }

            // 横线：从直线段里挑（<line>、path 的 H/V/L 都已是 GeoLine）。
            // rect 必须排除：它的四条边同样是 GeoLine，若不排，一块满画布天背景就会贡献 y=0 与 y=420
            // 两条"横线"，把推定地面错认成画布底边。rect 的横线只走上面的扁长特判一条路。
            foreach (var s in Segments)
            {
                if (!s.Horizontal) continue;
                if (string.Equals(s.El.Tag, "rect", StringComparison.OrdinalIgnoreCase)) continue;
                double len = Math.Abs(s.B.X - s.A.X);
                if (VbWidth != null && len <= 0.4 * VbWidth.Value) continue;
                string how = s.El.Tag.ToLowerInvariant() == "line" ? "line" : "path-H";
                Horizontals.Add(new HorizFact
                {
                    El = s.El, Y = s.A.Y,
                    X0 = Math.Min(s.A.X, s.B.X), X1 = Math.Max(s.A.X, s.B.X),
                    How = how
                });
            }
            Horizontals.Sort((a, b) => a.Y.CompareTo(b.Y));
        }

        /// <summary>半径最大的至多两个圆，按 x 升序。允许只有一个（单轮图也要能判穿轮）。</summary>
        public IReadOnlyList<CircleFact> TopWheels()
        {
            if (Circles.Count == 0) return Array.Empty<CircleFact>();
            return Circles.OrderByDescending(c => c.R).Take(2).OrderBy(c => c.Cx).ToList();
        }

        /// <summary>半径最大的两个圆，按 x 排定前后。与 Python 同规则。</summary>
        public (CircleFact rear, CircleFact front) GuessWheels()
        {
            var t = TopWheels();
            return t.Count < 2 ? (null, null) : (t[0], t[1]);
        }

        /// <summary>y 最大的横线 —— 推定地面。</summary>
        public HorizFact GuessGround() => Horizontals.Count == 0 ? null : Horizontals[Horizontals.Count - 1];

        /// <summary>
        /// 穿轮判定：必须同时满足 y 落入轮内 **且** x 区间与轮子重叠。
        /// 只比 y 是 svgcheck.py 的真实 bug：踏板 M336 322 h22（x∈[336,358]）
        /// 会撞上左轮（x∈[130,262]）—— 两者 x 区间根本不相交。
        /// </summary>
        public List<(HorizFact H, CircleFact W)> LinesThroughWheels(IReadOnlyList<CircleFact> wheels, double yFactor = 0.9)
        {
            var hits = new List<(HorizFact, CircleFact)>();
            foreach (var h in Horizontals)
                foreach (var w in wheels)
                {
                    if (h == null || w == null) continue;      // 允许传入未认定的槽位，不炸
                    if (Math.Abs(h.Y - w.Cy) >= w.R * yFactor) continue;
                    if (h.X1 < w.Cx - w.R || h.X0 > w.Cx + w.R) continue;   // x 区间不相交 ⇒ 不是它
                    hits.Add((h, w));
                }
            return hits;
        }

        /// <summary>轮子最低点（世界 y）。</summary>
        public double WheelBottom(IReadOnlyList<CircleFact> wheels)
            => wheels.Where(w => w != null).Max(w => w.Cy + w.R);

        /// <summary>
        /// 辐条计数（几何法）。两种真实画法都要认：
        ///  A. 直径式 —— 两端都在轮缘、且线段过轮心。这在视觉上就是**两根**辐条。
        ///     今天这只鹈鹕每轮画 4 条直径 = 8 根。只按"径向"判会把它们全跳掉（中点即轮心）。
        ///  B. 辐条式 —— 一端在毂内、一端在轮缘。
        /// 用几何而不是搜 class="spoke"：命名习惯不同就搜不到，那是诬告的来源。
        /// </summary>
        public int CountSpokes(CircleFact w)
        {
            int n = 0;
            foreach (var s in Segments)
            {
                double da = Dist(s.A, w), db = Dist(s.B, w);
                if (da > w.R * 1.08 || db > w.R * 1.08) continue;      // 有端点跑出轮外，不是辐条

                bool rimA = da >= w.R * 0.5, rimB = db >= w.R * 0.5;
                bool hubA = da <= w.R * 0.25, hubB = db <= w.R * 0.25;

                if (rimA && rimB)
                {
                    // 直径式：线段必须足够靠近轮心，否则是弦（装饰弧、轮圈分段）
                    double reach = Geom.DistToSegment(new Pt(w.Cx, w.Cy), s.A, s.B);
                    if (reach <= w.R * 0.15) n += 2;
                }
                else if ((hubA && rimB) || (hubB && rimA))
                {
                    n += 1;                                            // 辐条式
                }
            }
            return n;
        }

        private static double Dist(Pt p, CircleFact w)
            => Math.Sqrt((p.X - w.Cx) * (p.X - w.Cx) + (p.Y - w.Cy) * (p.Y - w.Cy));

        /// <summary>
        /// 找出所有三角形：把直线段端点在容差内合并成节点，数 3-环。
        /// 只合并**端点**，不求内部交点 —— 否则两条交叉辐条会和任意横杆凑出假三角。
        /// 返回每个三角形的三个世界坐标顶点，交由调用方判别是否属于车架（见 FrameTriangleCount）。
        /// </summary>
        public List<Pt[]> FindTriangles(double mergePx)
        {
            var nodes = new List<Pt>();
            int Idx(Pt p)
            {
                for (int k = 0; k < nodes.Count; k++)
                    if (Math.Abs(nodes[k].X - p.X) <= mergePx && Math.Abs(nodes[k].Y - p.Y) <= mergePx)
                        return k;
                nodes.Add(p);
                return nodes.Count - 1;
            }

            var edges = new HashSet<(int, int)>();
            foreach (var s in Segments)
            {
                if (s.Len < 1e-6) continue;
                int a = Idx(s.A), b = Idx(s.B);
                if (a == b) continue;
                edges.Add((Math.Min(a, b), Math.Max(a, b)));
            }

            var tris = new List<Pt[]>();
            var arr = edges.OrderBy(e => e.Item1).ThenBy(e => e.Item2).ToArray();
            for (int i = 0; i < arr.Length; i++)
                for (int j = i + 1; j < arr.Length; j++)
                {
                    if (arr[i].Item1 != arr[j].Item1) continue;
                    int p = arr[i].Item1, q = arr[i].Item2, r = arr[j].Item2;
                    if (q < r && edges.Contains((q, r)))
                        tris.Add(new[] { nodes[p], nodes[q], nodes[r] });
                }
            return tris;
        }

        /// <summary>
        /// 车架三角形数：只算**至少有一个顶点落在某个轮毂附近**的三角，及其邻接三角。
        ///
        /// 为什么要收紧：直接数全图三角会把尾羽三角、喙三角也算进来 —— 今天这只鹈鹕全图有 4 个三角，
        /// 但真车架只有 2 个（主三角 + 后三角）。一张车架为零、只有鸟零件三角的图不该被判合格。
        /// 不变量：真车架的后三角必有顶点在后轮毂上（后上叉与后下叉都汇于五通/轮毂）。
        /// </summary>
        public (int total, int frame) CountFrameTriangles(IReadOnlyList<CircleFact> wheels, double mergePx)
        {
            var tris = FindTriangles(mergePx);
            bool NearHub(Pt v) => wheels != null && wheels.Any(w =>
                Math.Sqrt((v.X - w.Cx) * (v.X - w.Cx) + (v.Y - w.Cy) * (v.Y - w.Cy)) <= mergePx * 2);

            var frame = new List<Pt[]>();
            foreach (var t in tris) if (t.Any(NearHub)) frame.Add(t);

            // 迭代扩散：与已认定车架三角共用 ≥2 顶点的也纳入。
            // 主三角（上管/下管/座管）本身不碰轮毂，但它与后三角共用座管两端，靠这一轮捞回来。
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var t in tris)
                {
                    if (frame.Contains(t)) continue;
                    if (!frame.Any(a => SharedVertices(a, t) >= 2)) continue;
                    frame.Add(t);
                    grew = true;
                }
            }
            return (tris.Count, frame.Count);
        }

        private static int SharedVertices(Pt[] a, Pt[] b)
        {
            int n = 0;
            foreach (var p in a) foreach (var q in b)
                if (Math.Abs(p.X - q.X) < 1e-6 && Math.Abs(p.Y - q.Y) < 1e-6) { n++; break; }
            return n;
        }

        /// <summary>不同填充色个数（供 gradient_depth 的分层兜底）。</summary>
        public int DistinctFills()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in Doc.Elements)
            {
                if (!el.Renderable) continue;
                var f = el.Get("fill");
                if (!string.IsNullOrEmpty(f) && f != "none") set.Add(f.Trim());
            }
            return set.Count;
        }

        /// <summary>
        /// 线段图：端点在 mergePx 容差内视为同一节点。
        /// 只合并**端点**，不求线段内部交点 —— 否则两条交叉辐条会和任意横杆凑出假三角、假五通。
        /// </summary>
        public sealed class SegGraph
        {
            public readonly List<Pt> Verts = new List<Pt>();
            public readonly HashSet<(int, int)> Edges = new HashSet<(int, int)>();
            /// <summary>vi → 该节点上的 (对端节点, 线段)。一个节点可能挂多条。</summary>
            public readonly Dictionary<int, List<(int Other, SegFact Seg)>> Incident =
                new Dictionary<int, List<(int, SegFact)>>();
        }

        public SegGraph SegmentGraph(double mergePx)
        {
            var g = new SegGraph();
            int Idx(Pt p)
            {
                for (int k = 0; k < g.Verts.Count; k++)
                    if (Math.Abs(g.Verts[k].X - p.X) <= mergePx && Math.Abs(g.Verts[k].Y - p.Y) <= mergePx)
                        return k;
                g.Verts.Add(p);
                return g.Verts.Count - 1;
            }
            foreach (var s in Segments)
            {
                if (s.Len < 1e-6) continue;
                int a = Idx(s.A), b = Idx(s.B);
                if (a == b) continue;
                var key = (Math.Min(a, b), Math.Max(a, b));
                if (!g.Edges.Add(key)) continue;
                if (!g.Incident.TryGetValue(a, out var la)) g.Incident[a] = la = new List<(int, SegFact)>();
                la.Add((b, s));
                if (!g.Incident.TryGetValue(b, out var lb)) g.Incident[b] = lb = new List<(int, SegFact)>();
                lb.Add((a, s));
            }
            return g;
        }

        /// <summary>实心（有效 fill ≠ none）元素的世界包围盒，供喉囊这类"下垂实心体"取证。</summary>
        public List<Box> FilledBounds()
        {
            var outp = new List<Box>();
            foreach (var el in Doc.Elements)
            {
                if (!el.Renderable || !el.Bounds.IsValid) continue;
                var fill = el.Get("fill");
                if (string.IsNullOrEmpty(fill) || string.Equals(fill, "none", StringComparison.OrdinalIgnoreCase))
                    continue;
                outp.Add(el.Bounds);
            }
            return outp;
        }
    }
}
