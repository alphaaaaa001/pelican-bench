using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SvgCheck.Core.Parsing;
using SvgCheck.Core.Rules;

namespace SvgCheck.Core.Report
{
    /// <summary>
    /// 契约 JSON。这是双尺对拍的输入 —— 字段与顺序严格照 rubric 的 _report.schema。
    /// 对拍只严格比对 rules[].id / verdict / score 与 summary；measured 与 text 仅展示，
    /// 因为两侧几何实现不同（C# 认 transform，Python 不认），比数值只会制造噪声。
    /// </summary>
    public static class JsonReport
    {
        public static string Build(SvgDocument doc, Rubric rub, Roles roles, Scorecard sc, string tool)
        {
            var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("tool", tool);
                w.WriteNumber("rubric_version", rub.Version);
                w.WriteString("file", string.IsNullOrEmpty(doc.SourcePath)
                    ? "" : Path.GetFileName(doc.SourcePath));
                w.WriteNumber("bytes", doc.Bytes);

                if (doc.ViewBox.HasValue)
                {
                    var vb = doc.ViewBox.Value;
                    w.WriteStartArray("viewbox");
                    w.WriteNumberValue(vb.MinX); w.WriteNumberValue(vb.MinY);
                    w.WriteNumberValue(vb.Width); w.WriteNumberValue(vb.Height);
                    w.WriteEndArray();
                }
                else w.WriteNull("viewbox");

                w.WriteBoolean("degraded", doc.Degraded);
                if (doc.Degraded) w.WriteString("degraded_reason", doc.DegradedReason ?? "");

                // roles：一律数组（脚有两只、踏板有两块）
                w.WriteStartObject("roles");
                foreach (var grp in roles.Bound.GroupBy(b => b.RoleId, StringComparer.OrdinalIgnoreCase))
                {
                    w.WriteStartArray(grp.Key);
                    foreach (var b in grp)
                    {
                        w.WriteStartObject();
                        w.WriteString("kind", b.Circle != null ? "circle" : b.Line != null ? "line" : "point");
                        w.WriteBoolean("auto", b.Auto);
                        w.WriteNumber("el", b.El?.Order ?? -1);
                        if (b.Circle != null)
                        {
                            w.WriteNumber("cx", b.Circle.Cx);
                            w.WriteNumber("cy", b.Circle.Cy);
                            w.WriteNumber("r", b.Circle.R);
                        }
                        else if (b.Line != null)
                        {
                            w.WriteNumber("y", b.Line.Y);
                            w.WriteNumber("x0", b.Line.X0);
                            w.WriteNumber("x1", b.Line.X1);
                        }
                        else
                        {
                            w.WriteNumber("x", b.Point.X);
                            w.WriteNumber("y", b.Point.Y);
                        }
                        if (b.Bounds.IsValid)
                        {
                            w.WriteStartArray("bbox");
                            w.WriteNumberValue(b.Bounds.MinX); w.WriteNumberValue(b.Bounds.MinY);
                            w.WriteNumberValue(b.Bounds.MaxX); w.WriteNumberValue(b.Bounds.MaxY);
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();

                // info：不计分，仅陈述
                w.WriteStartObject("info");
                w.WriteStartObject("animation");
                w.WriteStartArray("keyframes");
                foreach (var k in doc.Keyframes) w.WriteStringValue(k);
                w.WriteEndArray();
                w.WriteNumber("decls", doc.AnimDecls);
                w.WriteEndObject();
                w.WriteStartObject("counts");
                foreach (var t in new[] { "path", "circle", "ellipse", "line", "rect", "polygon", "polyline" })
                    w.WriteNumber(t, doc.CountOf(t));
                w.WriteNumber("gradient", doc.GradientCount);
                w.WriteEndObject();
                if (doc.Unsupported.Count > 0)
                {
                    w.WriteStartArray("unsupported");
                    foreach (var u in doc.Unsupported.Distinct()) w.WriteStringValue(u);
                    w.WriteEndArray();
                }
                w.WriteEndObject();

                w.WriteStartArray("rules");
                foreach (var o in sc.Outcomes)          // 声明序，对拍要求
                {
                    w.WriteStartObject();
                    w.WriteString("id", o.Id);
                    w.WriteString("group", o.Group);
                    w.WriteNumber("weight", o.Weight);
                    w.WriteString("verdict", o.Verdict);
                    if (o.Score.HasValue) w.WriteNumber("score", o.Score.Value); else w.WriteNull("score");
                    w.WriteStartObject("measured");
                    foreach (var kv in o.Measured) WriteScalar(w, kv.Key, kv.Value);
                    w.WriteEndObject();
                    w.WriteString("text", o.Text);
                    if (o.Unimplemented) w.WriteBoolean("unimplemented", true);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartObject("summary");
                if (sc.Score.HasValue) w.WriteNumber("score", sc.Score.Value); else w.WriteNull("score");
                w.WriteNumber("coverage", sc.Coverage);
                w.WriteNumber("judged", sc.Judged);
                w.WriteNumber("unknown", sc.UnknownCount);
                w.WriteBoolean("low_coverage", sc.LowCoverage);
                w.WriteEndObject();

                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        private static void WriteScalar(Utf8JsonWriter w, string key, object v)
        {
            switch (v)
            {
                case null: w.WriteNull(key); break;
                case double d: w.WriteNumber(key, d); break;
                case int i: w.WriteNumber(key, i); break;
                case bool b: w.WriteBoolean(key, b); break;
                default: w.WriteString(key, Convert.ToString(v, CultureInfo.InvariantCulture)); break;
            }
        }
    }

    /// <summary>人读的体检单。文案来自 rubric，这里只负责排版。</summary>
    public static class TextReport
    {
        public static string Build(SvgDocument doc, Rubric rub, Roles roles, Scorecard sc)
        {
            var L = new List<string>();
            Action<string> A = s => L.Add(s);

            A("■ " + (string.IsNullOrEmpty(doc.SourcePath) ? "(未命名)" : Path.GetFileName(doc.SourcePath)) +
              "   " + doc.Bytes + " bytes");
            A("  画布 viewBox = " + (doc.ViewBox.HasValue
                ? "[" + doc.ViewBox.Value.MinX + ", " + doc.ViewBox.Value.MinY + ", " +
                  doc.ViewBox.Value.Width + ", " + doc.ViewBox.Value.Height + "]"
                : "null"));
            A("  元素：path " + doc.CountOf("path") + " · circle " + doc.CountOf("circle") +
              " · ellipse " + doc.CountOf("ellipse") + " · line " + doc.CountOf("line") +
              " · rect " + doc.CountOf("rect"));
            A("  渐变 " + doc.GradientCount + " 个 · @keyframes " + doc.Keyframes.Count + " 个 [" +
              string.Join(", ", doc.Keyframes) + "] · animation 声明 " + doc.AnimDecls + " 处");
            if (doc.Degraded) A("  ⚠ 降级：" + doc.DegradedReason);
            if (doc.Unsupported.Count > 0)
                A("  ⚠ 未支持：" + string.Join(" | ", doc.Unsupported.Distinct()));

            A("");
            A("  已认定角色：" + (roles.Bound.Any()
                ? string.Join(", ", roles.Bound.GroupBy(b => b.RoleId).Select(g =>
                    g.Key + (g.Any(b => b.Auto) ? "(推定)" : "(指派)")))
                : "无"));

            foreach (var g in rub.Groups)
            {
                A("");
                A("  [" + g.Name + " —— " + sc.GroupScore(g.Id).ToString("0.#", CultureInfo.InvariantCulture) +
                  "/" + g.Weight.ToString("0.#", CultureInfo.InvariantCulture) + "]");
                foreach (var o in sc.InGroup(g.Id))
                {
                    A("  " + Mark(o.Verdict) + " " + o.Name + "  " +
                      o.Weight.ToString("0.#", CultureInfo.InvariantCulture) + " 分  " +
                      (o.Score.HasValue
                          ? (o.Score.Value * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                          : "未判定"));
                    A("     " + o.Text);
                }
            }

            A("");
            A("  " + new string('─', 46));
            A("  总分 " + (sc.Score.HasValue ? sc.Score.Value.ToString(CultureInfo.InvariantCulture) : "—") +
              " / 100    覆盖率 " +
              (sc.Coverage * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%（" +
              sc.Judged + " 项已判，" + sc.UnknownCount + " 项无判据）");
            if (sc.LowCoverage)
                A("  ⚠ 覆盖率低于 " + (rub.LowCoverage * 100).ToString("0", CultureInfo.InvariantCulture) +
                  "% —— 判据不足，此分数参考性有限（验不了 ≠ 通过）");
            if (sc.Unimplemented.Count > 0)
                A("  ❌ 尺子缺刻度：" + string.Join(", ", sc.Unimplemented.Select(o => o.Id)) +
                  "  在 rubric 里声明但没有实现");

            if (rub.ManualOnly.Count > 0)
            {
                A("");
                A("  [人工核对 —— 机器不替你做判断的]");
                foreach (var m in rub.ManualOnly) A("    □ " + m);
            }
            return string.Join(Environment.NewLine, L);
        }

        private static string Mark(string verdict) => verdict switch
        {
            "pass" => "✅",
            "warn" => "⚠️",
            "fail" => "❌",
            _ => "⏭ "
        };
    }
}
