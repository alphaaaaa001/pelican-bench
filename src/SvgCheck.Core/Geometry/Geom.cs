using System;
using System.Collections.Generic;
using System.Globalization;

namespace SvgCheck.Core.Geometry
{
    /// <summary>纯几何值类型。Core 刻意不引用 System.Windows —— 判据要能被控制台跑器驱动。</summary>
    public readonly struct Pt
    {
        public readonly double X;
        public readonly double Y;
        public Pt(double x, double y) { X = x; Y = y; }
        public override string ToString() => Fmt.N(X) + "," + Fmt.N(Y);
    }

    /// <summary>轴对齐包围盒（世界坐标）。</summary>
    public readonly struct Box
    {
        public readonly double MinX, MinY, MaxX, MaxY;
        public Box(double minX, double minY, double maxX, double maxY)
        { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }

        public static readonly Box Empty = new Box(double.PositiveInfinity, double.PositiveInfinity,
                                                   double.NegativeInfinity, double.NegativeInfinity);
        public bool IsValid => MinX <= MaxX && MinY <= MaxY;
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public Pt Center => new Pt((MinX + MaxX) / 2, (MinY + MaxY) / 2);

        public static Box Of(Pt p) => new Box(p.X, p.Y, p.X, p.Y);

        public static Box Union(Box a, Box b)
        {
            if (!b.IsValid) return a;
            if (!a.IsValid) return b;
            return new Box(Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY),
                           Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY));
        }

        public static Box Union(IEnumerable<Pt> pts)
        {
            var r = Empty;
            foreach (var p in pts) r = Union(r, Of(p));
            return r;
        }

        /// <summary>x 区间是否与另一个 x 区间重叠。穿轮判定必须过这一关（见 rubric 的 ground_clear_of_wheels）。</summary>
        public bool OverlapsX(Box o) => !(o.MinX > MaxX || o.MaxX < MinX);

        public bool ContainsX(double x) => x >= MinX && x <= MaxX;
        public bool ContainsY(double y) => y >= MinY && y <= MaxY;
    }

    /// <summary>SVG 的 2x3 仿射矩阵，语义同 matrix(a b c d e f)：x' = a·x + c·y + e，y' = b·x + d·y + f。</summary>
    public readonly struct Mat
    {
        public readonly double A, B, C, D, E, F;
        public Mat(double a, double b, double c, double d, double e, double f)
        { A = a; B = b; C = c; D = d; E = e; F = f; }

        public static readonly Mat Identity = new Mat(1, 0, 0, 1, 0, 0);
        public bool IsIdentity => A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0;

        /// <summary>组合：this ∘ o，即先施 o 再施 this。嵌套元素的 world = parentWorld * own。</summary>
        public Mat Mul(Mat o) => new Mat(
            A * o.A + C * o.B,
            B * o.A + D * o.B,
            A * o.C + C * o.D,
            B * o.C + D * o.D,
            A * o.E + C * o.F + E,
            B * o.E + D * o.F + F);

        public Pt Apply(Pt p) => new Pt(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

        public static Mat Translate(double tx, double ty) => new Mat(1, 0, 0, 1, tx, ty);
        public static Mat Scale(double sx, double sy) => new Mat(sx, 0, 0, sy, 0, 0);
        public static Mat SkewX(double deg) => new Mat(1, 0, Tan(deg), 1, 0, 0);
        public static Mat SkewY(double deg) => new Mat(1, Tan(deg), 0, 1, 0, 0);
        public static Mat Rotate(double deg)
        {
            double r = deg * Math.PI / 180.0;
            return new Mat(Math.Cos(r), Math.Sin(r), -Math.Sin(r), Math.Cos(r), 0, 0);
        }
        public static Mat RotateAt(double deg, double cx, double cy)
            => Translate(cx, cy).Mul(Rotate(deg)).Mul(Translate(-cx, -cy));

        private static double Tan(double deg) => Math.Tan(deg * Math.PI / 180.0);

        public override string ToString() =>
            "matrix(" + Fmt.N(A) + " " + Fmt.N(B) + " " + Fmt.N(C) + " " + Fmt.N(D) + " " + Fmt.N(E) + " " + Fmt.N(F) + ")";
    }

    /// <summary>可测量的几何基元。局部坐标 + 附带世界矩阵，对外一律返回世界坐标结果。</summary>
    public abstract class Geom
    {
        public Mat World = Mat.Identity;

        /// <summary>世界坐标下的轴对齐包围盒。</summary>
        public abstract Box Bounds();

        /// <summary>世界坐标下的折线采样，用于命中测试与近似判定。</summary>
        public abstract List<Pt> Flatten(int samples);

        /// <summary>世界坐标点到本几何的最短距离（近似，基于采样折线）。</summary>
        public double NearestDist(Pt p)
        {
            var pts = Flatten(64);
            double best = double.PositiveInfinity;
            for (int i = 0; i + 1 < pts.Count; i++)
                best = Math.Min(best, DistToSegment(p, pts[i], pts[i + 1]));
            if (pts.Count == 1) best = Math.Min(best, Math.Sqrt(Sq(p.X - pts[0].X) + Sq(p.Y - pts[0].Y)));
            return best;
        }

        /// <summary>点到线段的最短距离。</summary>
        public static double DistToSegment(Pt p, Pt a, Pt b)
        {
            double vx = b.X - a.X, vy = b.Y - a.Y;
            double wx = p.X - a.X, wy = p.Y - a.Y;
            double len2 = vx * vx + vy * vy;
            double t = len2 <= 1e-12 ? 0 : (wx * vx + wy * vy) / len2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double dx = wx - t * vx, dy = wy - t * vy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        internal static double Sq(double v) => v * v;
    }

    public sealed class GeoLine : Geom
    {
        public readonly Pt A, B;
        public GeoLine(Pt a, Pt b) { A = a; B = b; }

        public override Box Bounds()
        {
            var a = World.Apply(A);
            var b = World.Apply(B);
            return new Box(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        public override List<Pt> Flatten(int samples) => new List<Pt> { World.Apply(A), World.Apply(B) };
    }

    /// <summary>二次贝塞尔。仿射变换后仍是二次贝塞尔且控制点线性映射，故先变换再解导数根 → 精确包围盒。</summary>
    public sealed class GeoQuad : Geom
    {
        public readonly Pt P0, P1, P2;
        public GeoQuad(Pt p0, Pt p1, Pt p2) { P0 = p0; P1 = p1; P2 = p2; }

        public override Box Bounds()
        {
            var p0 = World.Apply(P0); var p1 = World.Apply(P1); var p2 = World.Apply(P2);
            var pts = new List<Pt> { p0, p2 };
            // B'(t) 每个坐标轴是一次式 → 单根。两根轴各自求根，再统一回代求点。
            var ts = new List<double>();
            StationaryT(p0.X, p1.X, p2.X, ts);
            StationaryT(p0.Y, p1.Y, p2.Y, ts);
            foreach (var t in ts)
            {
                double mt = 1 - t;
                pts.Add(World.Apply(new Pt(
                    mt * mt * P0.X + 2 * mt * t * P1.X + t * t * P2.X,
                    mt * mt * P0.Y + 2 * mt * t * P1.Y + t * t * P2.Y)));
            }
            return Box.Union(pts);
        }

        private static void StationaryT(double c0, double c1, double c2, List<double> into)
        {
            double den = c0 - 2 * c1 + c2;
            if (Math.Abs(den) < 1e-12) return;
            double t = (c0 - c1) / den;
            if (t > 0 && t < 1) into.Add(t);
        }

        public override List<Pt> Flatten(int samples)
        {
            var list = new List<Pt>(samples + 1);
            for (int i = 0; i <= samples; i++)
            {
                double t = (double)i / samples, mt = 1 - t;
                list.Add(World.Apply(new Pt(
                    mt * mt * P0.X + 2 * mt * t * P1.X + t * t * P2.X,
                    mt * mt * P0.Y + 2 * mt * t * P1.Y + t * t * P2.Y)));
            }
            return list;
        }
    }

    /// <summary>三次贝塞尔。同上：变换控制点后解 B'(t)=0 的二次方程求驻点。</summary>
    public sealed class GeoCubic : Geom
    {
        public readonly Pt P0, P1, P2, P3;
        public GeoCubic(Pt p0, Pt p1, Pt p2, Pt p3) { P0 = p0; P1 = p1; P2 = p2; P3 = p3; }

        public override Box Bounds()
        {
            var p0 = World.Apply(P0); var p1 = World.Apply(P1);
            var p2 = World.Apply(P2); var p3 = World.Apply(P3);
            var pts = new List<Pt> { p0, p3 };
            foreach (var t in CubicStationaryT(p0.X, p1.X, p2.X, p3.X)) pts.Add(Cat(p0, p1, p2, p3, t));
            foreach (var t in CubicStationaryT(p0.Y, p1.Y, p2.Y, p3.Y)) pts.Add(Cat(p0, p1, p2, p3, t));
            return Box.Union(pts);
        }

        private static Pt Cat(Pt a, Pt b, Pt c, Pt d, double t)
        {
            double mt = 1 - t;
            return new Pt(mt * mt * mt * a.X + 3 * mt * mt * t * b.X + 3 * mt * t * t * c.X + t * t * t * d.X,
                          mt * mt * mt * a.Y + 3 * mt * mt * t * b.Y + 3 * mt * t * t * c.Y + t * t * t * d.Y);
        }

        /// <summary>
        /// B'(t) 的系数。展开 y(t) 后 y'(t) = 3(-y0+3y1-3y2+y3)t² + 6(y0-2y1+y2)t + 3(y1-y0)，
        /// 三项同除 3 得下面这组（**必须同缩放**，只除其中两项会让根跑掉）：
        ///   a = -y0+3y1-3y2+y3，b = 2(y0-2y1+y2)，c = y1-y0
        /// </summary>
        private static IEnumerable<double> CubicStationaryT(double c0, double c1, double c2, double c3)
        {
            double a = -c0 + 3 * c1 - 3 * c2 + c3;
            double b = 2 * (c0 - 2 * c1 + c2);
            double c = c1 - c0;
            if (Math.Abs(a) < 1e-12)
            {
                if (Math.Abs(b) > 1e-12)
                {
                    double t = -c / b;
                    if (t > 0 && t < 1) yield return t;
                }
                yield break;
            }
            double disc = b * b - 4 * a * c;
            if (disc < 0) yield break;
            double sq = Math.Sqrt(disc);
            foreach (var t in new[] { (-b + sq) / (2 * a), (-b - sq) / (2 * a) })
                if (t > 0 && t < 1) yield return t;
        }

        public override List<Pt> Flatten(int samples)
        {
            var list = new List<Pt>(samples + 1);
            for (int i = 0; i <= samples; i++) list.Add(Cat(P0, P1, P2, P3, (double)i / samples));
            return list;
        }
    }

    /// <summary>
    /// 椭圆弧。内部转成圆心参数式（SVG F.6.5）。
    /// 包围盒 = 解析四个极值角 + 稠密采样兜底：非均匀缩放会让采样点略微低估，
    /// 但对 6px 容差的贴地判定而言误差远小于 0.1px，且绝不高估。
    /// 退化输入（rx/ry 为 0、端点重合）由 PathParser 挡在外面，不会构造本类。
    /// </summary>
    public sealed class GeoArc : Geom
    {
        public readonly double Cx, Cy, Rx, Ry, Theta1, DTheta, XAxisRot;

        public GeoArc(Pt p0, Pt p1, double rx, double ry, double xAxisRotDeg, bool largeArc, bool sweep)
        {
            XAxisRot = xAxisRotDeg * Math.PI / 180.0;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            double dx = (p0.X - p1.X) / 2, dy = (p0.Y - p1.Y) / 2;
            double cosR = Math.Cos(XAxisRot), sinR = Math.Sin(XAxisRot);
            double x1p = cosR * dx + sinR * dy;
            double y1p = -sinR * dx + cosR * dy;
            double lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
            if (lam > 1) { double s = Math.Sqrt(lam); rx *= s; ry *= s; }   // 半径太小，按比例放大
            double den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
            if (den < 1e-12)
            {
                // 两端点重合：退化为一个点，包围盒与采样都落在 p0 上，不参与任何极值推断
                Cx = p0.X; Cy = p0.Y; Rx = rx; Ry = ry; Theta1 = 0; DTheta = 0;
                return;
            }
            double num = (rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p) / den;
            double coef = num <= 0 ? 0 : Math.Sqrt(num);
            if (largeArc == sweep) coef = -coef;
            double cxp = coef * rx * y1p / ry;
            double cyp = -coef * ry * x1p / rx;
            Cx = cosR * cxp - sinR * cyp + (p0.X + p1.X) / 2;
            Cy = sinR * cxp + cosR * cyp + (p0.Y + p1.Y) / 2;
            Rx = rx; Ry = ry;
            Theta1 = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
            double delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
            if (!sweep && delta > 0) delta -= 2 * Math.PI;
            if (sweep && delta < 0) delta += 2 * Math.PI;
            DTheta = delta;
        }

        private static double Angle(double ux, double uy, double vx, double vy)
        {
            double dot = ux * vx + uy * vy;
            double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
            double ang = Math.Acos(Math.Max(-1, Math.Min(1, dot / (len <= 1e-12 ? 1e-12 : len))));
            return ux * vy - uy * vx < 0 ? -ang : ang;
        }

        public Pt At(double t)
        {
            double cosR = Math.Cos(XAxisRot), sinR = Math.Sin(XAxisRot);
            double x = Rx * Math.Cos(t), y = Ry * Math.Sin(t);
            return new Pt(cosR * x - sinR * y + Cx, sinR * x + cosR * y + Cy);
        }

        public override Box Bounds()
        {
            var pts = new List<Pt>();
            // 四个解析极值角：x 极值在 tan t = -(Ry·sinφ)/(Rx·cosφ)，y 极值在 tan t = (Ry·cosφ)/(Rx·sinφ)
            AddIfInSweep(Math.Atan2(-Ry * Math.Sin(XAxisRot), Rx * Math.Cos(XAxisRot)), pts);
            AddIfInSweep(Math.Atan2(Ry * Math.Cos(XAxisRot), Rx * Math.Sin(XAxisRot)), pts);
            for (int i = 0; i <= 96; i++) pts.Add(World.Apply(At(Theta1 + DTheta * i / 96)));
            return Box.Union(pts);
        }

        private void AddIfInSweep(double baseAng, List<Pt> into)
        {
            for (int k = 0; k < 2; k++)
            {
                double a = baseAng + k * Math.PI;
                double rel = a - Theta1;
                double two = 2 * Math.PI;
                rel = ((rel % two) + two) % two;
                if (DTheta >= 0 ? rel <= DTheta + 1e-9 : rel <= -DTheta + 1e-9)
                    into.Add(World.Apply(At(Theta1 + (DTheta >= 0 ? rel : -rel))));
            }
        }

        public override List<Pt> Flatten(int samples)
        {
            var list = new List<Pt>(samples + 1);
            for (int i = 0; i <= samples; i++) list.Add(World.Apply(At(Theta1 + DTheta * i / samples)));
            return list;
        }
    }

    /// <summary>圆 / 椭圆。仿射映射后仍是椭圆，其轴对齐包围盒有闭式解：
    /// halfWidth = √((a·rx)² + (c·ry)²)，halfHeight = √((b·rx)² + (d·ry)²)。旋转下亦精确。</summary>
    public sealed class GeoEllipse : Geom
    {
        public readonly double Cx, Cy, Rx, Ry;
        public GeoEllipse(double cx, double cy, double rx, double ry) { Cx = cx; Cy = cy; Rx = rx; Ry = ry; }

        public override Box Bounds()
        {
            var c = World.Apply(new Pt(Cx, Cy));
            double hw = Math.Sqrt(GeoMath.Sq(World.A * Rx) + GeoMath.Sq(World.C * Ry));
            double hh = Math.Sqrt(GeoMath.Sq(World.B * Rx) + GeoMath.Sq(World.D * Ry));
            return new Box(c.X - hw, c.Y - hh, c.X + hw, c.Y + hh);
        }

        public Pt At(double t) => new Pt(Cx + Rx * Math.Cos(t), Cy + Ry * Math.Sin(t));

        public override List<Pt> Flatten(int samples)
        {
            var list = new List<Pt>(samples + 1);
            for (int i = 0; i <= samples; i++) list.Add(World.Apply(At(2 * Math.PI * i / samples)));
            return list;
        }
    }

    internal static class GeoMath
    {
        public static double Sq(double v) => v * v;
    }

    /// <summary>一条子路径：若干首尾相接的 Geom，加闭合标志。所有坐标为局部，World 已附在各 Geom 上。</summary>
    public sealed class Subpath
    {
        public readonly List<Geom> Segs = new List<Geom>();
        public bool Closed;
        public Pt Start;
        public Pt Last;

        public Box Bounds()
        {
            var b = Box.Empty;
            foreach (var g in Segs) b = Box.Union(b, g.Bounds());
            return b;
        }

        public IEnumerable<Pt> Flatten(int samples)
        {
            foreach (var g in Segs)
                foreach (var p in g.Flatten(samples)) yield return p;
        }
    }

    internal static class Fmt
    {
        public static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
