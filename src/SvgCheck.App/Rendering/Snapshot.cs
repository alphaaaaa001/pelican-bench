using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SvgCheck.Core.Parsing;

namespace SvgCheck.App.Rendering
{
    /// <summary>
    /// 离屏渲染：把一份产出画成 PNG，不建窗口。
    ///
    /// 用途有两个：验收（让改判据的人真的看见自己画出了什么），以及 CI 里留快照。
    /// 走的是和窗口完全同一条 OnRender 路径 —— 所以"预览里看到的"和"渲染出来的"必然一致，
    /// 不会出现两套画法。
    /// </summary>
    public static class Snapshot
    {
        public static int Run(string input, string output, double width, double height)
        {
            Exception err = null;
            var t = new Thread(() =>
            {
                try
                {
                    var doc = SvgDocument.LoadFile(input);
                    var view = new SvgView { Document = doc };
                    var size = new Size(width, height);
                    view.Measure(size);
                    view.Arrange(new Rect(size));
                    view.UpdateLayout();

                    var rtb = new RenderTargetBitmap((int)Math.Round(width), (int)Math.Round(height),
                        96, 96, PixelFormats.Pbgra32);
                    rtb.Render(view);

                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(output)) enc.Save(fs);
                }
                catch (Exception e) { err = e; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();

            if (err != null)
            {
                Console.Error.WriteLine("渲染失败：" + err);
                return 1;
            }
            Console.WriteLine("已渲染 " + output + "（" + (int)width + "x" + (int)height + "）");
            return 0;
        }
    }
}
