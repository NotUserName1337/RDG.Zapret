using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using RDG.Zapret.ViewModels;

namespace RDG.Zapret.Views;

public partial class LogsView : UserControl
{
    private INotifyCollectionChanged? _items;

    public LogsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (LogList.ItemsSource is null)
            return;

        _items = LogList.Items as INotifyCollectionChanged;
        if (_items is not null)
            _items.CollectionChanged += OnItemsChanged;
        ScrollToEnd();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_items is not null)
        {
            _items.CollectionChanged -= OnItemsChanged;
            _items = null;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is LogsViewModel { AutoScroll: true } && e.Action == NotifyCollectionChangedAction.Add)
            ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count == 0)
            return;

        var last = LogList.Items[LogList.Items.Count - 1];
        if (last is not null)
            LogList.ScrollIntoView(last);
    }
}
