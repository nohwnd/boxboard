using System.Windows;
using System.Windows.Media;
using Boxboard.Models;

namespace Boxboard.Controls;

public sealed class LayoutPreview : FrameworkElement
{
    private static readonly Brush FrameBrush = new SolidColorBrush(Color.FromRgb(246, 249, 251));
    private static readonly Brush SlotBrush = new SolidColorBrush(Color.FromRgb(32, 116, 180));
    private static readonly Pen FramePen = new(new SolidColorBrush(Color.FromRgb(166, 187, 201)), 1);

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(WindowLayoutMode), typeof(LayoutPreview),
        new FrameworkPropertyMetadata(WindowLayoutMode.Quadrants, FrameworkPropertyMetadataOptions.AffectsRender));

    public WindowLayoutMode Mode
    {
        get => (WindowLayoutMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth < 8 || ActualHeight < 8)
            return;

        var frame = new Rect(0.5, 0.5, ActualWidth - 1, ActualHeight - 1);
        drawingContext.DrawRoundedRectangle(FrameBrush, FramePen, frame, 3, 3);
        var content = new Rect(frame.X + 3, frame.Y + 3, frame.Width - 6, frame.Height - 6);
        foreach (var slot in WindowLayoutGeometry.Divide(new(0, 0, 240, 200), Mode))
        {
            var rect = new Rect(content.X + slot.X * content.Width / 240 + 1,
                content.Y + slot.Y * content.Height / 200 + 1,
                slot.Width * content.Width / 240 - 2,
                slot.Height * content.Height / 200 - 2);
            drawingContext.DrawRoundedRectangle(SlotBrush, null, rect, 2, 2);
        }
    }
}
