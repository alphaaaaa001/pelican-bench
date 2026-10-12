using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SvgCheck.Core.Rules
{
    /// <summary>一条档位判据。自上而下第一条命中生效；三者都不填即兜底 else。</summary>
    public sealed class Criterion
    {
        public double? MaxAbs, Max, Min;
        public double Score;
        public string Verdict = "";
        public string Text = "";

        public bool IsElse => !MaxAbs.HasValue && !Max.HasValue && !Min.HasValue;

        public bool Matches(double v)
        {
            if (MaxAbs.HasValue) return Math.Abs(v) <= MaxAbs.Value;
            if (Max.HasValue) return v <= Max.Value;
            if (Min.HasValue) return v >= Min.Value;
            return true;
        }
    }

    public sealed class RubricRule
    {
        public string Id = "";
        public string Name = "";
        public string Group = "";
        public string Measure = "";
        public string Why = "";
        public string UnknownText = "";
        public double Weight;
        public readonly List<string> Requires = new List<string>();
        public readonly List<Criterion> Criteria = new List<Criterion>();

        /// <summary>
        /// hybrid = 缺席不可被几何证明。实现拿不出正面证据时必须给 unknown，禁止用 fail 表达「我不知道」。
        /// 见 rubric 的 evidence_policy —— 这条来自一次真实诬告（喉囊明明画了却报未检测到）。
        /// </summary>
        public bool Hybrid;
    }

    public sealed class RubricGroup
    {
        public string Id = "";
        public string Name = "";
        public readonly List<RubricRule> Rules = new List<RubricRule>();
        public double Weight => Rules.Sum(r => r.Weight);
    }

    /// <summary>一个可指派的角色（用于"点预览指定角色"）。kind 决定候选元素该长什么样，multi 决定能否重复指派。</summary>
    public sealed class RoleSpec
    {
        public string Id = "";
        public string Name = "";
        public string Kind = "any";
        public string Hint = "";
        /// <summary>true = 一个角色可同时绑多个元素（脚有两只、踏板有两块）。</summary>
        public bool Multi;
    }

    /// <summary>
    /// rubric.json 的加载与求值。两把尺子的唯一判据来源 —— 代码里不许硬编码权重、容差或判据文案。
    /// </summary>
    public sealed class Rubric
    {
        public int Version;
        public readonly Dictionary<string, double> Tol = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public readonly List<RubricGroup> Groups = new List<RubricGroup>();
        public readonly List<RoleSpec> RoleSpecs = new List<RoleSpec>();
        public readonly List<RubricRule> Rules = new List<RubricRule>();   // 声明序，对拍要求
        public readonly List<string> ManualOnly = new List<string>();
        public string SourcePath = "";

        public double LowCoverage => Tol.TryGetValue("low_coverage", out var v) ? v : 0.6;
        public double T(string key) => Tol.TryGetValue(key, out var v) ? v : throw new KeyError("rubric 里没有容差键: " + key);

        public sealed class KeyError : Exception
        {
            public KeyError(string message) : base(message) { }
        }

        // ---------------------------------------------------------------- 定位与加载

        /// <summary>
        /// 找 rubric.json：从若干起点逐级向上找。不依赖 CWD —— 双击 exe 时 CWD 往往是 exe 目录，
        /// 而 fixtures 可能在别处。
        /// </summary>
        public static string Locate(string start = null)
        {
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(start))
            {
                string d = Path.GetDirectoryName(Path.GetFullPath(start));
                if (d != null) roots.Add(d);
            }
            roots.Add(AppContext.BaseDirectory);
            roots.Add(Directory.GetCurrentDirectory());

            foreach (var root in roots.Where(r => r != null).Distinct())
            {
                var dir = (Directory.Exists(root) ? root : Path.GetDirectoryName(root)) as string;
                for (int hop = 0; hop < 8 && dir != null; hop++)
                {
                    string cand = Path.Combine(dir, "rubric.json");
                    if (File.Exists(cand)) return cand;
                    var parent = Directory.GetParent(dir);
                    dir = parent?.FullName;
                }
            }
            return null;
        }

        public static Rubric Load(string path)
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var r = new Rubric { SourcePath = path };
            var root = doc.RootElement;
            r.Version = root.TryGetProperty("version", out var v) ? v.GetInt32() : 0;

            if (root.TryGetProperty("tolerances", out var tol) && tol.ValueKind == JsonValueKind.Object)
                foreach (var p in tol.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number)
                        r.Tol[p.Name] = p.Value.GetDouble();

            if (root.TryGetProperty("roles", out var ros) && ros.ValueKind == JsonValueKind.Array)
                foreach (var e in ros.EnumerateArray())
                    r.RoleSpecs.Add(new RoleSpec
                    {
                        Id = Str(e, "id"),
                        Name = Str(e, "name"),
                        Kind = string.IsNullOrEmpty(Str(e, "kind")) ? "any" : Str(e, "kind"),
                        Hint = Str(e, "hint"),
                        Multi = e.TryGetProperty("multi", out var mu) && mu.ValueKind == JsonValueKind.True
                    });

            var hybrid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("evidence_policy", out var ep) && ep.ValueKind == JsonValueKind.Object)
            {
                CollectPolicy(ep, r, hybrid);
            }

            if (root.TryGetProperty("groups", out var gs) && gs.ValueKind == JsonValueKind.Array)
                foreach (var ge in gs.EnumerateArray())
                {
                    var g = new RubricGroup
                    {
                        Id = Str(ge, "id"),
                        Name = Str(ge, "name")
                    };
                    if (ge.TryGetProperty("rules", out var rs) && rs.ValueKind == JsonValueKind.Array)
                        foreach (var re in rs.EnumerateArray())
                        {
                            var rule = ParseRule(re, r);
                            rule.Group = g.Id;
                            rule.Hybrid = hybrid.Contains(rule.Id);
                            g.Rules.Add(rule);
                            r.Rules.Add(rule);
                        }
                    r.Groups.Add(g);
                }

            if (root.TryGetProperty("manual_only", out var mo) && mo.ValueKind == JsonValueKind.Array)
                foreach (var e in mo.EnumerateArray()) r.ManualOnly.Add(Str(e, "name"));

            r.Validate();
            return r;
        }

        public static Rubric LoadOrThrow(string start = null)
        {
            var p = Locate(start);
            if (p == null)
                throw new FileNotFoundException(
                    "找不到 rubric.json。判据必须外置，不允许退回内置默认值。查找起点: " +
                    string.Join(", ", new[] { start ?? "", AppContext.BaseDirectory }));
            return Load(p);
        }

        private static void CollectPolicy(JsonElement ep, Rubric r, HashSet<string> hybrid)
        {
            foreach (var kind in new[] { "geometric", "hybrid" })
            {
                if (!ep.TryGetProperty(kind, out var k) || k.ValueKind != JsonValueKind.Object) continue;
                if (!k.TryGetProperty("rules", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String) continue;
                    var id = e.GetString()!;
                    if (kind == "hybrid") hybrid.Add(id);
                }
            }
        }

        private static RubricRule ParseRule(JsonElement re, Rubric r)
        {
            var rule = new RubricRule
            {
                Id = Str(re, "id"),
                Name = Str(re, "name"),
                Measure = Str(re, "measure"),
                Why = Str(re, "why"),
                UnknownText = Str(re, "unknown"),
                Weight = re.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 0
            };
            if (re.TryGetProperty("requires", out var rq) && rq.ValueKind == JsonValueKind.Array)
                foreach (var e in rq.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) rule.Requires.Add(e.GetString()!);

            if (re.TryGetProperty("criteria", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var ce in cs.EnumerateArray())
                {
                    var c = new Criterion
                    {
                        Score = ce.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number ? sc.GetDouble() : 0,
                        Verdict = Str(ce, "verdict"),
                        Text = Str(ce, "text")
                    };
                    c.MaxAbs = Num(ce, "max_abs", r);
                    c.Max = Num(ce, "max", r);
                    c.Min = Num(ce, "min", r);
                    rule.Criteria.Add(c);
                }
            return rule;
        }

        /// <summary>
        /// 读一个阈值。它可以是数字，也可以是 "@容差键" —— 后者必须**就地解引用**。
        /// 若把它当成 null 放过，该 criterion 的三个阈值会全为空而被误认为兜底 else，
        /// 于是第一条档位永远命中：静默失效，比崩溃更坏。
        /// </summary>
        private static double? Num(JsonElement e, string prop, Rubric r)
        {
            if (!e.TryGetProperty(prop, out var v)) return null;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number: return v.GetDouble();
                case JsonValueKind.String:
                    var s = v.GetString()!.Trim();
                    if (s.StartsWith("@", StringComparison.Ordinal))
                    {
                        string key = s.Substring(1);
                        if (!r.Tol.TryGetValue(key, out var tv))
                            throw new KeyError("criteria 引用了不存在的容差键 @" + key);
                        return tv;
                    }
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
                default: return null;
            }
        }

        private static string Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        // ---------------------------------------------------------------- 自检

        private void Validate()
        {
            var seen = new HashSet<string>();
            foreach (var r in Rules)
            {
                if (string.IsNullOrEmpty(r.Id)) throw new InvalidDataException("rubric 存在空 id 的规则");
                if (!seen.Add(r.Id)) throw new InvalidDataException("重复 rule id: " + r.Id);
                if (r.Criteria.Count == 0) throw new InvalidDataException(r.Id + " 没有 criteria");
                if (!r.Criteria[r.Criteria.Count - 1].IsElse)
                    throw new InvalidDataException(r.Id + " 最后一条 criteria 不是兜底 else");
                if (string.IsNullOrEmpty(r.UnknownText))
                    throw new InvalidDataException(r.Id + " 缺 unknown 文案 —— 违反铁律：不许只写判通过的路径");
            }
            if (Math.Abs(Rules.Sum(x => x.Weight) - 100) > 1e-6)
                throw new InvalidDataException("规则权重合计 " + Rules.Sum(x => x.Weight) + " != 100");
        }

        // ---------------------------------------------------------------- 求值与渲染

        /// <summary>按声明序找第一条命中的档位。</summary>
        public static Criterion Pick(RubricRule rule, double value)
        {
            foreach (var c in rule.Criteria)
                if (c.Matches(value)) return c;
            return rule.Criteria[rule.Criteria.Count - 1];
        }

        /// <summary>
        /// 渲染判据文案。{name} 由 measured 填充，支持 :.0f / :+.0f / :.2f 格式说明符。
        /// 取不到的占位符**原样保留** —— 静默吞掉等于把"我不知道"伪装成结论。
        /// </summary>
        public static string Render(string template, IDictionary<string, object> measured)
        {
            if (string.IsNullOrEmpty(template)) return "";
            var sb = new StringBuilder();
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c != '{') { sb.Append(c); i++; continue; }
                int end = template.IndexOf('}', i + 1);
                if (end < 0) { sb.Append(c); i++; continue; }
                string body = template.Substring(i + 1, end - i - 1);
                string key = body, fmt = null;
                int colon = body.IndexOf(':');
                if (colon >= 0) { key = body.Substring(0, colon); fmt = body.Substring(colon + 1); }
                if (measured != null && measured.TryGetValue(key, out var val) && val != null)
                    sb.Append(Format(val, fmt));
                else
                    sb.Append('{').Append(body).Append('}');      // 原样保留，不吞
                i = end + 1;
            }
            return sb.ToString();
        }

        private static string Format(object val, string fmt)
        {
            if (val is double d) return FormatDouble(d, fmt);
            if (val is int i) return FormatDouble(i, fmt);
            if (val is long l) return FormatDouble(l, fmt);
            if (val is bool b) return b ? "是" : "否";
            return val.ToString() ?? "";
        }

        /// <summary>
        /// 按 **Python f-string 方言** 格式化：[+][.N]f 或 d。
        ///
        /// 为什么自己实现而不是交给 .NET：rubric.json 是两把尺子共读的，里面的
        /// {diff:+.0f} 在 Python 侧是原生语义；.NET 的自定义格式串不认 'f' 后缀，
        /// 会把 "256" 渲染成 "256.0f"、把 "+.0f" 整段当字面量吐出来（实测踩过）。
        /// 契约迁就任何一方都会让另一方失真，所以规定方言、由 C# 翻译。
        /// </summary>
        internal static string FormatDouble(double v, string fmt)
        {
            bool plus = false;
            int dec = -1;
            if (!string.IsNullOrWhiteSpace(fmt))
            {
                string f = fmt.Trim();
                if (f.StartsWith("+", StringComparison.Ordinal)) { plus = true; f = f.Substring(1); }
                if (f.StartsWith(".", StringComparison.Ordinal))
                {
                    int k = 1;
                    while (k < f.Length && char.IsDigit(f[k])) k++;
                    if (k > 1 && int.TryParse(f.Substring(1, k - 1), out int n)) dec = n;
                }
                else if (f.EndsWith("d", StringComparison.Ordinal)) dec = 0;
                else if (f.EndsWith("f", StringComparison.Ordinal))
                {
                    string head = f.Substring(0, f.Length - 1);
                    if (head.Length > 0 && int.TryParse(head, out int n)) dec = n;
                }
            }

            if (dec < 0)
            {
                string bare = v.ToString("0.##", CultureInfo.InvariantCulture);
                return plus && v >= 0 ? "+" + bare : bare;
            }
            dec = Math.Min(dec, 15);
            double r = Math.Round(v, dec, MidpointRounding.AwayFromZero);
            if (r == 0) r = 0;                       // 抹掉 -0
            string body = r.ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (plus && r >= 0) body = "+" + body;
            return body;
        }

        public override string ToString()
            => "rubric v" + Version + " · " + Rules.Count + " 条规则 · 权重合计 " +
               Rules.Sum(r => r.Weight).ToString(CultureInfo.InvariantCulture) + " · 源 " + SourcePath;
    }
}
