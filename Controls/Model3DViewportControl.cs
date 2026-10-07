using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DeadlockVmdlCompiler.Models;

namespace DeadlockVmdlCompiler.Controls;

/// <summary>
/// Model preview. Drag orbits, right or middle drag (or Shift+drag) pans, the wheel zooms
/// towards the view centre, double-click or F frames the model again.
/// </summary>
public class Model3DViewportControl : Control
{
    public static readonly StyledProperty<SimpleMesh3D?> CurrentMeshProperty =
        AvaloniaProperty.Register<Model3DViewportControl, SimpleMesh3D?>(nameof(CurrentMesh));

    public SimpleMesh3D? CurrentMesh
    {
        get => GetValue(CurrentMeshProperty);
        set => SetValue(CurrentMeshProperty, value);
    }

    private enum DragMode { None, Orbit, Pan }

    // Above this size a drag renders at half resolution to stay smooth.
    private const int HeavyTriangleCount = 60_000;
    private const string Hint = "drag: orbit   right-drag: pan   wheel: zoom   double-click: reset";

    private static readonly IPen BorderPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(90, 42, 52, 70)), 1.0);
    private static readonly IBrush HintBrush = new ImmutableSolidColorBrush(Color.FromArgb(150, 148, 163, 184));
    private static readonly Typeface HintTypeface = new(FontFamily.Default);

    private readonly SoftwareMeshRenderer _renderer = new();
    private PreparedMesh? _mesh;
    private OrbitCamera _camera = OrbitCamera.Frame(new Vector3(0, 0.9f, 0), 1.0f, 1.5f);
    private bool _needsFraming = true;
    private DragMode _drag;
    private Point _lastPointer;

    private WriteableBitmap? _bitmap;
    private float[] _depthBuffer = [];
    private int[] _pixelBuffer = [];

    public Model3DViewportControl()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    static Model3DViewportControl()
    {
        AffectsRender<Model3DViewportControl>(CurrentMeshProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CurrentMeshProperty)
        {
            _mesh = PreparedMesh.From(CurrentMesh);
            _needsFraming = true;
        }
    }

    /// <summary>Returns to the default three-quarter view with the whole model in frame.</summary>
    public void ResetView()
    {
        _needsFraming = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        var properties = point.Properties;
        if (properties.IsLeftButtonPressed && e.ClickCount == 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        if (properties.IsRightButtonPressed || properties.IsMiddleButtonPressed ||
            (properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            _drag = DragMode.Pan;
        else if (properties.IsLeftButtonPressed)
            _drag = DragMode.Orbit;
        else
            return;

        _lastPointer = point.Position;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == DragMode.None) return;

        var position = e.GetPosition(this);
        var delta = position - _lastPointer;
        _lastPointer = position;

        if (_drag == DragMode.Orbit)
        {
            // The model follows the cursor: dragging right turns it to the right.
            _camera.Yaw -= (float)delta.X * 0.4f;
            _camera.Pitch = Math.Clamp(_camera.Pitch + (float)delta.Y * 0.4f, -89.0f, 89.0f);
        }
        else
        {
            var forward = Vector3.Normalize(-_camera.Direction);
            var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            var up = Vector3.Cross(right, forward);
            // One pixel of drag moves the point under the cursor by exactly one pixel.
            var unitsPerPixel = 2 * _camera.Distance * MathF.Tan(OrbitCamera.FieldOfView / 2) /
                                (float)Math.Max(1.0, Bounds.Height);
            _camera.Target += (-right * (float)delta.X + up * (float)delta.Y) * unitsPerPixel;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        e.Pointer.Capture(null);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag == DragMode.None) return;
        _drag = DragMode.None;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var radius = _mesh?.Radius ?? 1.0f;
        // Proportional steps feel the same close up and far away.
        _camera.Distance = Math.Clamp(_camera.Distance * MathF.Pow(0.88f, (float)e.Delta.Y),
            radius * 0.03f, radius * 40.0f);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.F or Key.Home)
        {
            ResetView();
            e.Handled = true;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width < 10 || bounds.Height < 10) return;

        // Render in device pixels, at half size while a heavy model is being dragged.
        var scale = (VisualRoot?.RenderScaling ?? 1.0) *
                    (_drag != DragMode.None && _mesh is { TriangleCount: > HeavyTriangleCount } ? 0.5 : 1.0);
        var width = Math.Max(8, (int)(bounds.Width * scale));
        var height = Math.Max(8, (int)(bounds.Height * scale));

        if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Avalonia.Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            if (_pixelBuffer.Length < width * height)
            {
                _pixelBuffer = new int[width * height];
                _depthBuffer = new float[width * height];
            }
        }

        if (_needsFraming)
        {
            _camera = _mesh != null
                ? OrbitCamera.Frame(_mesh.Center, _mesh.Radius, (float)width / height)
                : OrbitCamera.Frame(new Vector3(0, 0.9f, 0), 1.0f, (float)width / height);
            _needsFraming = false;
        }

        _renderer.Render(_mesh, _camera, width, height, _pixelBuffer, _depthBuffer);

        using (var locked = _bitmap.Lock())
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(_pixelBuffer, y * width, IntPtr.Add(locked.Address, y * locked.RowBytes), width);
        }

        var target = new Rect(0, 0, bounds.Width, bounds.Height);
        context.DrawImage(_bitmap, new Rect(0, 0, width, height), target);
        context.DrawRectangle(null, BorderPen, new Rect(0.5, 0.5, bounds.Width - 1, bounds.Height - 1));

        if (_mesh != null && bounds.Width > 420)
        {
            var hint = new FormattedText(Hint, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                HintTypeface, 10, HintBrush);
            context.DrawText(hint, new Point(8, bounds.Height - hint.Height - 6));
        }
    }
}
