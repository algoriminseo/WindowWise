namespace WindowWise.Mobile.Controls;

// A circular progress ring: a faint full track and a blue arc from the top, clockwise.
public sealed class ProgressRing : GraphicsView
{
    public static readonly BindableProperty ProgressProperty = BindableProperty.Create(
        nameof(Progress), typeof(double), typeof(ProgressRing), 0d, propertyChanged: (view, _, _) => ((ProgressRing)view).Invalidate());

    public ProgressRing() => Drawable = new RingDrawable(this);

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    private sealed class RingDrawable(ProgressRing ring) : IDrawable
    {
        private const float Thickness = 14;

        public void Draw(ICanvas canvas, RectF bounds)
        {
            float size = Math.Min(bounds.Width, bounds.Height) - Thickness;
            var box = new RectF(bounds.Center.X - size / 2, bounds.Center.Y - size / 2, size, size);
            canvas.StrokeSize = Thickness;
            canvas.StrokeLineCap = LineCap.Round;
            canvas.StrokeColor = Color.FromArgb("#12FFFFFF");
            canvas.DrawEllipse(box);

            double progress = Math.Clamp(ring.Progress, 0, 1);
            if (progress <= 0) return;
            canvas.SetShadow(new SizeF(0, 0), 18, Color.FromArgb("#805A82FF"));
            canvas.StrokeColor = Color.FromArgb("#7FA2FF");
            // Angles run counter-clockwise from 3 o'clock; start at 12 o'clock and sweep clockwise.
            float end = 90 - (float)(progress * 360);
            if (progress >= 0.999) canvas.DrawEllipse(box);
            else canvas.DrawArc(box, 90, end, true, false);
        }
    }
}
