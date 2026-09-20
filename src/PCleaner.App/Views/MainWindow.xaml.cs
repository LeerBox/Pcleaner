using System.Collections.Specialized;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PCleaner.App.ViewModels;

namespace PCleaner.App.Views;

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ConfirmHandler = request => ConfirmDialog.Confirm(this, request.Title, request.Message, request.Warning, request.Detail, "Clean now", viewModel.LogConfirmation);
        Loaded += OnLoaded;
        Closing += OnClosing;
        ((INotifyCollectionChanged)viewModel.LogEntries).CollectionChanged += OnLogChanged;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentSection))
            {
                ResetPageScroll();
                PlayPageTransition();
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        PlayPageTransition();
        await _viewModel.LoadAsync();
        ResetPageScroll();
    }

    /// <summary>
    /// Every page opens at its top. Focus changes during the first layout (before the window has its final size)
    /// can otherwise leave a page scrolled so that a focused button sits at the top edge; the reset runs after
    /// layout has settled.
    /// </summary>
    private void ResetPageScroll()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            DashboardScroll.ScrollToTop();
            RulesScroll.ScrollToTop();
            PrivacyScroll.ScrollToTop();
            SettingsScroll.ScrollToTop();
        });
    }

    /// <summary>
    /// Fade + slide the content host in when the section changes. When the slide has finished, the animation clocks
    /// are released and the transform reset to exact zero, so no sub-pixel offset ever lingers under the text.
    /// </summary>
    private void PlayPageTransition()
    {
        if (PageHost.RenderTransform is not TranslateTransform translate)
        {
            translate = new TranslateTransform();
            PageHost.RenderTransform = translate;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.Y = 0;
            PageHost.BeginAnimation(OpacityProperty, null);
            PageHost.Opacity = 1;
        };

        PageHost.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.State == EngineState.Cleaning)
        {
            var stop = ConfirmDialog.Confirm(this, "Stop cleaning?", "PCleaner is still cleaning. Files that were already removed stay removed.", null, null, "Stop and exit");
            if (!stop)
            {
                e.Cancel = true;
                return;
            }
        }

        _viewModel.Shutdown();
    }

    private void OnRuleRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RuleItemViewModel item } row)
        {
            _viewModel.SelectedItem = item;
            // A row clicked at the very edge of the list is pulled fully into view so the highlight is never cut off.
            row.BringIntoView();
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0 && _viewModel.IsLog)
        {
            LogList.ScrollIntoView(LogList.Items[^1]);
        }
    }
}