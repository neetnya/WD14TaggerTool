using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WD14Tagger.Core;
using WD14Tagger.ViewModels;

namespace WD14Tagger;

public partial class App : Application
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool cliMode = e.Args.Any(a => a.Equals("--cli", StringComparison.OrdinalIgnoreCase));
        if (cliMode)
        {
            AttachConsole(AttachParentProcess);
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* ignore */ }
            int code = CliRunner.Run(e.Args);
            Shutdown(code);
            return;
        }

        base.OnStartup(e);

        var paths = new AppPaths();
        paths.EnsureCreated();
        var config = AppConfig.LoadOrCreate(paths.ConfigPath);

        var viewModel = new MainViewModel(paths, config);
        string? renderPath = ArgValue(e.Args, "--render");
        if (renderPath != null) viewModel.Headless = true;

        // 启动就读 models\：InitializeAsync 全是同步操作，这里直接跑完再显示窗口，
        // 避免依赖 Loaded 事件（Show() 期间 Loaded 可能已经触发，再挂就漏了）。
        try
        {
            viewModel.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败：{ex.Message}", "出错", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();

        // 无头自检：--render <png> [--demo <图片目录>] [--demo-run]  → 渲染界面截图后退出
        string? demoDir = ArgValue(e.Args, "--demo");
        if (renderPath != null)
        {
            string logPath = Path.ChangeExtension(Path.GetFullPath(renderPath), ".log");
            void Log(string message)
            {
                try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n", Encoding.UTF8); } catch { }
            }

            File.WriteAllText(logPath, "", Encoding.UTF8);
            Log($"start render={renderPath} demo={demoDir ?? "(none)"}");

            bool demoRun = e.Args.Any(a => a.Equals("--demo-run", StringComparison.OrdinalIgnoreCase));
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                try
                {
                    Log($"models={viewModel.Models.Count} items={viewModel.Items.Count}");
                    if (demoDir != null) await viewModel.AddPathAsync(demoDir);
                    Log($"after add items={viewModel.Items.Count}");
                    if (demoRun && viewModel.HasItems)
                    {
                        viewModel.StartCommand.Execute(null);
                        while (viewModel.IsRunning) await Task.Delay(150);
                        Log("run finished");
                    }
                    await Task.Delay(500);
                    if (viewModel.SelectedItem == null && viewModel.Items.Count > 0)
                        viewModel.SelectedItem = viewModel.Items[0];
                    await Task.Delay(700);
                    Log($"rendering {window.ActualWidth}x{window.ActualHeight}");
                    SaveWindowPng(window, renderPath);
                    Log("saved");
                    Shutdown(0);
                }
                catch (Exception ex)
                {
                    Log("ERROR " + ex);
                    Shutdown(1);
                }
            }));
            return;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static void SaveWindowPng(Window window, string path)
    {
        window.UpdateLayout();
        int w = (int)Math.Ceiling(window.ActualWidth > 0 ? window.ActualWidth : window.Width);
        int h = (int)Math.Ceiling(window.ActualHeight > 0 ? window.ActualHeight : window.Height);
        if (w <= 0) w = 1240;
        if (h <= 0) h = 840;

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        (MainWindow?.DataContext as MainViewModel)?.SaveConfig();
        base.OnExit(e);
    }
}
