using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using Boxboard.Services;

namespace Boxboard;

public partial class LogWindow : Window
{
    private readonly ActivityLog _log;
    private bool _scrollQueued;
    public LogWindow(ActivityLog log)
    {
        InitializeComponent();
        _log = log;
        DataContext = log;
        _log.Entries.CollectionChanged += OnEntry;
        Closed += (_, _) => _log.Entries.CollectionChanged -= OnEntry;
    }
    private void OnEntry(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_scrollQueued || e.NewItems is not { Count: > 0 })
            return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _scrollQueued = false;
            if (IsVisible && _log.Entries.Count > 0)
                LogList.ScrollIntoView(_log.Entries[^1]);
        }));
    }
}
