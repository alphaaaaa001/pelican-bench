using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using SvgCheck.Core.Parsing;
using SvgCheck.Core.Report;
using SvgCheck.Core.Rules;

namespace SvgCheck.Tests
{
    /// <summary>
    /// 双尺对拍。这是"Python 与 C# 并存"能成立的前提 ——
    /// 没有它，两把尺子会在几周内漂成给出不同分数的两个东西，那比没尺子更糟。
    ///
    /// 比对口径（照 rubric 的 _report.对拍口径）：
    ///  - 严格比：rules[].id / verdict / score，summary.score / coverage / judged / unknown
    ///  - 只展示不比：measured 的键与数值精度、text 的措辞
    ///    —— 两侧几何实现本就不同（C# 认 transform 与贝塞尔精确包围盒，Python 不认），
    ///       比数值只会制造噪声，比结论才有意义。
    /// </summary>
    internal static class Parity
    {
        private const string Py = "svgcheck.py";

        public static int Run(string[] rest)
        {
            var repoRoot = FindRoot();
            if (repoRoot == null) { Console.WriteLine("找不到仓库根（rubric.json 所在处）"); return 2; }
            var rubricPath = Path.Combine(repoRoot, "rubric.json");
            if (!File.Exists(rubricPath)) { Console.WriteLine("缺 rubric.json"); return 2; }

            var py = Path.Combine(repoRoot, Py);
            if (!File.Exists(py)) { Console.WriteLine("缺 " + Py + "，无法对拍"); return 2; }

            var pyExe = FindPython();
            if (pyExe == null)
            {
                Console.WriteLine("找不到 Python 3.8+，无法对拍。试过：python3 / python / 真实 Launcher / 常见安装根");
                return 2;
            }
            string pyExePath = pyExe.Value.Item1, pySwitch = pyExe.Value.Item2, pyVer = pyExe.Value.Item3;
            Console.WriteLine("  Python: " + pyExePath + " " + pyVer +
                              (string.IsNullOrEmpty(pySwitch) ? "" : "  (switch " + pySwitch + ")"));

            var samples = rest.Length > 0
                ? rest.Select(Path.GetFullPath).ToList()
                : DefaultSamples(repoRoot);

            if (samples.Count == 0) { Console.WriteLine("没有可对拍的样本"); return 2; }

            int bad = 0;
            foreach (var s in samples)
            {
                Console.WriteLine();
                Console.WriteLine("  ■ " + Path.GetFileName(s));
                if (!File.Exists(s)) { Console.WriteLine("    ✗ 文件不存在"); bad++; continue; }

                string pyJson;
                try { pyJson = RunPy(pyExePath, pySwitch, py, s, repoRoot); }
                catch (Exception e) { Console.WriteLine("    ✗ Python 侧跑挂：" + e.Message); bad++; continue; }

                string csJson;
                try { csJson = RunCs(s, rubricPath); }
                catch (Exception e) { Console.WriteLine("    ✗ C# 侧跑挂：" + e.Message); bad++; continue; }

                JsonDocument a, b;
                try { a = JsonDocument.Parse(pyJson); }
                catch (Exception e) { Console.WriteLine("    ✗ Python 输出不是合法 JSON：" + e.Message); bad++; continue; }
                try { b = JsonDocument.Parse(csJson); }
                catch (Exception e) { Console.WriteLine("    ✗ C# 输出不是合法 JSON：" + e.Message); bad++; continue; }

                var av = a.RootElement; var bv = b.RootElement;
                int ver = av.GetProperty("rubric_version").GetInt32();
                int bver = bv.GetProperty("rubric_version").GetInt32();
                if (ver != bver)
                {
                    Console.WriteLine($"    ✗ rubric_version 不一致：py={ver} cs={bver} —— 拒绝对拍");
                    bad++;
                    continue;
                }

                var pa = Index(av); var pb = Index(bv);
                var ids = pa.Keys.Union(pb.Keys).ToList();
                int diffs = 0;
                foreach (var id in ids)
                {
                    if (!pa.TryGetValue(id, out var x)) { Console.WriteLine("    ✗ " + id + "：Python 侧没有这条规则"); diffs++; continue; }
                    if (!pb.TryGetValue(id, out var y)) { Console.WriteLine("    ✗ " + id + "：C# 侧没有这条规则"); diffs++; continue; }

                    string vx = Verdict(x), vy = Verdict(y);
                    string sx = Score(x), sy = Score(y);
                    if (vx == vy && sx == sy)
                    {
                        Console.WriteLine("      ✓ " + Pad(id) + "  " + vx + "  " + sx);
                        continue;
                    }
                    Console.WriteLine("      ✗ " + Pad(id) + "  py=" + vx + "/" + sx + "   cs=" + vy + "/" + sy);
                    Console.WriteLine("           py 文案：" + Trim(Txt(x)));
                    Console.WriteLine("           cs 文案：" + Trim(Txt(y)));
                    diffs++;
                }

                foreach (var key in new[] { "score", "coverage", "judged", "unknown" })
                {
                    string m = SumStr(av, key), n = SumStr(bv, key);
                    if (m == n) continue;
                    Console.WriteLine("      ✗ summary." + key + "：py=" + m + "  cs=" + n);
                    diffs++;
                }

                if (diffs == 0) Console.WriteLine("    ⇒ 一致（" + ids.Count + " 条规则 + 4 项 summary）");
                else { Console.WriteLine("    ⇒ " + diffs + " 处不一致"); bad++; }
            }

            Console.WriteLine();
            Console.WriteLine(bad == 0
                ? "  ⇒ 对拍通过：两把尺子对所有样本给出相同结论"
                : "  ⇒ 对拍失败：" + bad + " 个样本结论漂移");
            return bad == 0 ? 0 : 1;
        }

        // ---------------------------------------------------------------- 两侧驱动

        private static string RunCs(string sample, string rubricPath)
        {
            var rub = Rubric.Load(rubricPath);
            var doc = SvgDocument.LoadFile(sample);
            var f = new Facts(doc);
            var roles = new Roles();
            roles.AutoInfer(f);
            var sc = Analyzer.Run(doc, f, rub, roles);
            return JsonReport.Build(doc, rub, roles, sc, "SvgCheck.Core");
        }

        private static string RunPy(string exe, string sw, string script, string sample, string cwd)
        {
            var psi = new ProcessStartInfo(exe,
                (string.IsNullOrEmpty(sw) ? "" : sw + " ") + Q(script) + " --json " + Q(sample))
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            using var p = Process.Start(psi);
            string so = p.StandardOutput.ReadToEnd();
            string se = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(60_000)) { p.Kill(true); throw new TimeoutException("Python 60s 未退出"); }
            if (p.ExitCode != 0) throw new Exception("exit=" + p.ExitCode + " " + Trim(se));
            return so;
        }

        private static string Q(string s) => "\"" + s + "\"";

        // ---------------------------------------------------------------- JSON 取值

        private static Dictionary<string, JsonElement> Index(JsonElement root)
        {
            var d = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (!root.TryGetProperty("rules", out var rules)) return d;
            foreach (var r in rules.EnumerateArray())
            {
                var id = r.TryGetProperty("id", out var i) ? i.GetString() : null;
                if (!string.IsNullOrEmpty(id)) d[id] = r;
            }
            return d;
        }

        private static string Verdict(JsonElement e)
            => e.TryGetProperty("verdict", out var v) ? v.GetString() : "(缺)";

        private static string Score(JsonElement e)
        {
            if (!e.TryGetProperty("score", out var s)) return "(缺)";
            if (s.ValueKind == JsonValueKind.Null) return "null";
            return s.GetDouble().ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Txt(JsonElement e)
            => e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

        private static string SumStr(JsonElement root, string key)
        {
            if (!root.TryGetProperty("summary", out var sm)) return "(缺)";
            if (!sm.TryGetProperty(key, out var v)) return "(缺)";
            if (v.ValueKind == JsonValueKind.Null) return "null";
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble().ToString("0.####", CultureInfo.InvariantCulture);
            return v.ToString();
        }

        // ---------------------------------------------------------------- 设施

        private static List<string> DefaultSamples(string root)
        {
            var dir = Path.Combine(root, "fixtures");
            var list = new List<string>();
            if (Directory.Exists(dir))
                list.AddRange(Directory.EnumerateFiles(dir)
                    .Where(f => f.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            return list;
        }

        private static string FindRoot()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "rubric.json"))) return dir;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }

        /// <summary>
        /// 探测可用的 Python 3.8+，返回 (解释器, 版本 switch, 版本号)。
        ///
        /// **switch 必须一路带到执行处**：真 Launcher 靠 -3.12 挑解释器，漏掉它就会退到默认
        /// 解释器（这台机上是 3.4，直接 SyntaxError）。我在这儿栽过一次 —— 探到 3.12、
        /// 跑起来却 9009。
        ///
        /// 次序与 bin/svgcheck.cmd 保持一致：先验 PATH 上的候选，再扫常见安装根。另有两个坑：
        ///  - 不用 `python -c "..."`：内嵌代码要在 Arguments 里带引号，而 .NET 的参数解析规则和
        ///    cmd.exe 不同，引号会碎 —— 实测这样一条都探不到。改用 `-V`，输出是 "Python 3.12.0a2"。
        ///  - 不假设 `py` 是 Python Launcher：这台机上的 py.exe 是个老解释器，`py -0p` 直接报
        ///    "Unknown option: -0"。所以 launcher 只是众多候选之一，探不到就往下走。
        /// </summary>
        private static (string, string, string)? FindPython()
        {
            var tried = new List<string>();

            foreach (var cand in new[] { "python3", "python" })
                if (Probe(cand, "", tried) is string v) return (cand, "", v);

            // 真实 Launcher 的安装位置（PATH 上的 py 可能是不认 -3.12 的 2015 stub）
            foreach (var dir in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs\Python\Launcher\py.exe",
                         Environment.GetEnvironmentVariable("ProgramFiles") + @"\Python\Launcher\py.exe",
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)") + @"\Python\Launcher\py.exe"
                     })
            {
                if (string.IsNullOrEmpty(dir) || !File.Exists(dir)) continue;
                foreach (var sw in new[] { "-3.12", "-3" })
                    if (Probe(dir, sw, tried) is string v) return (dir, sw, v);
            }

            // 扫常见安装根
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs\Python\Python3*",
                Environment.GetEnvironmentVariable("ProgramFiles") + @"\Python3*",
                Environment.GetEnvironmentVariable("ProgramFiles") + @"\Python\Python3*",
                Environment.GetEnvironmentVariable("ProgramFiles(x86)") + @"\Python3*",
                Environment.GetEnvironmentVariable("SYSTEMDRIVE") + @"\Python3*"
            };
            foreach (var r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                var parent = Path.GetDirectoryName(r);
                var leaf = Path.GetFileName(r);
                if (parent == null || !Directory.Exists(parent)) continue;
                foreach (var d in Directory.GetDirectories(parent, leaf).OrderBy(d => d))
                {
                    var exe = Path.Combine(d, "python.exe");
                    if (!File.Exists(exe)) continue;
                    if (Probe(exe, "", tried) is string v) return (exe, "", v);
                }
            }
            return null;
        }

        /// <summary>验一个候选。够新则返回版本串，否则返回 null 并把"见到的是什么"记进 tried。</summary>
        private static string Probe(string exe, string sw, List<string> tried)
        {
            var o = Captured(exe, (sw.Length > 0 ? sw + " " : "") + "-V");
            var v = ParseVer(o);
            if (v != null && NewerOrEqual(v, 3, 8)) return v;
            tried.Add(exe + (sw.Length > 0 ? " " + sw : "") + "=" + (v ?? "命令不存在"));
            return null;
        }

        /// <summary>跑一条命令并抓输出（-V 的版本信息 Python 2 走 stderr、3 走 stdout，两边都读）。</summary>
        private static string Captured(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(15_000)) { p.Kill(true); return null; }
                var combined = (so + "\n" + se).Trim();
                return combined.Length == 0 ? null : combined;
            }
            catch { return null; }
        }

        private static string ParseVer(string output)
        {
            var m = Regex.Match(output ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            return m.Success ? m.Groups[1].Value + "." + m.Groups[2].Value : null;
        }

        private static bool NewerOrEqual(string pv, int maj, int min)
            => Version.TryParse(pv, out var v) && v >= new Version(maj, min);

        private static string Pad(string id)
        {
            id = id ?? "";
            return id.Length >= 22 ? id : id + new string(' ', 22 - id.Length);
        }

        private static string Trim(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 110 ? s : s.Substring(0, 107) + "…";
        }
    }
}
