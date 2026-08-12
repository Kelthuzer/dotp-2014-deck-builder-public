using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DeckBuilder.Modern;

internal sealed class CustomDeckCoverEditorWindow : Window
{
    private const int ColorWheelSize = 180;

    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly ComboBox _materialBox = new();
    private readonly Image _colorWheel = new() { Width = ColorWheelSize, Height = ColorWheelSize, Stretch = Stretch.None };
    private readonly Border _colorSwatch = new() { Height = 26, BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
    private readonly TextBlock _colorText = new();
    private readonly Slider _valueSlider = new() { Minimum = 0.25, Maximum = 1.0, TickFrequency = 0.05, IsSnapToTickEnabled = false };
    private readonly TextBlock _status = new();
    private Point? _dragStart;
    private double _dragOriginX;
    private double _dragOriginY;
    private double _hue;
    private double _saturation;
    private double _value = 1.0;

    public string SourcePath { get; private set; }
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public double Zoom { get; private set; } = 1.0;
    public string MaterialPreset { get; private set; } = "Classic";
    public string SkinPreset => CustomDeckCoverBuilder.EncodeStyle(MaterialPreset, TintHex);
    public string TintHex { get; private set; } = "#FFFFFF";

    public CustomDeckCoverEditorWindow(
        string sourcePath,
        double offsetX = 0,
        double offsetY = 0,
        double zoom = 1,
        string materialPreset = "Classic",
        string tintHex = "#FFFFFF")
    {
        SourcePath = Path.GetFullPath(sourcePath);
        OffsetX = offsetX;
        OffsetY = offsetY;
        Zoom = Math.Clamp(zoom, 0.25, 6.0);

        if (materialPreset.Contains('|', StringComparison.Ordinal))
        {
            CustomDeckCoverBuilder.DecodeStyle(materialPreset, out string decodedMaterial, out string decodedTint);
            materialPreset = decodedMaterial;
            if (string.IsNullOrWhiteSpace(tintHex) || tintHex.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase))
                tintHex = decodedTint;
        }

        MaterialPreset = CustomDeckCoverBuilder.MaterialPresets.Contains(materialPreset, StringComparer.OrdinalIgnoreCase)
            ? CustomDeckCoverBuilder.MaterialPresets.First(value => value.Equals(materialPreset, StringComparison.OrdinalIgnoreCase))
            : "Classic";
        TintHex = CustomDeckCoverBuilder.NormalizeTintHex(tintHex);
        SetHsvFromHex(TintHex);

        Title = "Редактор своей обложки";
        Width = 960;
        Height = 760;
        MinWidth = 820;
        MinHeight = 650;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;

        Content = BuildUi();
        Loaded += (_, _) =>
        {
            _colorWheel.Source = BuildColorWheel();
            _valueSlider.Value = _value;
            RefreshColorUi(updatePreview: false);
            RefreshPreview();
        };
    }

    private UIElement BuildUi()
    {
        Grid root = new() { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        StackPanel header = new();
        header.Children.Add(new TextBlock
        {
            Text = "Своя обложка колоды",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = "ЛКМ — двигать изображение; колесо над обложкой — масштаб. Цветовой круг меняет только тон материала корпуса.",
            Margin = new Thickness(0, 4, 0, 12),
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(header);

        Grid body = new();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        Border previewBorder = new()
        {
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            Background = Brushes.Black,
            Padding = new Thickness(10),
            ClipToBounds = true
        };
        previewBorder.Child = _preview;
        previewBorder.MouseLeftButtonDown += Preview_MouseLeftButtonDown;
        previewBorder.MouseMove += Preview_MouseMove;
        previewBorder.MouseLeftButtonUp += Preview_MouseLeftButtonUp;
        previewBorder.MouseWheel += Preview_MouseWheel;
        body.Children.Add(previewBorder);

        ScrollViewer toolsScroll = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(14, 0, 0, 0)
        };
        Grid.SetColumn(toolsScroll, 1);
        body.Children.Add(toolsScroll);

        StackPanel tools = new();
        toolsScroll.Content = tools;

        tools.Children.Add(Label("Материал корпуса"));
        _materialBox.ItemsSource = CustomDeckCoverBuilder.MaterialPresets;
        _materialBox.SelectedItem = MaterialPreset;
        _materialBox.SelectionChanged += (_, _) =>
        {
            MaterialPreset = _materialBox.SelectedItem as string ?? "Classic";
            RefreshPreview();
        };
        tools.Children.Add(_materialBox);

        tools.Children.Add(new TextBlock
        {
            Text = "Цвет материала",
            Margin = new Thickness(0, 14, 0, 6),
            FontWeight = FontWeights.SemiBold
        });

        Border wheelBorder = new()
        {
            Width = ColorWheelSize + 2,
            Height = ColorWheelSize + 2,
            BorderBrush = SystemColors.ControlDarkBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = _colorWheel
        };
        _colorWheel.MouseLeftButtonDown += ColorWheel_MousePick;
        _colorWheel.MouseMove += ColorWheel_MouseMove;
        tools.Children.Add(wheelBorder);

        tools.Children.Add(new TextBlock
        {
            Text = "Яркость",
            Margin = new Thickness(0, 8, 0, 2),
            Opacity = 0.78
        });
        _valueSlider.ValueChanged += (_, _) =>
        {
            _value = _valueSlider.Value;
            RefreshColorUi(updatePreview: true);
        };
        tools.Children.Add(_valueSlider);

        Grid colorRow = new() { Margin = new Thickness(0, 8, 0, 0) };
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _colorSwatch.Margin = new Thickness(0, 0, 8, 0);
        colorRow.Children.Add(_colorSwatch);
        _colorText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_colorText, 1);
        colorRow.Children.Add(_colorText);
        tools.Children.Add(colorRow);

        Button resetColor = Button("Сбросить цвет");
        resetColor.Click += (_, _) =>
        {
            _hue = 0;
            _saturation = 0;
            _value = 1;
            _valueSlider.Value = 1;
            RefreshColorUi(updatePreview: true);
        };
        tools.Children.Add(resetColor);

        Button changeImage = Button("Сменить изображение…");
        changeImage.Click += ChangeImage_Click;
        tools.Children.Add(changeImage);

        Button center = Button("По центру");
        center.Click += (_, _) => ResetView();
        tools.Children.Add(center);

        Button zoomIn = Button("Увеличить +");
        zoomIn.Click += (_, _) => ChangeZoom(1.12);
        tools.Children.Add(zoomIn);

        Button zoomOut = Button("Уменьшить −");
        zoomOut.Click += (_, _) => ChangeZoom(1.0 / 1.12);
        tools.Children.Add(zoomOut);

        Button reset = Button("Подогнать заново");
        reset.Click += (_, _) =>
        {
            OffsetX = 0;
            OffsetY = 0;
            Zoom = 1;
            RefreshPreview();
        };
        tools.Children.Add(reset);

        _status.Margin = new Thickness(0, 14, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Opacity = 0.75;
        tools.Children.Add(_status);

        DockPanel footer = new() { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Button cancel = new() { Content = "Отмена", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(cancel);

        Button use = new()
        {
            Content = "Использовать",
            MinWidth = 120,
            FontWeight = FontWeights.SemiBold
        };
        use.Click += (_, _) => DialogResult = true;
        DockPanel.SetDock(use, Dock.Right);
        footer.Children.Add(use);

        return root;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 0, 0, 5),
        FontWeight = FontWeights.SemiBold
    };

    private static Button Button(string text) => new()
    {
        Content = text,
        Margin = new Thickness(0, 10, 0, 0),
        Padding = new Thickness(8, 5, 8, 5),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private void ChangeImage_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new()
        {
            Title = "Выберите изображение для обложки",
            Filter = "Изображения (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(SourcePath)) ? Path.GetDirectoryName(SourcePath) : null
        };
        if (dialog.ShowDialog(this) != true)
            return;

        SourcePath = Path.GetFullPath(dialog.FileName);
        OffsetX = 0;
        OffsetY = 0;
        Zoom = 1;
        RefreshPreview();
    }

    private void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount > 1)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        _dragStart = e.GetPosition((IInputElement)sender);
        _dragOriginX = OffsetX;
        _dragOriginY = OffsetY;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point now = e.GetPosition((IInputElement)sender);
        Vector delta = now - _dragStart.Value;
        double scaleX = 512.0 / Math.Max(1.0, ((FrameworkElement)sender).ActualWidth);
        double scaleY = 512.0 / Math.Max(1.0, ((FrameworkElement)sender).ActualHeight);
        OffsetX = _dragOriginX + delta.X * scaleX;
        OffsetY = _dragOriginY + delta.Y * scaleY;
        RefreshPreview();
        e.Handled = true;
    }

    private void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ChangeZoom(e.Delta > 0 ? 1.10 : 1.0 / 1.10);
        e.Handled = true;
    }

    private void ColorWheel_MousePick(object sender, MouseButtonEventArgs e)
    {
        PickColorFromWheel(e.GetPosition(_colorWheel));
        _colorWheel.CaptureMouse();
        e.Handled = true;
    }

    private void ColorWheel_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_colorWheel.IsMouseCaptured)
                _colorWheel.ReleaseMouseCapture();
            return;
        }

        PickColorFromWheel(e.GetPosition(_colorWheel));
        e.Handled = true;
    }

    private void PickColorFromWheel(Point point)
    {
        double radius = ColorWheelSize / 2.0;
        double dx = point.X - radius;
        double dy = point.Y - radius;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance > radius)
            return;

        _saturation = Math.Clamp(distance / radius, 0, 1);
        _hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (_hue < 0)
            _hue += 360;
        RefreshColorUi(updatePreview: true);
    }

    private void RefreshColorUi(bool updatePreview)
    {
        Color color = HsvToRgb(_hue, _saturation, _value);
        TintHex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _colorSwatch.Background = new SolidColorBrush(color);
        _colorText.Text = TintHex;
        if (updatePreview)
            RefreshPreview();
    }

    private static BitmapSource BuildColorWheel()
    {
        int width = ColorWheelSize;
        int height = ColorWheelSize;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        double radius = width / 2.0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double dx = x + 0.5 - radius;
                double dy = y + 0.5 - radius;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                if (distance > radius)
                    continue;

                double saturation = Math.Clamp(distance / radius, 0, 1);
                double hue = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                if (hue < 0)
                    hue += 360;
                Color color = HsvToRgb(hue, saturation, 1);
                int index = (y * width + x) * 4;
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = 255;
            }
        }

        WriteableBitmap bitmap = new(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        bitmap.Freeze();
        return bitmap;
    }

    private void ChangeZoom(double multiplier)
    {
        Zoom = Math.Clamp(Zoom * multiplier, 0.25, 6.0);
        RefreshPreview();
    }

    private void ResetView()
    {
        OffsetX = 0;
        OffsetY = 0;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        try
        {
            BitmapSource image = CustomDeckCoverBuilder.RenderPreview(
                SourcePath,
                OffsetX,
                OffsetY,
                Zoom,
                MaterialPreset,
                TintHex);
            _preview.Source = image;
            _status.Text =
                $"{Path.GetFileName(SourcePath)}\n" +
                $"X: {OffsetX:0.0}  Y: {OffsetY:0.0}\n" +
                $"Zoom: {Zoom:0.00}×\n" +
                $"Материал: {MaterialPreset}\n" +
                $"Цвет: {TintHex}";
        }
        catch (Exception exception)
        {
            _preview.Source = null;
            _status.Text = "Не удалось построить предпросмотр:\n" + exception.Message;
        }
    }

    private void SetHsvFromHex(string hex)
    {
        string text = CustomDeckCoverBuilder.NormalizeTintHex(hex).TrimStart('#');
        byte r = Convert.ToByte(text[..2], 16);
        byte g = Convert.ToByte(text.Substring(2, 2), 16);
        byte b = Convert.ToByte(text.Substring(4, 2), 16);
        RgbToHsv(r, g, b, out _hue, out _saturation, out _value);
    }

    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        double m = value - chroma;

        double r;
        double g;
        double b;
        if (hue < 60)
            (r, g, b) = (chroma, x, 0.0);
        else if (hue < 120)
            (r, g, b) = (x, chroma, 0.0);
        else if (hue < 180)
            (r, g, b) = (0.0, chroma, x);
        else if (hue < 240)
            (r, g, b) = (0.0, x, chroma);
        else if (hue < 300)
            (r, g, b) = (x, 0.0, chroma);
        else
            (r, g, b) = (chroma, 0.0, x);

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static void RgbToHsv(byte red, byte green, byte blue, out double hue, out double saturation, out double value)
    {
        double r = red / 255.0;
        double g = green / 255.0;
        double b = blue / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        if (delta < 0.000001)
            hue = 0;
        else if (Math.Abs(max - r) < 0.000001)
            hue = 60 * (((g - b) / delta) % 6);
        else if (Math.Abs(max - g) < 0.000001)
            hue = 60 * (((b - r) / delta) + 2);
        else
            hue = 60 * (((r - g) / delta) + 4);

        if (hue < 0)
            hue += 360;
        saturation = max <= 0 ? 0 : delta / max;
        value = max;
    }
}
