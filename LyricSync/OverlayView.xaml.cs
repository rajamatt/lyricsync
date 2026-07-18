using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Animation;

namespace LyricSync;

public sealed partial class OverlayView : UserControl
{
    /// <summary>
    /// Proxy bound to Display.CurrentIndex; the change callback runs the slide
    /// animation (a view concern, so it lives in code-behind rather than the model).
    /// </summary>
    public static readonly DependencyProperty CurrentIndexProperty = DependencyProperty.Register(
        nameof(CurrentIndex), typeof(int), typeof(OverlayView), new PropertyMetadata(-1, OnCurrentIndexChanged));

    public OverlayView()
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
        var view = (OverlayView)d;
        var previous = (int)e.OldValue;
        var current = (int)e.NewValue;

        if (previous < 0 || current < 0)
        {
            view.LyricsTranslate.Y = 0;
            return;
        }

        var delta = current - previous;
        view.AnimateLineChange(delta);
    }

    /// <summary>
    /// Slides the lyric block in from the direction of travel: on a normal advance the
    /// incoming line rises from below into place (subtitle style). Jumps (seeks) snap.
    /// </summary>
    private void AnimateLineChange(int delta)
    {
        if (delta == 0 || Math.Abs(delta) > 2)
        {
            LyricsTranslate.Y = 0;
            return;
        }

        var secondaryFontSize = 16.0;
        if (DataContext is MainViewModel vm && vm.SecondaryFontSize is double s and > 0)
        {
            secondaryFontSize = s;
        }

        var offset = Math.Sign(delta) * (secondaryFontSize * 1.6 + 6);
        try
        {
            var animation = new DoubleAnimation
            {
                From = offset,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(320)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true,
            };
            var storyboard = new Storyboard();
            Storyboard.SetTarget(animation, LyricsTranslate);
            Storyboard.SetTargetProperty(animation, nameof(LyricsTranslate.Y));
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
            LyricsTranslate.Y = 0;
        }
    }
}
