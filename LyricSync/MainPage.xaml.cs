using Microsoft.UI.Xaml.Data;

namespace LyricSync;

public sealed partial class MainPage : Page
{
    /// <summary>
    /// Proxy bound to Display.CurrentIndex; the change callback drives the list
    /// highlight + scroll (kept in code-behind because they are view concerns).
    /// </summary>
    public static readonly DependencyProperty CurrentIndexProperty = DependencyProperty.Register(
        nameof(CurrentIndex), typeof(int), typeof(MainPage), new PropertyMetadata(-1, OnCurrentIndexChanged));

    public MainPage()
    {
        this.InitializeComponent();
        DataContext = AppRoot.ViewModel;

        SetBinding(CurrentIndexProperty, new Binding { Path = new PropertyPath("Display.CurrentIndex") });
    }

    public int CurrentIndex
    {
        get => (int)GetValue(CurrentIndexProperty);
        set => SetValue(CurrentIndexProperty, value);
    }

    private static void OnCurrentIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var page = (MainPage)d;
        var index = (int)e.NewValue;

        try
        {
            var list = page.LyricsList;
            if (index >= 0 && index < list.Items.Count)
            {
                list.SelectedIndex = index;
                list.ScrollIntoView(list.Items[index], ScrollIntoViewAlignment.Leading);
            }
            else
            {
                list.SelectedIndex = -1;
            }
        }
        catch
        {
            // Highlight/scroll is cosmetic; never let it take the app down.
        }
    }
}
