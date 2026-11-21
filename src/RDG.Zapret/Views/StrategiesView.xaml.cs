using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RDG.Zapret.ViewModels;

namespace RDG.Zapret.Views;

public partial class StrategiesView : UserControl
{
    private StrategiesViewModel? _viewModel;

    public StrategiesView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel = DataContext as StrategiesViewModel;
        if (_viewModel is null)
            return;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Strategies.CollectionChanged += OnStrategiesChanged;
        QueueScrollToSelection();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Strategies.CollectionChanged -= OnStrategiesChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StrategiesViewModel.SelectedItem))
            QueueScrollToSelection();
    }

    private void OnStrategiesChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueScrollToSelection();

    private void QueueScrollToSelection()
    {
        Dispatcher.BeginInvoke(new Action(ScrollToSelection));
    }

    private void ScrollToSelection()
    {
        if (StrategyList.Items.Count == 0)
            return;

        var target = StrategyList.SelectedItem ?? StrategyList.Items[0];
        if (target is not null)
            StrategyList.ScrollIntoView(target);
    }
}
