using System;
using System.Collections.Generic;
using System.Linq;
using SvgCheck.Core.Geometry;
using SvgCheck.Core.Parsing;

namespace SvgCheck.Core.Rules
{
    public enum RoleKind { Point, Circle, Line, Any }

    /// <summary>一次角色绑定：用户（或自动推定）认定某个元素在图里扮演什么。</summary>
    public sealed class RoleBinding
    {
        public string RoleId = "";
        public SvgEl El;
        public RoleKind Kind;
        public Pt Point;
        public CircleFact Circle;
        public HorizFact Line;
        public Box Bounds;
        /// <summary>true = 自动推定，可被用户覆盖；false = 用户亲自指派，自动推定不得抢占。</summary>
        public bool Auto;

        public double X => Circle != null ? Circle.Cx : Line != null ? (Line.X0 + Line.X1) / 2 : Point.X;
        public double Y => Circle != null ? Circle.Cy : Line != null ? Line.Y : Point.Y;
        public double R => Circle?.R ?? 0;
    }

    /// <summary>
    /// 角色表。它是"语义项能不能自动判"的开关：
    /// 朝向、脚落踏板、挂点这三条在几何上完全可算，卡住的从来不是算术而是**认不出谁是谁**。
    /// 自动推定只敢认轮子与地面（规则与 Python 侧一致）；其余交给用户在预览里点。
    ///
    /// 每个角色可以绑**多个**元素 —— 脚有两只、踏板有两块。单值角色用 replace:true 保持一对一。
    /// </summary>
    public sealed class Roles
    {
        private readonly Dictionary<string, List<RoleBinding>> _map =
            new Dictionary<string, List<RoleBinding>>(StringComparer.OrdinalIgnoreCase);

        public event Action Changed;

        public bool Has(string id) => !string.IsNullOrEmpty(id) && _map.TryGetValue(id, out var l) && l.Count > 0;

        /// <summary>取该角色的第一个绑定（单值角色用这个）。</summary>
        public RoleBinding Get(string id)
            => _map.TryGetValue(id ?? "", out var l) && l.Count > 0 ? l[0] : null;

        /// <summary>取该角色的全部绑定（脚、踏板这类多值角色用这个）。</summary>
        public IReadOnlyList<RoleBinding> AllOf(string id)
            => _map.TryGetValue(id ?? "", out var l) ? l : Array.Empty<RoleBinding>();

        public IEnumerable<RoleBinding> Bound => _map.Values.SelectMany(v => v);

        private void Put(RoleBinding b, bool replace)
        {
            if (!_map.TryGetValue(b.RoleId, out var l)) { l = new List<RoleBinding>(); _map[b.RoleId] = l; }
            if (replace) l.Clear();
            l.Add(b);
            Changed?.Invoke();
        }

        public void Assign(string roleId, SvgEl el, Facts f, RoleKind kind, Pt? pick = null,
                           bool auto = false, bool replace = true)
        {
            if (string.IsNullOrEmpty(roleId) || el == null) return;
            var b = new RoleBinding { RoleId = roleId, El = el, Kind = kind, Auto = auto };

            var c = f.Circles.FirstOrDefault(x => ReferenceEquals(x.El, el));
            if (c != null) { b.Circle = c; b.Point = new Pt(c.Cx, c.Cy); b.Bounds = c.Bounds; }

            var h = f.Horizontals.FirstOrDefault(x => ReferenceEquals(x.El, el));
            if (h != null && b.Circle == null) { b.Line = h; b.Point = new Pt((h.X0 + h.X1) / 2, h.Y); b.Bounds = h.AsBox; }

            if (b.Circle == null && b.Line == null)
            {
                b.Bounds = el.Bounds;
                b.Point = pick ?? (el.Bounds.IsValid ? el.Bounds.Center : new Pt(0, 0));
            }
            Put(b, replace);
        }

        public void AssignCircle(string roleId, CircleFact c, bool auto = false, bool replace = true)
        {
            if (c == null) return;
            Put(new RoleBinding
            {
                RoleId = roleId, El = c.El, Kind = RoleKind.Circle, Circle = c,
                Point = new Pt(c.Cx, c.Cy), Bounds = c.Bounds, Auto = auto
            }, replace);
        }

        public void AssignLine(string roleId, HorizFact h, bool auto = false, bool replace = true)
        {
            if (h == null) return;
            Put(new RoleBinding
            {
                RoleId = roleId, El = h.El, Kind = RoleKind.Line, Line = h,
                Point = new Pt((h.X0 + h.X1) / 2, h.Y), Bounds = h.AsBox, Auto = auto
            }, replace);
        }

        public void Clear(string roleId)
        {
            if (_map.Remove(roleId ?? "")) Changed?.Invoke();
        }

        /// <summary>清空所有自动推定（用户指派保留）—— 换文件时复用用户判断用得上。</summary>
        public void ClearAuto()
        {
            bool dirty = false;
            foreach (var kv in _map.ToList())
            {
                var keep = kv.Value.Where(b => !b.Auto).ToList();
                if (keep.Count != kv.Value.Count) dirty = true;
                if (keep.Count == 0) _map.Remove(kv.Key); else _map[kv.Key] = keep;
            }
            if (dirty) Changed?.Invoke();
        }

        public void Reset()
        {
            _map.Clear();
            Changed?.Invoke();
        }

        /// <summary>还缺哪些角色。空表 = 该规则的所有依赖都已就位。</summary>
        public List<string> Missing(IReadOnlyList<string> requires)
        {
            var miss = new List<string>();
            if (requires == null) return miss;
            foreach (var r in requires) if (!Has(r)) miss.Add(r);
            return miss;
        }

        /// <summary>
        /// 自动推定：只认轮子与地面，且绝不覆盖用户已指派的项。
        /// 规则与 svgcheck.py 完全一致（最大两个圆 = 轮、y 最大的长横线 = 地面），
        /// 这是两把尺子初值可比的前提。
        /// </summary>
        public void AutoInfer(Facts f)
        {
            var (rear, front) = f.GuessWheels();
            if (rear != null && !Has("wheel_rear")) AssignCircle("wheel_rear", rear, auto: true);
            if (front != null && !Has("wheel_front")) AssignCircle("wheel_front", front, auto: true);
            var g = f.GuessGround();
            if (g != null && !Has("ground")) AssignLine("ground", g, auto: true);
        }

        /// <summary>已绑定的两个轮子（按 x 升序），供判据取用。</summary>
        public (CircleFact rear, CircleFact front) Wheels()
        {
            var a = Get("wheel_rear")?.Circle;
            var b = Get("wheel_front")?.Circle;
            if (a == null || b == null) return (null, null);
            return a.Cx <= b.Cx ? (a, b) : (b, a);
        }

        public HorizFact Ground() => Get("ground")?.Line;
    }
}
