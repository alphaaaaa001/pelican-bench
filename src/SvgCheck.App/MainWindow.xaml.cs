using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SvgCheck.Core.Parsing;
using SvgCheck.Core.Report;
using SvgCheck.Core.Rules;
using SvgCheck.App.Rendering;

namespace SvgCheck.App
{
    public partial class MainWindow : Window
    {
        private Rubric _rub;
        private SvgDocument _doc;
        private Facts _facts;
        private readonly Roles _roles = new Roles();
        private Scorecard _sc;
        private string _path;

        public MainWindow()
        {
            InitializeComponent();
            View.Picked += OnPicked;

            try
            {
                _rub = Rubric.LoadOrThrow(App.PendingFile ?? Directory.GetCurrentDirectory());
            }
            catch (Exception e)
            {
                _rub = null;
                ShowBanner("判据加载失败：" + e.Message +
                           "\n没有 rubric.json 这把尺子就拒绝出数 —— 内置默认值迟早会和另一把尺子漂移。", true);
            }

            Loaded += (s, a) =>
            {
                if (!string.IsNullOrEmpty(App.PendingFile))
                {
                    var f = App.PendingFile; App.PendingFile = null;
                    LoadFile(f);
                }
            };
        }

        // ---------------------------------------------------------------- 载入

        private void OnOpen(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要体检的产出",
                Filter = "SVG / HTML|*.svg;*.htm;*.html|所有文件|*.*"
            };
            if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0) return;
            LoadFile(files[0]);
        }

        private void LoadFile(string path)
        {
            if (_rub == null) return;
            try
            {
                _path = Path.GetFullPath(path);
                _doc = SvgDocument.LoadFile(_path);
                _facts = new Facts(_doc);
                _roles.Reset();
                _roles.AutoInfer(_facts);
                EmptyHint.Visibility = Visibility.Collapsed;
                foreach (var b in new[] { BtnCopy, BtnSave, BtnReset, BtnRoles }) b.IsEnabled = true;
                FileName.Text = _doc.Bytes + " bytes · " + Path.GetFileName(_path);
                Status.Text = "判据 " + _rub.SourcePath;
                View.Document = _doc;
                Recompute();
            }
            catch (Exception ex)
            {
                ShowBanner("这份文件读不下来：" + ex.GetType().Name + " — " + ex.Message, true);
            }
        }

        // ---------------------------------------------------------------- 重算

        private void Recompute()
        {
            if (_doc == null || _rub == null) return;
            _sc = Analyzer.Run(_doc, _facts, _rub, _roles);
            RenderSummary();
            RenderScoreCard();
            RenderOverlays();
        }

        private void RenderSummary()
        {
            if (_sc.Score.HasValue)
            {
                ScoreText.Text = _sc.Score.Value.ToString(CultureInfo.InvariantCulture);
                ScoreBadge.Background = BrushFor(_sc.Score.Value >= 85 ? "pass" : _sc.Score.Value >= 60 ? "warn" : "fail");
            }
            else
            {
                ScoreText.Text = "—";
                ScoreBadge.Background = Frozen("#FF3A3F48");
            }
            CoverageText.Text = (_sc.Coverage * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%  ·  " +
                                _sc.Judged + "/" + _sc.Outcomes.Count + " 项";

            if (_doc.Degraded)
                ShowBanner("⚠ " + _doc.DegradedReason, true);
            else if (_sc.LowCoverage)
                ShowBanner("覆盖率只有 " + (_sc.Coverage * 100).ToString("0", CultureInfo.InvariantCulture) +
                           "% —— 这么多判据够不着，总分的参考性有限。右键点图中的元素指定角色可以把它们变可判。", false);
            else if (_sc.Unimplemented.Count > 0)
                ShowBanner("尺子缺刻度：rubric 声明了但没实现 —— " +
                           string.Join(", ", _sc.Unimplemented.Select(o => o.Id)), true);
            else ShowBanner(null, false);
        }

        private void RenderOverlays()
        {
            var tags = new Dictionary<int, string>();
            foreach (var b in _roles.Bound)
            {
                if (b.El == null) continue;
                var spec = _rub.RoleSpecs.FirstOrDefault(s => s.Id == b.RoleId);
                tags[b.El.Order] = (spec?.Name ?? b.RoleId) + (b.Auto ? "（推定）" : "");
            }
            View.SetRoleTags(tags);
        }

        // ---------------------------------------------------------------- 判据面板

        private void RenderScoreCard()
        {
            ScorePanel.Children.Clear();

            ScorePanel.Children.Add(Header("体检单", 17, true));
            ScorePanel.Children.Add(Header(_path == null ? "" : Path.GetFileName(_path), 12, false, Frozen("#FF5B6470")));

            if (_doc.Unsupported.Count > 0)
            {
                var u = Header("未支持 " + _doc.Unsupported.Distinct().Count() + " 项", 12, false, Frozen("#FFB26B00"));
                u.Margin = new Thickness(0, 8, 0, 0);
                ScorePanel.Children.Add(u);
                foreach (var s in _doc.Unsupported.Distinct().Take(6))
                    ScorePanel.Children.Add(Hint(s));
            }

            foreach (var g in _rub.Groups)
            {
                var exp = new Expander
                {
                    Header = GroupHeader(g),
                    IsExpanded = true,
                    Margin = new Thickness(0, 10, 0, 0),
                    Background = Frozen("#FFFFFFFF"),
                    BorderBrush = Frozen("#FFDDE2E7"),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6)
                };
                var inner = new StackPanel();
                foreach (var o in _sc.InGroup(g.Id)) inner.Children.Add(RuleRow(o));
                exp.Content = inner;
                ScorePanel.Children.Add(exp);
            }

            if (_rub.ManualOnly.Count > 0)
            {
                var m = Header("人工核对 —— 机器不替你做判断的", 13, true);
                m.Margin = new Thickness(0, 14, 0, 4);
                ScorePanel.Children.Add(m);
                foreach (var s in _rub.ManualOnly) ScorePanel.Children.Add(Hint("□  " + s));
            }
        }

        private static StackPanel GroupHeader(RubricGroup g)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(new TextBlock
            {
                Text = g.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Frozen("#FF1C1F24"),
                VerticalAlignment = VerticalAlignment.Center
            });
            return p;
        }

        private FrameworkElement RuleRow(RuleOutcome o)
        {
            var rule = _rub.Rules.First(r => r.Id == o.Id);
            var exp = new Expander { Margin = new Thickness(0, 4, 0, 0) };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var mark = new TextBlock
            {
                Text = MarkFor(o.Verdict),
                FontSize = 13,
                Foreground = VerdictBrush(o.Verdict),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 7, 0)
            };
            Grid.SetColumn(mark, 0); head.Children.Add(mark);

            var name = new TextBlock
            {
                Text = o.Name,
                FontSize = 13.5,
                Foreground = Frozen("#FF1C1F24"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(name, 1); head.Children.Add(name);

            var wt = new TextBlock
            {
                Text = o.Weight.ToString("0.#", CultureInfo.InvariantCulture) + " 分",
                FontSize = 11.5,
                Foreground = Frozen("#FF7A828C"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            Grid.SetColumn(wt, 2); head.Children.Add(wt);

            var pct = new Border
            {
                Background = VerdictBrush(o.Verdict),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Child = new TextBlock
                {
                    Text = o.Score.HasValue
                        ? (o.Score.Value * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                        : "未判定",
                    Foreground = Frozen("#FFFFFFFF"),
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold
                }
            };
            Grid.SetColumn(pct, 3); head.Children.Add(pct);

            exp.Header = head;

            var body = new StackPanel { Margin = new Thickness(2, 6, 2, 2) };
            body.Children.Add(new TextBlock
            {
                Text = o.Text,
                FontSize = 12.5,
                Foreground = Frozen("#FF2B3038"),
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock
            {
                Text = "判据：" + rule.Measure,
                FontSize = 11.5,
                Foreground = Frozen("#FF5B6470"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
            body.Children.Add(new TextBlock
            {
                Text = "为什么量它：" + rule.Why,
                FontSize = 11.5,
                Foreground = Frozen("#FF5B6470"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });

            if (o.Measured.Count > 0)
            {
                var tbl = new Grid { Margin = new Thickness(0, 7, 0, 0) };
                tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
                tbl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int row = 0;
                foreach (var kv in o.Measured)
                {
                    tbl.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    var k = new TextBlock
                    {
                        Text = kv.Key,
                        FontSize = 11.5,
                        Foreground = Frozen("#FF7A828C"),
                        FontFamily = new FontFamily("Consolas, monospace")
                    };
                    Grid.SetRow(k, row); Grid.SetColumn(k, 0); tbl.Children.Add(k);
                    var v = new TextBlock
                    {
                        Text = FmtVal(kv.Value),
                        FontSize = 11.5,
                        Foreground = Frozen("#FF1C1F24"),
                        FontFamily = new FontFamily("Consolas, monospace"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(6, 0, 0, 2)
                    };
                    Grid.SetRow(v, row); Grid.SetColumn(v, 1); tbl.Children.Add(v);
                    row++;
                }
                body.Children.Add(tbl);
            }

            if (o.Highlight.Count > 0)
            {
                var btn = new Button
                {
                    Content = "在预览里高亮这 " + o.Highlight.Distinct().Count() + " 个元素",
                    FontSize = 11.5,
                    Margin = new Thickness(0, 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Tag = o
                };
                btn.Click += (s, a) =>
                {
                    View.SetHighlight(((RuleOutcome)((Button)s).Tag).Highlight);
                    Status.Text = "已高亮 " + ((RuleOutcome)((Button)s).Tag).Id + " 指认的元素";
                };
                body.Children.Add(btn);
            }

            exp.Content = body;
            return exp;
        }

        // ---------------------------------------------------------------- 角色指派

        private void OnPicked(Core.Parsing.SvgEl el, Point screen)
        {
            if (_rub == null || el == null) return;
            var menu = new ContextMenu { PlacementTarget = View };

            menu.Items.Add(Header("把「" + Describe(el) + "」设为", 12, false, Frozen("#FF5B6470")));

            var specs = _rub.RoleSpecs.Where(s => Compatible(s, el)).ToList();
            if (specs.Count == 0)
                menu.Items.Add(Hint("（这个元素的形状不适合任何角色）"));
            foreach (var s in specs)
            {
                var item = new MenuItem { Header = s.Name + "　" + s.Id, ToolTip = s.Hint };
                var cap = s;
                item.Click += (a, b) => Assign(cap, el);
                menu.Items.Add(item);
            }

            var current = _roles.Bound.Where(b => b.El != null && b.El.Order == el.Order).ToList();
            if (current.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var b in current)
                {
                    var spec = _rub.RoleSpecs.FirstOrDefault(s => s.Id == b.RoleId);
                    var item = new MenuItem { Header = "取消：" + (spec?.Name ?? b.RoleId) };
                    var bind = b;
                    item.Click += (a, e2) => Unassign(bind);
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new Separator());
            var auto = new MenuItem { Header = "清除全部自动推定" };
            auto.Click += (a, b) => { _roles.ClearAuto(); Recompute(); };
            menu.Items.Add(auto);

            menu.IsOpen = true;
        }

        private void Assign(RoleSpec s, Core.Parsing.SvgEl el)
        {
            var kind = s.Kind switch
            {
                "circle" => RoleKind.Circle,
                "line" => RoleKind.Line,
                "point" => RoleKind.Point,
                _ => RoleKind.Any
            };
            _roles.Assign(s.Id, el, _facts, kind, null, auto: false, replace: !s.Multi);
            Status.Text = "已把 " + Describe(el) + " 设为「" + s.Name + "」" + (s.Multi ? "（该角色可多选）" : "");
            Recompute();
        }

        private void Unassign(RoleBinding b)
        {
            var lst = _roles.AllOf(b.RoleId);
            if (lst.Count <= 1) _roles.Clear(b.RoleId);
            else
            {
                // 多值角色只摘掉这一个，其余保留
                var keep = lst.Where(x => !ReferenceEquals(x, b)).ToList();
                _roles.Clear(b.RoleId);
                foreach (var k in keep) _roles.Assign(b.RoleId, k.El, _facts, k.Kind, k.Point, k.Auto, replace: false);
            }
            Recompute();
        }

        private bool Compatible(RoleSpec s, Core.Parsing.SvgEl el)
        {
            switch (s.Kind)
            {
                case "circle": return string.Equals(el.Tag, "circle", StringComparison.OrdinalIgnoreCase);
                case "line": return _facts != null && _facts.Horizontals.Any(h => ReferenceEquals(h.El, el));
                default: return true;
            }
        }

        private static string Describe(Core.Parsing.SvgEl el)
        {
            var b = el.Bounds;
            string geo = b.IsValid
                ? "(" + b.Center.X.ToString("0", CultureInfo.InvariantCulture) + "," +
                  b.Center.Y.ToString("0", CultureInfo.InvariantCulture) + ")"
                : "无几何";
            return el.Tag + " " + geo;
        }

        // ---------------------------------------------------------------- 动作

        private void OnResetView(object sender, RoutedEventArgs e) => View.ResetView();

        private void OnClearRoles(object sender, RoutedEventArgs e)
        {
            _roles.Reset();
            _roles.AutoInfer(_facts);
            Status.Text = "角色已重置为自动推定";
            Recompute();
        }

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            if (_sc == null) return;
            try { Clipboard.SetText(TextReport.Build(_doc, _rub, _roles, _sc)); Status.Text = "报告已复制到剪贴板"; }
            catch (Exception ex) { Status.Text = "复制失败：" + ex.Message; }
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            if (_sc == null) return;
            var dlg = new SaveFileDialog
            {
                Title = "导出体检报告",
                Filter = "Markdown|*.md|JSON（契约）|*.json|纯文本|*.txt",
                FileName = Path.GetFileNameWithoutExtension(_path ?? "report") + "-体检"
            };
            if (dlg.ShowDialog(this) != true) return;
            string body = Path.GetExtension(dlg.FileName).ToLowerInvariant() == ".json"
                ? JsonReport.Build(_doc, _rub, _roles, _sc, "SvgCheck.App")
                : TextReport.Build(_doc, _rub, _roles, _sc);
            File.WriteAllText(dlg.FileName, body, new UTF8Encoding(false));
            Status.Text = "已导出 " + dlg.FileName;
        }

        // ---------------------------------------------------------------- 小工具

        private void ShowBanner(string text, bool warn)
        {
            if (string.IsNullOrEmpty(text)) { Banner.Visibility = Visibility.Collapsed; return; }
            BannerText.Text = text;
            Banner.Visibility = Visibility.Visible;
            Banner.Background = warn ? Frozen("#FFF6E7B4") : Frozen("#FFE3EEF2");
            Banner.BorderBrush = warn ? Frozen("#FFE0C060") : Frozen("#FFB6CCD4");
            BannerText.Foreground = warn ? Frozen("#FF5A4300") : Frozen("#FF274551");
        }

        private static string MarkFor(string v) => v switch
        {
            "pass" => "✔",
            "warn" => "▲",
            "fail" => "✖",
            _ => "○"
        };

        private Brush VerdictBrush(string v) => v switch
        {
            "pass" => Frozen("#FF2E7D5B"),
            "warn" => Frozen("#FFB26B00"),
            "fail" => Frozen("#FFC0392B"),
            _ => Frozen("#FF7A828C")
        };

        private static string FmtVal(object v)
        {
            switch (v)
            {
                case null: return "—";
                case double d: return d.ToString("0.###", CultureInfo.InvariantCulture);
                case int i: return i.ToString(CultureInfo.InvariantCulture);
                case bool b: return b ? "是" : "否";
                default: return v.ToString();
            }
        }

        private TextBlock Header(string text, double size, bool bold, Brush ink = null)
            => new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ink ?? Frozen("#FF1C1F24"),
                TextWrapping = TextWrapping.Wrap
            };

        private TextBlock Hint(string text)
            => new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Foreground = Frozen("#FF5B6470"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 1, 0, 1)
            };

        private static readonly Dictionary<string, Brush> _brushes = new Dictionary<string, Brush>();
        private static Brush Frozen(Color c) => Frozen("#FF" + c.R.ToString("X2", CultureInfo.InvariantCulture) +
                                                        c.G.ToString("X2", CultureInfo.InvariantCulture) +
                                                        c.B.ToString("X2", CultureInfo.InvariantCulture));
        private static Brush Frozen(string hex)
        {
            if (_brushes.TryGetValue(hex, out var b)) return b;
            var nb = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            nb.Freeze();
            _brushes[hex] = nb;
            return nb;
        }

        private static Brush BrushFor(string kind) => kind switch
        {
            "pass" => Frozen("#FF2E7D5B"),
            "warn" => Frozen("#FFB26B00"),
            _ => Frozen("#FFC0392B")
        };
    }
}
