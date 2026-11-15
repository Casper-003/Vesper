using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Vesper.App;

public sealed partial class MainWindow
{
    private sealed record QueuePeekChrome(ShapeVisual Visual, CompositionRoundedRectangleGeometry FillGeometry,
        CompositionRoundedRectangleGeometry StrokeGeometry, CompositionColorBrush FillBrush, CompositionColorBrush StrokeBrush);
    private QueuePeekChrome? queueBrowserPeekChrome, singingPanelPeekChrome, queuePanelPeekChrome;
    private CompositionPropertySet? queueRailPeekState;
    private ExpressionAnimation? singingPeekContentTranslation, queuePeekContentTranslation;
    private bool queuePeekContentTranslationRunning, queueRailPeekActive, queueRailPeekTarget;
    private int queueRailPeekRevision;
    private float queueRailPeekAmount;
    private Vector2 queuePeekBrowserSize, singingPeekPanelSize, queuePeekPanelSize, queuePeekContentSize;
    private float queuePeekBrowserFillInset, singingPeekPanelFillInset, queuePeekPanelFillInset;
    private double queuePeekBrowserOpacity, singingPeekPanelOpacity, queuePeekPanelOpacity;

    private void InitializeQueueRailPeek()
    {
        // The wider, constant hit area is centered two units left of the 34-unit
        // rail. Only the content's facade translation compensates that difference.
        SingingToggleContent.Translation = new Vector3(2, 0, 0);
        QueueToggleContent.Translation = new Vector3(2, 0, 0);
        Root.SizeChanged += OnQueuePeekRootSizeChanged;
        Root.ActualThemeChanged += OnQueuePeekThemeChanged;
    }

    private void OnQueuePeekRootSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (queueRailPeekActive) ResetQueueRailPeek();
    }

    private void OnQueuePeekThemeChanged(FrameworkElement sender, object args) => ResetQueueRailPeek();

    private void CloseQueueRailPeek()
    {
        // AppWindow.Closing resets the live visuals before the native window is
        // destroyed. Closed cleanup only drops callbacks; no composition writes.
        if (!closed) ResetQueueRailPeek();
        else
        {
            ++queueRailPeekRevision;
            queueRailPeekActive = queueRailPeekTarget = queuePeekContentTranslationRunning = false;
        }
        Root.SizeChanged -= OnQueuePeekRootSizeChanged;
        Root.ActualThemeChanged -= OnQueuePeekThemeChanged;
    }

    private void AnimateQueueRailPeek(bool peek)
    {
        if (closed) return;
        peek = peek && queueSidebarVisible && !queueSidebarAnimating && !singingPanelOpen && !queuePanelOpen && !queueLayoutAnimating;
        if (!queueRailPeekActive)
        {
            if (!peek || !PrepareQueueRailPeek()) return;
        }
        else if (queueRailPeekTarget == peek) return;
        queueRailPeekTarget = peek;
        int revision = ++queueRailPeekRevision;
        BindQueuePeekExpressions();
        var compositor = queueRailPeekState!.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, peek ? 4 : 0, compositor.CreateCubicBezierEasingFunction(new Vector2(.215f, .61f), new Vector2(.355f, 1)));
        animation.Duration = TimeSpan.FromMilliseconds(140);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        queueRailPeekState.StartAnimation("Amount", animation);
        batch.Completed += (_, _) =>
        {
            if (closed) return;
            batch.Dispose();
            if (revision != queueRailPeekRevision || !queueRailPeekActive) return;
            float amount = peek ? 4 : 0;
            queueRailPeekState.InsertScalar("Amount", amount);
            queueRailPeekState.StopAnimation("Amount");
            MaterializeQueueRailPeek(amount);
            if (!peek) ResetQueueRailPeek();
        };
        batch.End();
    }

    private bool PrepareQueueRailPeek()
    {
        if (browserContentClipGeometry is null || BrowserCardChrome.ActualWidth <= 8 || SingingShellChrome.ActualWidth <= 8 || QueueShellChrome.ActualWidth <= 8) return false;
        if (!TryQueuePeekBrushColor(BrowserCardChrome.Background, out var browserFill)
            || !TryQueuePeekBrushColor(BrowserCardChrome.BorderBrush, out var browserStroke)
            || !TryQueuePeekBrushColor(SingingShellChrome.Background, out var singingFill)
            || !TryQueuePeekBrushColor(SingingShellChrome.BorderBrush, out var singingStroke)
            || !TryQueuePeekBrushColor(QueueShellChrome.Background, out var queueFill)
            || !TryQueuePeekBrushColor(QueueShellChrome.BorderBrush, out var queueStroke)) return false;
        var compositor = ElementCompositionPreview.GetElementVisual(Root).Compositor;
        queueRailPeekState ??= compositor.CreatePropertySet();
        queueBrowserPeekChrome ??= CreateQueuePeekChrome(compositor, BrowserPeekChromeLayer);
        singingPanelPeekChrome ??= CreateQueuePeekChrome(compositor, SingingPeekChromeLayer);
        queuePanelPeekChrome ??= CreateQueuePeekChrome(compositor, QueuePeekChromeLayer);
        queuePeekBrowserSize = new((float)BrowserCardChrome.ActualWidth, (float)BrowserCardChrome.ActualHeight);
        singingPeekPanelSize = new((float)SingingShellChrome.ActualWidth, (float)SingingShellChrome.ActualHeight);
        queuePeekPanelSize = new((float)QueueShellChrome.ActualWidth, (float)QueueShellChrome.ActualHeight);
        queuePeekContentSize = new((float)BrowserContentClip.ActualWidth, (float)BrowserContentClip.ActualHeight);
        queuePeekBrowserFillInset = BrowserCardChrome.BackgroundSizing == BackgroundSizing.InnerBorderEdge ? 1 : 0;
        singingPeekPanelFillInset = SingingShellChrome.BackgroundSizing == BackgroundSizing.InnerBorderEdge ? 1 : 0;
        queuePeekPanelFillInset = QueueShellChrome.BackgroundSizing == BackgroundSizing.InnerBorderEdge ? 1 : 0;
        queuePeekBrowserOpacity = BrowserCardChrome.Opacity;
        singingPeekPanelOpacity = SingingShellChrome.Opacity;
        queuePeekPanelOpacity = QueueShellChrome.Opacity;
        queueRailPeekState.InsertScalar("Amount", 0);
        queueRailPeekState.InsertVector2("BrowserSize", queuePeekBrowserSize);
        queueRailPeekState.InsertVector2("SingingSize", singingPeekPanelSize);
        queueRailPeekState.InsertVector2("PanelSize", queuePeekPanelSize);
        queueRailPeekState.InsertVector2("ContentSize", queuePeekContentSize);
        queueBrowserPeekChrome.FillBrush.Color = browserFill; queueBrowserPeekChrome.StrokeBrush.Color = browserStroke;
        singingPanelPeekChrome.FillBrush.Color = singingFill; singingPanelPeekChrome.StrokeBrush.Color = singingStroke;
        queuePanelPeekChrome.FillBrush.Color = queueFill; queuePanelPeekChrome.StrokeBrush.Color = queueStroke;
        queueBrowserPeekChrome.Visual.Size = queuePeekBrowserSize + new Vector2(4, 0);
        singingPanelPeekChrome.Visual.Size = singingPeekPanelSize + new Vector2(4, 0);
        queuePanelPeekChrome.Visual.Size = queuePeekPanelSize + new Vector2(4, 0);
        queueRailPeekActive = true;
        MaterializeQueueRailPeek(0);
        queueBrowserPeekChrome.Visual.Opacity = (float)queuePeekBrowserOpacity;
        singingPanelPeekChrome.Visual.Opacity = (float)singingPeekPanelOpacity;
        queuePanelPeekChrome.Visual.Opacity = (float)queuePeekPanelOpacity;
        BrowserCardChrome.Opacity = SingingShellChrome.Opacity = QueueShellChrome.Opacity = 0;
        return true;
    }

    private static bool TryQueuePeekBrushColor(Brush? brush, out Color color)
    {
        if (brush is SolidColorBrush solid)
        {
            color = solid.Color;
            color.A = (byte)Math.Clamp((int)Math.Round(color.A * solid.Opacity), 0, 255);
            return true;
        }
        color = default;
        return brush is null;
    }

    private static QueuePeekChrome CreateQueuePeekChrome(Compositor compositor, UIElement layer)
    {
        var fillGeometry = compositor.CreateRoundedRectangleGeometry();
        var strokeGeometry = compositor.CreateRoundedRectangleGeometry();
        strokeGeometry.CornerRadius = new Vector2(7.5f);
        var fill = compositor.CreateColorBrush();
        var stroke = compositor.CreateColorBrush();
        var fillShape = compositor.CreateSpriteShape(fillGeometry); fillShape.FillBrush = fill;
        var strokeShape = compositor.CreateSpriteShape(strokeGeometry); strokeShape.StrokeBrush = stroke; strokeShape.StrokeThickness = 1;
        // Leave four units of actual drawing extent on the left. The container
        // offset cancels the visual offset, preserving the geometry's local bounds.
        var shapes = compositor.CreateContainerShape();
        shapes.Offset = new Vector2(4, 0);
        shapes.Shapes.Add(fillShape); shapes.Shapes.Add(strokeShape);
        var visual = compositor.CreateShapeVisual();
        visual.Offset = new Vector3(-4, 0, 0);
        visual.Shapes.Add(shapes); visual.Opacity = 0;
        ElementCompositionPreview.SetElementChildVisual(layer, visual);
        return new(visual, fillGeometry, strokeGeometry, fill, stroke);
    }

    private ExpressionAnimation QueuePeekExpression(string expression)
    {
        var animation = queueRailPeekState!.Compositor.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("hint", queueRailPeekState);
        return animation;
    }

    private void BindQueuePeekExpressions()
    {
        BindQueuePeekChrome(queueBrowserPeekChrome!, "BrowserSize", false, queuePeekBrowserFillInset);
        BindQueuePeekChrome(singingPanelPeekChrome!, "SingingSize", true, singingPeekPanelFillInset);
        BindQueuePeekChrome(queuePanelPeekChrome!, "PanelSize", true, queuePeekPanelFillInset);
        browserContentClipGeometry!.StartAnimation("Size", QueuePeekExpression("Vector2(Max(0, hint.ContentSize.X - hint.Amount), hint.ContentSize.Y)"));
        singingPeekContentTranslation ??= QueuePeekExpression("Vector3(2 - hint.Amount * 0.5, 0, 0)");
        singingPeekContentTranslation.Target = "Translation";
        SingingToggleContent.StartAnimation(singingPeekContentTranslation);
        queuePeekContentTranslation ??= QueuePeekExpression("Vector3(2 - hint.Amount * 0.5, 0, 0)");
        queuePeekContentTranslation.Target = "Translation";
        QueueToggleContent.StartAnimation(queuePeekContentTranslation);
        queuePeekContentTranslationRunning = true;
    }

    private void BindQueuePeekChrome(QueuePeekChrome chrome, string size, bool panel, float fillInset)
    {
        foreach (var (geometry, inset) in new[] { (chrome.FillGeometry, fillInset), (chrome.StrokeGeometry, .5f) })
        {
            var width = QueuePeekExpression($"Vector2(Max(0, hint.{size}.X {(panel ? "+" : "-")} hint.Amount - inset * 2), Max(0, hint.{size}.Y - inset * 2))");
            width.SetScalarParameter("inset", inset);
            geometry.StartAnimation("Size", width);
            var offset = QueuePeekExpression(panel ? "Vector2(inset - hint.Amount, inset)" : "Vector2(inset, inset)");
            offset.SetScalarParameter("inset", inset);
            geometry.StartAnimation("Offset", offset);
        }
    }

    private void MaterializeQueueRailPeek(float amount)
    {
        queueRailPeekAmount = amount;
        SetQueuePeekChrome(queueBrowserPeekChrome!, queuePeekBrowserSize, amount, false, queuePeekBrowserFillInset);
        SetQueuePeekChrome(singingPanelPeekChrome!, singingPeekPanelSize, amount, true, singingPeekPanelFillInset);
        SetQueuePeekChrome(queuePanelPeekChrome!, queuePeekPanelSize, amount, true, queuePeekPanelFillInset);
        if (browserContentClipGeometry is not null)
        {
            browserContentClipGeometry.Size = new Vector2(Math.Max(0, queuePeekContentSize.X - amount), queuePeekContentSize.Y);
            browserContentClipGeometry.StopAnimation("Size");
        }
        SingingToggleContent.Translation = new Vector3(2 - amount * .5f, 0, 0);
        QueueToggleContent.Translation = new Vector3(2 - amount * .5f, 0, 0);
        if (queuePeekContentTranslationRunning)
        {
            SingingToggleContent.StopAnimation(singingPeekContentTranslation!);
            QueueToggleContent.StopAnimation(queuePeekContentTranslation!);
            queuePeekContentTranslationRunning = false;
        }
    }

    private static void SetQueuePeekChrome(QueuePeekChrome chrome, Vector2 size, float amount, bool panel, float fillInset)
    {
        float width = size.X + (panel ? amount : -amount);
        foreach (var (geometry, inset) in new[] { (chrome.FillGeometry, fillInset), (chrome.StrokeGeometry, .5f) })
        {
            geometry.CornerRadius = new Vector2(8 - inset);
            geometry.Size = new Vector2(Math.Max(0, width - inset * 2), Math.Max(0, size.Y - inset * 2));
            geometry.Offset = new Vector2(inset - (panel ? amount : 0), inset);
            geometry.StopAnimation("Size"); geometry.StopAnimation("Offset");
        }
    }

    private void ResetQueueRailPeek()
    {
        if (closed) return;
        ++queueRailPeekRevision;
        queueRailPeekState?.StopAnimation("Amount");
        queueRailPeekState?.InsertScalar("Amount", 0);
        if (queueRailPeekActive)
        {
            MaterializeQueueRailPeek(0);
            queueBrowserPeekChrome!.Visual.Opacity = singingPanelPeekChrome!.Visual.Opacity = queuePanelPeekChrome!.Visual.Opacity = 0;
            BrowserCardChrome.Opacity = queuePeekBrowserOpacity;
            SingingShellChrome.Opacity = singingPeekPanelOpacity;
            QueueShellChrome.Opacity = queuePeekPanelOpacity;
            queueRailPeekActive = false;
            UpdateBrowserContentClipSize(BrowserContentClip.ActualWidth, BrowserContentClip.ActualHeight);
        }
        queueRailPeekAmount = 0;
        queueRailPeekTarget = false;
        SingingToggleContent.Translation = new Vector3(2, 0, 0);
        QueueToggleContent.Translation = new Vector3(2, 0, 0);
    }
}
