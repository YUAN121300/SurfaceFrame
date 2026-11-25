using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SurfaceFrame;

/// <summary>
/// Centers and scales Image children inside a clipped viewport without triggering WPF's unclippedDesiredSize zoom bug.
/// </summary>
public sealed class CenterFillPanel : Panel
{
    static readonly HashSet<string> LoggedPanels = new(StringComparer.Ordinal);

    public static readonly DependencyProperty CropProperty = DependencyProperty.Register(
        nameof(Crop),
        typeof(bool),
        typeof(CenterFillPanel),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public bool Crop
    {
        get => (bool)GetValue(CropProperty);
        set => SetValue(CropProperty, value);
    }

    public CenterFillPanel()
    {
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var availW = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var availH = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(default);
                continue;
            }

            if (TryGetContentSize(child, out var cw, out var ch) && availW > 0 && availH > 0)
            {
                var box = CenterFillLayout.Compute(availW, availH, cw, ch, Crop);
                child.Measure(new Size(box.Width, box.Height));
            }
            else
            {
                child.Measure(new Size(availW, availH));
            }
        }

        return new Size(availW, availH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width <= 0 || finalSize.Height <= 0)
            return finalSize;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Arrange(default);
                continue;
            }

            if (TryGetContentSize(child, out var cw, out var ch))
            {
                var box = CenterFillLayout.Compute(finalSize.Width, finalSize.Height, cw, ch, Crop);
                if (Math.Abs(child.DesiredSize.Width - box.Width) > 0.5
                    || Math.Abs(child.DesiredSize.Height - box.Height) > 0.5)
                {
                    child.Measure(new Size(box.Width, box.Height));
                }
                child.Arrange(new Rect(box.X, box.Y, box.Width, box.Height));

                if (!string.IsNullOrEmpty(Name) && LoggedPanels.Add(Name))
                {
                    Log.Info(string.Format(
                        CultureInfo.InvariantCulture,
                        "layout {0} {1:0}x{2:0} img={3:0}x{4:0} -> ({5:0.0},{6:0.0},{7:0.0},{8:0.0})",
                        Name, finalSize.Width, finalSize.Height, cw, ch, box.X, box.Y, box.Width, box.Height));
                }
            }
            else
            {
                child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            }
        }

        return finalSize;
    }

    static bool TryGetContentSize(UIElement child, out double width, out double height)
    {
        width = 0;
        height = 0;
        if (child is not Image img || img.Source is not BitmapSource bmp || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0)
            return false;

        width = bmp.PixelWidth;
        height = bmp.PixelHeight;
        if (img.LayoutTransform is RotateTransform rot)
        {
            var angle = Math.Abs((int)Math.Round(rot.Angle)) % 180;
            if (angle == 90)
                (width, height) = (height, width);
        }
        return true;
    }
}
