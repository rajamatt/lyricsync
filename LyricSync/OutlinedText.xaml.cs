namespace LyricSync;

public sealed partial class OutlinedText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(OutlinedText), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty WrappingProperty = DependencyProperty.Register(
        nameof(Wrapping), typeof(TextWrapping), typeof(OutlinedText), new PropertyMetadata(TextWrapping.NoWrap));

    public static readonly DependencyProperty TrimmingProperty = DependencyProperty.Register(
        nameof(Trimming), typeof(TextTrimming), typeof(OutlinedText), new PropertyMetadata(TextTrimming.CharacterEllipsis));

    public static readonly DependencyProperty MaxTextLinesProperty = DependencyProperty.Register(
        nameof(MaxTextLines), typeof(int), typeof(OutlinedText), new PropertyMetadata(1));

    public OutlinedText()
    {
        this.InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public TextWrapping Wrapping
    {
        get => (TextWrapping)GetValue(WrappingProperty);
        set => SetValue(WrappingProperty, value);
    }

    public TextTrimming Trimming
    {
        get => (TextTrimming)GetValue(TrimmingProperty);
        set => SetValue(TrimmingProperty, value);
    }

    public int MaxTextLines
    {
        get => (int)GetValue(MaxTextLinesProperty);
        set => SetValue(MaxTextLinesProperty, value);
    }
}
