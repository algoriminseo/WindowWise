using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowWise.Controls;

public partial class FeatureCard : UserControl
{
    public static readonly DependencyProperty TitleProperty = Register<string>(nameof(Title));
    public static readonly DependencyProperty DescriptionProperty = Register<string>(nameof(Description));
    public static readonly DependencyProperty ActionTextProperty = Register<string>(nameof(ActionText));
    public static readonly DependencyProperty IconDataProperty = Register<Geometry>(nameof(IconData));

    public FeatureCard() => InitializeComponent();

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public Geometry IconData { get => (Geometry)GetValue(IconDataProperty); set => SetValue(IconDataProperty, value); }

    private static DependencyProperty Register<T>(string name) =>
        DependencyProperty.Register(name, typeof(T), typeof(FeatureCard));
}
