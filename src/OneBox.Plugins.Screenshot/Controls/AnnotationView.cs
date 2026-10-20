using System.Windows;
using System.Windows.Media;
using OneBox.Plugins.Screenshot.Core;
using OneBox.Plugins.Screenshot.Services;

namespace OneBox.Plugins.Screenshot.Controls;

/// <summary>
/// 贴图上的标注绘制层。只负责画，不参与命中测试；
/// 鼠标事件继续由 PinWindow 处理。
/// </summary>
public sealed class AnnotationView : FrameworkElement
{
    private IReadOnlyList<PinAnnotation> _annotations = [];
    private PinAnnotation? _draft;
    private double _imageWidth = 1;
    private double _imageHeight = 1;

    public void SetAnnotations(IReadOnlyList<PinAnnotation> annotations)
    {
        _annotations = annotations;
        InvalidateVisual();
    }

    public void SetDraft(PinAnnotation? draft)
    {
        _draft = draft;
        InvalidateVisual();
    }

    public void SetImageSize(double width, double height)
    {
        _imageWidth = Math.Max(1, width);
        _imageHeight = Math.Max(1, height);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var scaleX = ActualWidth / _imageWidth;
        var scaleY = ActualHeight / _imageHeight;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        PinAnnotationRenderer.Draw(drawingContext, _annotations, scaleX, scaleY, pixelsPerDip);
        if (_draft is not null)
            PinAnnotationRenderer.DrawDraft(drawingContext, _draft, scaleX, scaleY, pixelsPerDip);
    }
}
