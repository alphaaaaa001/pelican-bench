using System;
using System.Linq;
using System.Windows;

namespace SvgCheck.App
{
    public partial class App : Application
    {
        /// <summary>命令行传入的文件（`pelican-bench.exe A.html`）留给主窗消费。</summary>
        public static string PendingFile;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 无头快照模式：把产出渲染成 PNG 就退出。它存在的理由是验收——
            // "进程没崩"不等于"渲染是对的"，得有人真的看见那张图。
            if (e.Args != null && e.Args.Length >= 3 &&
                string.Equals(e.Args[0], "--render", StringComparison.OrdinalIgnoreCase))
            {
                var rc = Rendering.Snapshot.Run(e.Args[1], e.Args[2],
                    e.Args.Length > 3 && double.TryParse(e.Args[3], out var w) ? w : 1200,
                    e.Args.Length > 4 && double.TryParse(e.Args[4], out var h) ? h : 800);
                Shutdown(rc);
                return;
            }

            if (e.Args != null && e.Args.Length > 0 && !e.Args[0].StartsWith("-", StringComparison.Ordinal))
                PendingFile = e.Args[0];

            var win = new MainWindow();
            win.Show();
        }
    }
}
