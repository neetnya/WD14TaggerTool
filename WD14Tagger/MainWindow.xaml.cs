using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using WD14Tagger.ViewModels;

namespace WD14Tagger;

public partial class MainWindow : Window
{
    private bool _activationRescan;

    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
        Activated += OnActivated;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>窗口第一次激活时再扫一次 models\，防止启动瞬间目录还没就绪。</summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        if (_activationRescan) return;
        _activationRescan = true;
        if (Vm is { HasModels: false }) Vm.ReloadModels();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths)
        {
            if (File.Exists(path) || Directory.Exists(path))
                await Vm!.AddPathAsync(path);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            Vm?.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            Vm?.StartCommand.Execute(null);
            e.Handled = true;
        }
    }
}
