using System;
using static System.Console;
using System.IO;
using System.Linq;
using System.Text;
using SvgCheck.Core.Parsing;
using SvgCheck.Core.Report;
using SvgCheck.Core.Rules;

namespace SvgCheck.Tests
{
    internal static class Program
    {
        private static int Main(string[] argv)
        {
            try { System.Console.OutputEncoding = Encoding.UTF8; } catch { /* 重定向到文件时可能失败，忽略 */ }
            if (argv.Length == 0)
            {
                WriteLine("用法: SvgCheck.Tests --dump <文件>   （其余模式待补）");
                return 2;
            }
            switch (argv[0])
            {
                case "--check":
                    return SelfCheck.Run();
                case "--parity":
                    return Parity.Run(argv.Skip(1).ToArray());
                case "--dump":
                    return Dump(argv.Skip(1).ToArray());
                case "--score":
                    return Score(argv.Skip(1).ToArray(), json: false);
                case "--json":
                    return Score(argv.Skip(1).ToArray(), json: true);
                default:
                    WriteLine("未知模式: " + argv[0]);
                    return 2;
            }
        }

        /// <summary>加载一份文档 → 自动推定角色 → 出报告。UI 还没建，这是唯一的验证入口。</summary>
        private static int Score(string[] rest, bool json)
        {
            if (rest.Length == 0) { WriteLine("需要一个文件"); return 2; }
            string path = Path.GetFullPath(rest[0]);
            var rubPath = Rubric.Locate(path);
            if (rubPath == null) { WriteLine("找不到 rubric.json"); return 2; }
            var rub = Rubric.Load(rubPath);
            var doc = SvgDocument.LoadFile(path);
            var f = new Facts(doc);
            var roles = new Roles();
            roles.AutoInfer(f);
            var sc = Analyzer.Run(doc, f, rub, roles);

            if (json) WriteLine(JsonReport.Build(doc, rub, roles, sc, "SvgCheck.Core"));
            else WriteLine(TextReport.Build(doc, rub, roles, sc));
            return 0;
        }

        private static int Dump(string[] rest)
        {
            if (rest.Length == 0) { WriteLine("--dump 需要一个文件"); return 2; }
            string path = Path.GetFullPath(rest[0]);
            var doc = SvgDocument.LoadFile(path);
            var f = new Facts(doc);

            WriteLine("■ " + Path.GetFileName(path) + "   " + doc.Bytes + " bytes");
            WriteLine("  降级: " + (doc.Degraded ? "是 — " + doc.DegradedReason : "否"));
            WriteLine("  viewBox: " + (doc.ViewBox.HasValue
                ? $"[{doc.ViewBox.Value.MinX}, {doc.ViewBox.Value.MinY}, {doc.ViewBox.Value.Width}, {doc.ViewBox.Value.Height}]"
                : "null"));
            WriteLine("  元素: " + string.Join(" · ", new[] { "path", "circle", "ellipse", "line", "rect", "polygon", "polyline" }
                .Select(t => t + " " + doc.CountOf(t))));
            WriteLine("  渐变 " + doc.GradientCount + " · @keyframes " + doc.Keyframes.Count + " · animation " + doc.AnimDecls);
            if (doc.Unsupported.Count > 0)
                WriteLine("  不支持: " + string.Join(" | ", doc.Unsupported.Distinct()));

            WriteLine();
            WriteLine("  圆 " + f.Circles.Count + " 个（按半径降序）:");
            foreach (var c in f.Circles.OrderByDescending(c => c.R))
                WriteLine($"    ({c.Cx},{c.Cy}) r={c.R}  class='{c.Class.Trim()}'");

            var (rear, front) = f.GuessWheels();
            if (rear != null)
                WriteLine("  推定轮: 后=(" + rear.Cx + "," + rear.Cy + ") r=" + rear.R +
                          "  前=(" + front.Cx + "," + front.Cy + ") r=" + front.R +
                          "  轮底 y=" + f.WheelBottom(new[] { rear, front}));

            WriteLine();
            WriteLine("  水平线 " + f.Horizontals.Count + " 条:");
            foreach (var h in f.Horizontals)
                WriteLine($"    y={h.Y}  x∈[{h.X0}, {h.X1}]  len={h.Len}  ({h.How})");
            var g = f.GuessGround();
            if (g != null) WriteLine("  推定地面: y=" + g.Y + " (" + g.How + ")");

            if (rear != null)
            {
                var wheels = new[] { rear, front };
                var hits = f.LinesThroughWheels(wheels);
                WriteLine();
                WriteLine("  穿轮命中 " + hits.Count + " 处:");
                foreach (var (hh, ww) in hits)
                    WriteLine($"    线 y={hh.Y} x∈[{hh.X0},{hh.X1}] × 轮 ({ww.Cx},{ww.Cy}) r={ww.R}");
                WriteLine("  辐条: 后轮 " + f.CountSpokes(rear) + "  前轮 " + f.CountSpokes(front) +
                          "  合计 " + (f.CountSpokes(rear) + f.CountSpokes(front)));
            }

            var ws = f.GuessWheels();
            var fr = f.CountFrameTriangles(ws.rear == null ? null : new[] { ws.rear, ws.front }, 8);
            WriteLine();
            WriteLine("  直线段 " + f.Segments.Count + " 条 · 三角 全图 " + fr.total + " / 车架 " + fr.frame +
                      " · 不同填充色 " + f.DistinctFills());
            var sb = doc.AllBounds();
            var subjOnly = doc.SubjectBounds(0.95);
            WriteLine("  全部元素包围盒: " + (sb.IsValid
                ? $"x∈[{sb.MinX}, {sb.MaxX}] y∈[{sb.MinY}, {sb.MaxY}]" : "无效"));
            WriteLine("  主体（剔背景后）: " + (subjOnly.IsValid
                ? $"x∈[{subjOnly.MinX}, {subjOnly.MaxX}] y∈[{subjOnly.MinY}, {subjOnly.MaxY}]" : "无效"));
            return 0;
        }
    }
}
