using System.Collections.Specialized;
using Avalonia.Controls;
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
            _observed = viewModel.Logs;
            _observed.CollectionChanged += OnLogsChanged;
        }
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e) {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.AutoScrollLogs) {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 }) {
            LogList.ScrollIntoView(e.NewItems[^1]!);
        }
    }
}
