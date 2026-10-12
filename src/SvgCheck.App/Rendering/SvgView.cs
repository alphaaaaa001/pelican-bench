using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SvgCheck.Core.Geometry;
using SvgCheck.Core.Parsing;
using Pt = SvgCheck.Core.Geometry.Pt;
using Box = SvgCheck.Core.Geometry.Box;
using Mat = SvgCheck.Core.Geometry.Mat;

namespace SvgCheck.App.Rendering
{
    /// <summary>
    /// SVG 预览与拾取。
    ///
    /// 两个刻意的取舍：
    /// 1) 几何按**元素局部坐标**构建，再套 MatrixTransform(el.World)。若把变换烘进每个点，
    ///    画笔粗细就不随 viewBox 缩放了 —— 而 SVG 的 stroke-width 是用户坐标里的量。
    /// 2) 拾取用 Core 的距离函数，不用 WPF 自带的命中测试。大量元素是 fill:none 的描边，
    ///    点它中间什么也点不中；而辐条、地面线、车架管恰恰全是描边，偏偏最需要被点中。
    /// </summary>
    public sealed class SvgView : FrameworkElement
    {
        private SvgDocument _doc;
        private Matrix _worldToScreen = Matrix.Identity;
        private bool _dragging;
        private Point _dragLast;

        private readonly Dictionary<int, Geometry> _geoCache = new Dictionary<int, Geometry>();
        private readonly HashSet<int> _highlight = new HashSet<int>();
        private readonly Dictionary<int, string> _roleTags = new Dictionary<int, string>();

        public double Zoom { get; private set; } = 1.0;
        public Point Pan { get; private set; } = new Point(0, 0);

        /// <summary>右键点中某个元素时抛出（元素 + 屏幕坐标，供弹菜单）。</summary>
        public event Action<SvgEl, Point> Picked;

        public SvgDocument Document
        {
            get => _doc;
            set { _doc = value; _geoCache.Clear(); ResetView(); }
        }

        public void SetHighlight(IEnumerable<int> orders)
        {
            _highlight.Clear();
            if (orders != null) foreach (var o in orders) if (o >= 0) _highlight.Add(o);
            InvalidateVisual();
        }

        public void SetRoleTags(Dictionary<int, string> tags)
        {
            _roleTags.Clear();
            if (tags != null) foreach (var kv in tags) _roleTags[kv.Key] = kv.Value;
            InvalidateVisual();
        }

        // ---------------------------------------------------------------- 绘制

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, Math.Max(0, w), Math.Max(0, h)));
            if (_doc == null || w <= 0 || h <= 0) return;

            var vb = _doc.ViewBox ?? new Box(0, 0, 300, 150);
            double s = Math.Min(w / Math.Max(vb.Width, 1e-6), h / Math.Max(vb.Height, 1e-6)) * Zoom;
            double ox = (w - vb.Width * s) / 2 + Pan.X - vb.MinX * s;
            double oy = (h - vb.Height * s) / 2 + Pan.Y - vb.MinY * s;
            var m = new Matrix(s, 0, 0, s, ox, oy);
            _worldToScreen = m;

            dc.PushTransform(new MatrixTransform(m));
            try
            {
                // 画布边框：让「主体被裁切」这件事肉眼可见
                dc.DrawRectangle(null, new Pen(BorderBrush, 1 / s),
                    new Rect(vb.MinX, vb.MinY, vb.Width, vb.Height));

                var order = _doc.RenderList().ToList();
                foreach (var el in order)
                {
                    var g = GeometryOf(el);
                    if (g == null) continue;
                    Paint(dc, el, g);
                }
                foreach (var el in order.Where(e => _roleTags.ContainsKey(e.Order)))
                {
                    var g = GeometryOf(el);
                    if (g == null) continue;
                    g.Transform = new MatrixTransform(MatrixOf(el.World));
                    dc.DrawGeometry(null, new Pen(RoleBrush, 3 / s), g);
                }
                foreach (var el in order.Where(e => _highlight.Contains(e.Order)))
                {
                    var g = GeometryOf(el);
                    if (g == null) continue;
                    g.Transform = new MatrixTransform(MatrixOf(el.World));
                    double sw = Math.Max(el.Num("stroke-width", 1), 4 / s);
                    dc.DrawGeometry(null, new Pen(HlBrush, sw), g);
                }
            }
            finally { dc.Pop(); }

            DrawLabels(dc);
        }

        private void Paint(DrawingContext dc, SvgEl el, Geometry g)
        {
            g.Transform = new MatrixTransform(MatrixOf(el.World));

            Brush fill = FillBrush(el);
            Pen pen = null;
            var stroke = StrokeBrush(el);
            double sw = el.Num("stroke-width", 1);
            if (stroke != null && sw > 0)
            {
                pen = new Pen(stroke, sw)
                {
                    StartLineCap = Cap(el.Get("stroke-linecap")),
                    EndLineCap = Cap(el.Get("stroke-linecap")),
                    LineJoin = Join(el.Get("stroke-linejoin"))
                };
                var dash = Dash(el.Get("stroke-dasharray"));
                if (dash != null) pen.DashStyle = new DashStyle(dash, 0);
                pen.Freeze();
            }
            if (fill != null && fill.CanFreeze && !fill.IsFrozen) fill.Freeze();
            if (fill != null || pen != null) dc.DrawGeometry(fill, pen, g);
        }

        /// <summary>元素 → 局部坐标几何（缓存）。返回的是克隆，调用方可安全改 Transform。</summary>
        private Geometry GeometryOf(SvgEl el)
        {
            if (el == null || !el.HasGeometry) return null;
            if (!_geoCache.TryGetValue(el.Order, out var built))
            {
                var fg = new GeometryGroup { FillRule = ToFillRule(el.Get("fill-rule")) };
                foreach (var sp in el.Subpaths)
                {
                    var one = BuildSubpath(sp, ToFillRule(el.Get("fill-rule")));
                    if (one != null) fg.Children.Add(one);
                }
                if (fg.Children.Count == 0) return null;
                fg.Freeze();
                built = fg;
                _geoCache[el.Order] = fg;
            }
            return built.Clone();
        }

        private static Geometry BuildSubpath(Core.Geometry.Subpath sp, FillRule rule)
        {
            if (sp.Segs.Count == 0) return null;
            var sg = new StreamGeometry { FillRule = rule };
            using (var gc = sg.Open())
            {
                gc.BeginFigure(StartOf(sp.Segs[0]), true, sp.Closed);
                foreach (var g in sp.Segs)
                {
                    switch (g)
                    {
                        case GeoLine l:
                            gc.LineTo(W(l.B), true, false);
                            break;
                        case GeoQuad q:
                            gc.QuadraticBezierTo(W(q.P1), W(q.P2), true, false);
                            break;
                        case GeoCubic c:
                            gc.BezierTo(W(c.P1), W(c.P2), W(c.P3), true, false);
                            break;
                        case GeoArc a:
                            gc.ArcTo(W(a.At(a.Theta1 + a.DTheta)),
                                new Size(Math.Abs(a.Rx), Math.Abs(a.Ry)),
                                a.XAxisRot * 180 / Math.PI,
                                Math.Abs(a.DTheta) > Math.PI,
                                a.DTheta >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                                true, false);
                            break;
                        case GeoEllipse e:
                            // 整椭圆用两段半弧拼，避开单弧退化
                            var p0 = W(e.At(0)); var p1 = W(e.At(Math.PI));
                            gc.ArcTo(p1, new Size(Math.Abs(e.Rx), Math.Abs(e.Ry)), 0, true,
                                SweepDirection.Clockwise, true, false);
                            gc.ArcTo(p0, new Size(Math.Abs(e.Rx), Math.Abs(e.Ry)), 0, true,
                                SweepDirection.Clockwise, true, false);
                            break;
                    }
                }
            }
            sg.Freeze();
            return sg;
        }

        /// <summary>子路径起点。Subpath.Start 对 circle/ellipse/rect 未必可靠，故优先从首段推。</summary>
        private static Point StartOf(Geom g) => g switch
        {
            GeoLine l => W(l.A),
            GeoQuad q => W(q.P0),
            GeoCubic c => W(c.P0),
            GeoArc a => W(a.At(a.Theta1)),
            GeoEllipse e => W(e.At(0)),
            _ => new Point(0, 0)
        };

        private static Point W(Pt p) => new Point(p.X, p.Y);
        private static Matrix MatrixOf(Mat m) => new Matrix(m.A, m.B, m.C, m.D, m.E, m.F);

        private static FillRule ToFillRule(string s)
            => string.Equals(s, "evenodd", StringComparison.OrdinalIgnoreCase) ? FillRule.EvenOdd : FillRule.Nonzero;

        private static PenLineCap Cap(string s) => s switch
        {
            "round" => PenLineCap.Round,
            "square" => PenLineCap.Square,
            _ => PenLineCap.Flat
        };

        private static PenLineJoin Join(string s) => s switch
        {
            "round" => PenLineJoin.Round,
            "bevel" => PenLineJoin.Bevel,
            _ => PenLineJoin.Miter
        };

        private static DoubleCollection Dash(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Trim() == "none") return null;
            var nums = s.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0.0)
                .Where(d => d > 0).ToList();
            if (nums.Count == 0) return null;
            var c = new DoubleCollection(nums);
            c.Freeze();
            return c;
        }

        private Brush FillBrush(SvgEl el)
        {
            var v = el.Get("fill");
            if (string.IsNullOrWhiteSpace(v) || string.Equals(v, "none", StringComparison.OrdinalIgnoreCase)) return null;
            var b = BrushFor(el, v);
            if (b == null) return null;
            ApplyOpacity(b, el.Num("fill-opacity", 1) * el.Num("opacity", 1));
            return b;
        }

        private Brush StrokeBrush(SvgEl el)
        {
            var v = el.Get("stroke");
            if (string.IsNullOrWhiteSpace(v) || string.Equals(v, "none", StringComparison.OrdinalIgnoreCase)) return null;
            var b = BrushFor(el, v);
            if (b == null) return null;
            ApplyOpacity(b, el.Num("stroke-opacity", 1) * el.Num("opacity", 1));
            return b;
        }

        /// <summary>解析一个 paint 值：字面颜色走 ColorUtil，url(#id) 走渐变。</summary>
        private Brush BrushFor(SvgEl el, string value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (!value.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return ColorUtil.BrushFrom(value);

            string id = value.Substring(4).TrimEnd(')').Trim().Trim('"', '\'');
            if (id.StartsWith("#", StringComparison.Ordinal)) id = id.Substring(1);
            var grad = _doc?.FindById(id);
            if (grad == null) return ColorUtil.Missing();          // 断链已由 PreflightRefs 报过
            return GradientBrush(el, grad);
        }

        /// <summary>
        /// SVG 渐变 → WPF 渐变画刷。
        ///
        /// 默认 gradientUnits="objectBoundingBox" 的坐标是 0..1 的相对量，而 WPF 渐变画刷的
        /// MappingMode 默认正是 RelativeBoundingBox —— 两边可以直接对映，不用换算。
        /// userSpaceOnUse 才是绝对用户坐标，那时改用元素自己的局部包围盒换算（几何是局部坐标
        /// 再套 MatrixTransform，所以这里必须用**局部**包围盒，不能用世界的）。
        /// </summary>
        private static Brush GradientBrush(SvgEl target, SvgEl grad)
        {
            var stops = Stops(grad);
            if (stops.Count == 0) return ColorUtil.Missing();
            bool radial = string.Equals(grad.Tag, "radialGradient", StringComparison.OrdinalIgnoreCase);
            bool userSpace = string.Equals(grad.Get("gradientUnits"), "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);

            if (radial)
            {
                double cx = grad.Num("cx", 0.5), cy = grad.Num("cy", 0.5), r = grad.Num("r", 0.5);
                double fx = grad.Num("fx", cx), fy = grad.Num("fy", cy);
                var b = new RadialGradientBrush();
                foreach (var s in stops) b.GradientStops.Add(s);
                if (userSpace)
                {
                    var lb = LocalBounds(target);
                    b.MappingMode = BrushMappingMode.Absolute;
                    b.Center = new Point(lb.MinX + cx * lb.Width, lb.MinY + cy * lb.Height);
                    b.RadiusX = r * lb.Width; b.RadiusY = r * lb.Height;
                    b.GradientOrigin = new Point(lb.MinX + fx * lb.Width, lb.MinY + fy * lb.Height);
                }
                else
                {
                    b.Center = new Point(cx, cy);
                    b.RadiusX = r; b.RadiusY = r;
                    b.GradientOrigin = new Point(fx, fy);
                }
                b.SpreadMethod = Spread(grad);
                return b;
            }
            else
            {
                double x1 = grad.Num("x1", 0), y1 = grad.Num("y1", 0);
                double x2 = grad.Num("x2", 1), y2 = grad.Num("y2", 0);
                var b = new LinearGradientBrush();
                foreach (var s in stops) b.GradientStops.Add(s);
                if (userSpace)
                {
                    b.MappingMode = BrushMappingMode.Absolute;   // 坐标本来就是用户坐标，直接用
                }
                b.StartPoint = new Point(x1, y1);
                b.EndPoint = new Point(x2, y2);
                b.SpreadMethod = Spread(grad);
                return b;
            }
        }

        private static GradientSpreadMethod Spread(SvgEl grad) => grad.Get("spreadMethod") switch
        {
            "repeat" => GradientSpreadMethod.Repeat,
            "reflect" => GradientSpreadMethod.Reflect,
            _ => GradientSpreadMethod.Pad
        };

        /// <summary>元素在**局部**坐标下的包围盒（World 尚未套用前），userSpaceOnUse 渐变换算用。</summary>
        private static Box LocalBounds(SvgEl el)
        {
            var b = Box.Empty;
            foreach (var sp in el.Subpaths)
                foreach (var g in sp.Segs)
                {
                    var saved = g.World; g.World = Mat.Identity;
                    b = Box.Union(b, g.Bounds());
                    g.World = saved;
                }
            return b.IsValid ? b : el.Bounds;
        }

        private static List<GradientStop> Stops(SvgEl grad)
        {
            var list = new List<GradientStop>();
            foreach (var s in grad.Children)
            {
                if (!string.Equals(s.Tag, "stop", StringComparison.OrdinalIgnoreCase)) continue;
                double t = 0;
                var off = (s.Get("offset") ?? "0").Trim();
                if (off.EndsWith("%", StringComparison.Ordinal))
                {
                    if (double.TryParse(off.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        t = pct / 100.0;
                }
                else if (!double.TryParse(off, NumberStyles.Float, CultureInfo.InvariantCulture, out t)) t = 0;
                t = Math.Max(0, Math.Min(1, t));

                var col = ColorUtil.ColorFrom(s.Get("stop-color") ?? "#000000");
                if (!col.HasValue) continue;                 // 解不出来的 stop 直接跳过，不当黑色凑数
                double op = s.Num("stop-opacity", 1);
                var c = col.Value;
                c.A = (byte)Math.Round(Math.Max(0, Math.Min(1, op)) * 255);
                list.Add(new GradientStop(c, t));
            }
            list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return list;
        }

        private static void ApplyOpacity(Brush b, double o)
        {
            o = Math.Max(0, Math.Min(1, o));
            if (Math.Abs(o - 1) < 1e-6) return;
            b.Opacity = o;
        }

        // ---------------------------------------------------------------- 交互

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.ChangedButton != MouseButton.Left) return;
            _dragging = true;
            _dragLast = e.GetPosition(this);
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging) return;
            var p = e.GetPosition(this);
            double dx = p.X - _dragLast.X, dy = p.Y - _dragLast.Y;
            Pan = new Point(Pan.X + dx, Pan.Y + dy);
            _dragLast = p;
            InvalidateVisual();
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.ChangedButton != MouseButton.Left) return;
            _dragging = false;
            ReleaseMouseCapture();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
            Zoom = Math.Max(0.1, Math.Min(40, Zoom * factor));
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonUp(e);
            var pos = e.GetPosition(this);
            var el = PickAt(pos);
            if (el != null) { Picked?.Invoke(el, pos); e.Handled = true; }
        }

        private Point ScreenToWorld(Point p)
        {
            var m = _worldToScreen;
            if (m.M11 == 0 && m.M22 == 0) return p;
            m.Invert();
            return m.Transform(p);
        }

        private double Scale => _worldToScreen.M11 == 0 ? 1 : Math.Abs(_worldToScreen.M11);

        /// <summary>
        /// 拾取：屏幕点换到世界坐标，在所有可渲染元素里找最近的一个。
        /// 阈值随描边宽度与缩放走 —— 细线要放大才点得中，这符合直觉。
        /// 填充区域的内部也算命中（射线法，不是包围盒，否则大椭圆会吞掉它旁边的细线）。
        /// </summary>
        public SvgEl PickAt(Point screen)
        {
            if (_doc == null) return null;
            var wp = ScreenToWorld(screen);
            var p = new Pt(wp.X, wp.Y);
            SvgEl best = null; double bestD = double.MaxValue;

            foreach (var el in _doc.RenderList())
            {
                double tol = Math.Max(el.Num("stroke-width", 1), 8 / Scale);
                double d = double.MaxValue;
                foreach (var sp in el.Subpaths)
                    foreach (var g in sp.Segs)
                        d = Math.Min(d, g.NearestDist(p));
                if (d <= tol && d < bestD) { bestD = d; best = el; continue; }

                var fill = el.Get("fill");
                bool painted = !string.IsNullOrEmpty(fill) && !string.Equals(fill, "none", StringComparison.OrdinalIgnoreCase);
                if (painted && el.Subpaths.Any(sp => sp.Closed && InClosed(sp, p)) && best == null)
                    best = el;
            }
            return best;
        }

        private static bool InClosed(Core.Geometry.Subpath sp, Pt p)
        {
            var pts = sp.Flatten(64).ToList();
            if (pts.Count < 3) return false;
            bool inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                var a = pts[i]; var b = pts[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) &&
                    p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y + 1e-12) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        public void ResetView() { Zoom = 1; Pan = new Point(0, 0); _geoCache.Clear(); InvalidateVisual(); }

        private void DrawLabels(DrawingContext dc)
        {
            if (_roleTags.Count == 0 || _doc == null) return;
            var tf = new Typeface("Segoe UI");
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var kv in _roleTags)
            {
                var el = _doc.RenderList().FirstOrDefault(e => e.Order == kv.Key);
                if (el == null || !el.Bounds.IsValid) continue;
                var sp = _worldToScreen.Transform(new Point(el.Bounds.MinX, el.Bounds.MinY));
                var ft = new FormattedText(kv.Value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    tf, 12, RoleBrush, dpi);
                dc.DrawRectangle(BgBrush, null, new Rect(sp.X - 2, sp.Y - 16, ft.Width + 4, 17));
                dc.DrawText(ft, new Point(sp.X, sp.Y - 15));
            }
        }

        // ---------------------------------------------------------------- 配色

        private static readonly Brush BgBrush = Frozen("#ff1e1f24");
        private static readonly Brush BorderBrush = Frozen("#449aa5b1");
        private static readonly Brush HlBrush = Frozen("#ffe5484d");
        private static readonly Brush RoleBrush = Frozen("#ff4ec9b0");

        private static Brush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }

    /// <summary>
    /// 渲染层的颜色入口。**语法以 Core 的 ColorParse 为唯一真源** —— 判据（亮度）和渲染
    /// 若各有一份颜色表，迟早出现"渲染画得出来、判据说不清"或反过来。这里只在 Core 表外的
    /// 命名色上回落到 WPF 的 ColorConverter，所以渲染是判据的超集：判据认得的渲染一定认得，
    /// 渲染多认的那部分，判据按【无定论】处理而不是瞎猜。
    ///
    /// 一律返回**未冻结**的新实例：调用方要按 fill-opacity / stroke-opacity / opacity 调整透明度，
    /// 而对已冻结的 Freezable 设属性会抛 InvalidOperationException（实测：围巾 opacity=.9 一发命中）。
    /// </summary>
    internal static class ColorUtil
    {
        public static Brush BrushFrom(string value)
        {
            var c = ColorFrom(value);
            return c.HasValue ? new SolidColorBrush(c.Value) : (Brush)Missing();
        }

        /// <summary>解析不出颜色时返回 null —— 调用方自己决定怎么兜底，别在这儿静默变黑。</summary>
        public static Color? ColorFrom(string value)
        {
            var rgb = Core.Parsing.ColorParse.ToRgb(value);
            if (rgb.HasValue) return Color.FromRgb(rgb.Value.R, rgb.Value.G, rgb.Value.B);

            value = value?.Trim();
            if (string.IsNullOrEmpty(value)) return null;
            if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                if (ColorConverter.ConvertFromString(value) is Color c) return c;
            }
            catch (FormatException) { /* 表外且 WPF 也不认识 → 交给 Missing() 的灰 */ }
            catch (OverflowException) { }
            return null;
        }

        /// <summary>没实现的特性一律中性灰：看见灰块就知道这儿有个缺口，而不是被静默骗过去。</summary>
        public static Brush Missing() => new SolidColorBrush(Color.FromRgb(0x8a, 0x8f, 0x96));
    }
}
