using System.Collections.Specialized;
using Avalonia.Controls;
using ViewLight.Services;
using ViewLight.ViewModels;

namespace ViewLight.Views;

public partial class LogsView : UserControl {

    private INotifyCollectionChanged? _observed;

    public LogsView() {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) {
        if (_observed is not null) {
            _observed.CollectionChanged -= OnLogsChanged;
            _observed = null;
        }

        if (DataContext is MainWindowViewModel viewModel) {
            _observed = viewModel.Log.Blocks;
            _observed.CollectionChanged += OnLogsChanged;
        }
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.AutoScrollLogs) {
            return;
        }

        // Only the bottom of the Log is worth following: a line arriving changes the last block, and the
        // line dropping off the top of a full Log changes the first one. Folding a run open somewhere in
        // the middle changes neither, and dragging the view down to the end for that would be the launcher
        // moving the page under somebody who is reading it.
        var atTheEnd = e.Action switch {
            NotifyCollectionChangedAction.Add => true,
            NotifyCollectionChangedAction.Replace => e.NewStartingIndex == viewModel.Log.Blocks.Count - 1,
            _ => false,
        };

        if (atTheEnd) {
            LogScroll.ScrollToEnd();
        }
    }

    private void OnFoldClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) {
        if (sender is Button { DataContext: LogBlock block } && DataContext is MainWindowViewModel viewModel) {
            viewModel.ToggleLogRepeat(block);
        }
    }
}
