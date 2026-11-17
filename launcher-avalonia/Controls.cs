// The launcher's own controls on Linux and macOS, drawn like their Windows twins (launcher/Theme.cs and
// launcher/Widgets.cs): rounded buttons, the value picker, switch, slider, progress line, cards, status marks,
// toolbar buttons, the view switch, console tabs, the search field and the message bars.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PortLauncher {

// A control that paints itself and knows when the mouse is over it or pressing it.
abstract class Drawn : Control {
  protected bool hover, down;
  public event EventHandler Click;
  protected Drawn() { Cursor = new Cursor(StandardCursorType.Hand); ClipToBounds = true; }
  protected override void OnPointerEntered(PointerEventArgs e) { hover = true; InvalidateVisual(); base.OnPointerEntered(e); }
  protected override void OnPointerExited(PointerEventArgs e) { hover = down = false; InvalidateVisual(); base.OnPointerExited(e); }
  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { down = true; InvalidateVisual(); }
    base.OnPointerPressed(e);
  }
  protected override void OnPointerReleased(PointerReleasedEventArgs e) {
    bool was = down;
    down = false;
    InvalidateVisual();
    base.OnPointerReleased(e);
    if (was && IsEffectivelyEnabled && new Rect(Bounds.Size).Contains(e.GetPosition(this))) OnClick();
  }
  protected override void OnKeyDown(KeyEventArgs e) {
    if ((e.Key == Key.Enter || e.Key == Key.Space) && Focusable) { OnClick(); e.Handled = true; }
    base.OnKeyDown(e);
  }
  protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
    base.OnPropertyChanged(change);
    if (change.Property == IsEnabledProperty || change.Property == IsFocusedProperty) InvalidateVisual();
  }
  protected virtual void OnClick() => Click?.Invoke(this, EventArgs.Empty);
  public void PerformClick() => OnClick();
  protected Rect R => new Rect(Bounds.Size);
}

enum ButtonKind { Primary, Blue, Secondary, Danger, Ghost }

sealed class FlatButton : Drawn {
  public ButtonKind Kind;
  string text;
  public double FontPt = 10.5;
  public Action<DrawingContext> Overlay;
  public string Text { get => text; set { text = value; InvalidateVisual(); } }

  public FlatButton(string text, ButtonKind kind, double width, double height) {
    this.text = text; Kind = kind; Width = width; Height = height; Focusable = true;
  }

  public override void Render(DrawingContext g) {
    Color fill, fg = Colors.White, border = Colors.Transparent;
    switch (Kind) {
      case ButtonKind.Primary: fill = hover ? Look.AccentHover : Look.Accent; fg = Look.OnAccent; break;
      case ButtonKind.Blue: fill = hover ? Look.BlueHover : Look.Blue; break;
      case ButtonKind.Danger: fill = hover ? Look.DangerHover : Look.Danger; break;
      case ButtonKind.Ghost: fill = hover ? Look.Surface2 : Colors.Transparent; fg = Look.Text2; break;
      default: fill = hover ? Look.Hover : Look.Surface2; fg = Look.Text; border = Look.Border; break;
    }
    if (!IsEffectivelyEnabled) { fill = Look.Disabled; fg = Look.DisabledText; border = Colors.Transparent; }
    if (down && IsEffectivelyEnabled) fill = Color.FromRgb((byte)(fill.R * 0.92), (byte)(fill.G * 0.92), (byte)(fill.B * 0.92));
    var r = R.Deflate(0.5);
    Look.FillRound(g, fill, r, 7);
    if (border.A > 0) Look.StrokeRound(g, border, r, 7);
    if (IsFocused && IsEffectivelyEnabled && IsKeyboardFocusWithin) Look.StrokeRound(g, Look.Text, r, 7, 1.5);
    Look.DrawCentered(g, text, Look.Semi, FontPt, fg, R.Deflate(new Thickness(6, 0)));
    Overlay?.Invoke(g);
  }
}

// A value picker: shows the current choice, opens a list on click; left/right arrows step.
sealed class ChoiceBox : Drawn {
  public sealed class Entry { public int Value; public string Text; }
  public readonly List<Entry> Items = new List<Entry>();
  int index = -1;
  public event EventHandler SelectedIndexChanged;
  public object Tag2;

  public ChoiceBox() { Width = 360; Height = 36; Focusable = true; }

  public int SelectedIndex {
    get => index;
    set { int v = Items.Count == 0 ? -1 : Math.Max(0, Math.Min(Items.Count - 1, value)); if (v != index) { index = v; InvalidateVisual(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); } }
  }
  public Entry Selected => index >= 0 && index < Items.Count ? Items[index] : null;

  public void Select(int value, int fallback) {
    int pick = Items.FindIndex(i => i.Value == value);
    if (pick < 0) pick = Items.FindIndex(i => i.Value == fallback);
    SelectedIndex = pick >= 0 ? pick : 0;
  }

  protected override void OnKeyDown(KeyEventArgs e) {
    if (e.Key == Key.Left && index > 0) { SelectedIndex = index - 1; e.Handled = true; }
    else if (e.Key == Key.Right && index < Items.Count - 1) { SelectedIndex = index + 1; e.Handled = true; }
    else base.OnKeyDown(e);
  }

  protected override void OnClick() {
    var menu = new ContextMenu { MinWidth = Bounds.Width, Placement = PlacementMode.Bottom, PlacementTarget = this };
    for (int i = 0; i < Items.Count; ++i) {
      int pick = i;
      var item = new MenuItem { Header = Items[i].Text, ToggleType = MenuItemToggleType.CheckBox, IsChecked = i == index };
      item.Click += (s, e) => SelectedIndex = pick;
      menu.Items.Add(item);
    }
    menu.Open(this);
  }

  public override void Render(DrawingContext g) {
    var r = R.Deflate(0.5);
    Look.FillRound(g, !IsEffectivelyEnabled ? Look.Disabled : hover ? Look.Hover : Look.Surface2, r, 7);
    Look.StrokeRound(g, IsFocused ? Look.Accent : Look.Border, r, 7, IsFocused ? 1.5 : 1);
    Look.DrawLeft(g, Selected?.Text ?? "", Look.UI(), 10, IsEffectivelyEnabled ? Look.Text : Look.DisabledText, new Rect(12, 0, Bounds.Width - 44, Bounds.Height));
    double cx = Bounds.Width - 20, cy = Bounds.Height / 2;
    var pen = Look.RoundPen(IsEffectivelyEnabled ? Look.Text2 : Look.DisabledText, 1.8);
    g.DrawLine(pen, new Point(cx - 5, cy - 2.5), new Point(cx, cy + 2.5));
    g.DrawLine(pen, new Point(cx, cy + 2.5), new Point(cx + 5, cy - 2.5));
  }
}

// An on/off switch with its state written next to it.
sealed class ToggleSwitch : Drawn {
  bool on;
  public string OnText = "On", OffText = "Off";
  public event EventHandler CheckedChanged;
  public ToggleSwitch() { Width = 200; Height = 36; Focusable = true; }
  public bool Checked { get => on; set { if (value != on) { on = value; InvalidateVisual(); CheckedChanged?.Invoke(this, EventArgs.Empty); } } }
  protected override void OnClick() => Checked = !on;
  public override void Render(DrawingContext g) {
    var track = new Rect(1, (Bounds.Height - 24) / 2, 44, 24);
    Look.FillRound(g, !IsEffectivelyEnabled ? Look.Disabled : on ? Look.Accent : Look.Border, track, 12);
    if (IsFocused) Look.StrokeRound(g, Look.Text, track, 12, 1.5);
    double knob = on ? track.Right - 21 : track.X + 3;
    g.DrawEllipse(Look.B(IsEffectivelyEnabled ? Colors.White : Look.DisabledText), null, new Point(knob + 9, track.Y + 12), 9, 9);
    Look.DrawLeft(g, on ? OnText : OffText, Look.UI(), 10, IsEffectivelyEnabled ? Look.Text : Look.DisabledText, new Rect(56, 0, Bounds.Width - 56, Bounds.Height));
  }
}

// A slider with a filled track and the value at its end.
sealed class SliderBox : Drawn {
  int value, min, max = 100, step = 5;
  bool drag;
  public string Suffix = "%";
  public event EventHandler ValueChanged;
  public SliderBox() { Width = 360; Height = 36; Focusable = true; }
  public void Range(int minimum, int maximum, int stepSize) { min = minimum; max = maximum; step = Math.Max(1, stepSize); }
  public int Value {
    get => value;
    set { int v = Math.Max(min, Math.Min(max, value)); if (v != this.value) { this.value = v; InvalidateVisual(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
  }
  Rect Track => new Rect(10, Bounds.Height / 2 - 3, Math.Max(10, Bounds.Width - 80), 6);
  void SetFromX(double x) {
    var t = Track;
    double f = Math.Max(0, Math.Min(1, (x - t.X) / t.Width));
    Value = (int)Math.Round((min + f * (max - min)) / step) * step;
  }
  protected override void OnPointerPressed(PointerPressedEventArgs e) { Focus(); if (IsEffectivelyEnabled) { drag = true; SetFromX(e.GetPosition(this).X); e.Pointer.Capture(this); } base.OnPointerPressed(e); }
  protected override void OnPointerMoved(PointerEventArgs e) { if (drag) SetFromX(e.GetPosition(this).X); base.OnPointerMoved(e); }
  protected override void OnPointerReleased(PointerReleasedEventArgs e) { drag = false; e.Pointer.Capture(null); base.OnPointerReleased(e); }
  protected override void OnClick() { }
  protected override void OnKeyDown(KeyEventArgs e) {
    if (e.Key == Key.Left) { Value -= step; e.Handled = true; }
    else if (e.Key == Key.Right) { Value += step; e.Handled = true; }
    else base.OnKeyDown(e);
  }
  public override void Render(DrawingContext g) {
    var t = Track;
    double f = max > min ? (value - min) / (double)(max - min) : 0;
    Look.FillRound(g, Look.Border, t, 3);
    Look.FillRound(g, IsEffectivelyEnabled ? Look.Accent : Look.DisabledText, new Rect(t.X, t.Y, Math.Max(6, t.Width * f), t.Height), 3);
    double kx = t.X + t.Width * f, cy = Bounds.Height / 2;
    g.DrawEllipse(Look.B(IsEffectivelyEnabled ? Colors.White : Look.DisabledText), null, new Point(kx, cy), 9, 9);
    if (IsFocused) g.DrawEllipse(null, Look.P(Look.Accent, 2), new Point(kx, cy), 10, 10);
    var ft = Look.Format(value + Suffix, Look.Semi, 10, IsEffectivelyEnabled ? Look.Text : Look.DisabledText, 60, TextAlignment.Right);
    g.DrawText(ft, new Point(Bounds.Width - 60, (Bounds.Height - ft.Height) / 2));
  }
}

// A thin progress bar in the accent colour.
sealed class ProgressLine : Control {
  int value, maximum = 1;
  public ProgressLine() { Height = 6; }
  public int Maximum { get => maximum; set { maximum = Math.Max(1, value); InvalidateVisual(); } }
  public int Value { get => value; set { this.value = Math.Max(0, Math.Min(maximum, value)); InvalidateVisual(); } }
  public override void Render(DrawingContext g) {
    var r = new Rect(Bounds.Size);
    Look.FillRound(g, Look.Surface2, r, r.Height / 2);
    if (value > 0) Look.FillRound(g, Look.Accent, new Rect(0, 0, Math.Max(r.Height, r.Width * value / maximum), r.Height), r.Height / 2);
  }
}

// A status mark: a filled circle with a check, a cross or a dot while checking.
sealed class StatusDot : Control {
  public bool? Ok;
  public StatusDot() { Width = 26; Height = 26; }
  public override void Render(DrawingContext g) {
    var color = Ok == null ? Look.Text3 : Ok == true ? Look.Accent : Look.Danger;
    double c = Bounds.Width / 2;
    g.DrawEllipse(Look.B(Look.A(48, color)), null, new Point(c, c), c - 1, c - 1);
    var pen = Look.RoundPen(color, 2.2);
    if (Ok == true) { g.DrawLine(pen, new Point(c - 5, c), new Point(c - 1.5, c + 3.5)); g.DrawLine(pen, new Point(c - 1.5, c + 3.5), new Point(c + 5.5, c - 4)); }
    else if (Ok == false) { g.DrawLine(pen, new Point(c - 4, c - 4), new Point(c + 4, c + 4)); g.DrawLine(pen, new Point(c + 4, c - 4), new Point(c - 4, c + 4)); }
    else g.DrawEllipse(Look.B(color), null, new Point(c, c), 3, 3);
  }
}

// The state pill: "● Ready" in a tinted rounded label.
sealed class PillLabel : Control {
  string text = "";
  Color color = Colors.Gray;
  public PillLabel() { Height = 26; HorizontalAlignment = HorizontalAlignment.Left; }
  public void Set(string t, Color c) { text = t; color = c; InvalidateMeasure(); InvalidateVisual(); }
  protected override Size MeasureOverride(Size available) => new Size(Pill.Width(text, false) + 2, 26);
  public override void Render(DrawingContext g) => Pill.Draw(g, text, color, new Point(0, Bounds.Height / 2), false);
}

static class Pill {
  public static double Width(string text, bool solid) => 10 + 6 + 6 + Look.Measure(text, Look.Semi, solid ? 8 : 8.75).Width + 11;

  // solid: drawn over a cover (dark backing for contrast).
  public static void Draw(DrawingContext g, string text, Color color, Point leftMiddle, bool solid) {
    double pts = solid ? 8 : 8.75;
    var ft = Look.Format(text, Look.Semi, pts, solid ? Color.FromRgb(236, 238, 242) : (Look.Dark ? Lighten(color) : color));
    double h = solid ? 20 : 24, dot = 6, padL = 10, padR = 11;
    var r = new Rect(leftMiddle.X, leftMiddle.Y - h / 2, padL + dot + 6 + ft.WidthIncludingTrailingWhitespace + padR, h);
    Look.FillRound(g, solid ? Color.FromArgb(215, 14, 16, 20) : Look.A(Look.Dark ? 40 : 30, color), r, h / 2);
    g.DrawEllipse(Look.B(solid ? Lighten(color) : color), null, new Point(r.X + padL + dot / 2, r.Y + h / 2), dot / 2, dot / 2);
    g.DrawText(ft, new Point(r.X + padL + dot + 6, r.Y + (h - ft.Height) / 2));
  }

  public static Color Lighten(Color c) => c == Look.Text3 ? Color.FromRgb(180, 186, 196)
      : Color.FromRgb((byte)Math.Min(255, c.R + 30), (byte)Math.Min(255, c.G + 30), (byte)Math.Min(255, c.B + 30));
}

// A toolbar button: an icon over a short label, like an emulator's main toolbar.
sealed class ToolButton : Drawn {
  readonly Glyph glyph;
  string text;
  public Color? Tint;
  public bool Checked;
  public string Text { get => text; set { text = value; InvalidateVisual(); } }
  public ToolButton(Glyph glyph, string text, double width = 74) {
    this.glyph = glyph; this.text = text;
    Width = Math.Max(width, Look.Measure(text, Look.UI(), 8.75).Width + 18);
    Height = 58;
    Margin = new Thickness(1, 0);
  }
  public override void Render(DrawingContext g) {
    bool on = IsEffectivelyEnabled;
    if (on && (hover || Checked)) Look.FillRound(g, down ? Look.Border : Checked ? Look.Surface2 : Look.Hover, new Rect(2, 3, Bounds.Width - 4, Bounds.Height - 6), 8);
    var color = !on ? Look.DisabledText : Tint ?? (hover ? Look.Text : Look.Text2);
    double s = 22;
    Glyphs.Draw(g, glyph, new Rect((Bounds.Width - s) / 2, 8, s, s), color);
    var ft = Look.Format(text, Look.UI(), 8.75, on ? (hover ? Look.Text : Look.Text2) : Look.DisabledText, Bounds.Width, TextAlignment.Center);
    g.DrawText(ft, new Point(0, 33));
  }
}

// A thin vertical line between toolbar groups.
sealed class ToolSeparator : Control {
  public ToolSeparator() { Width = 17; Height = 58; }
  public override void Render(DrawingContext g) =>
      g.DrawLine(Look.P(Look.Border), new Point(Math.Floor(Bounds.Width / 2) + 0.5, Bounds.Height * 0.22), new Point(Math.Floor(Bounds.Width / 2) + 0.5, Bounds.Height * 0.78));
}

// Two icon buttons side by side: list or grid.
sealed class ViewSwitch : Drawn {
  public bool Grid;
  int hot = -1;
  public event EventHandler Changed;
  public ViewSwitch() { Width = 76; Height = 34; }
  public void Set(bool grid) { if (grid != Grid) { Grid = grid; InvalidateVisual(); Changed?.Invoke(this, EventArgs.Empty); } }
  protected override void OnPointerMoved(PointerEventArgs e) { int h = e.GetPosition(this).X < Bounds.Width / 2 ? 0 : 1; if (h != hot) { hot = h; InvalidateVisual(); } base.OnPointerMoved(e); }
  protected override void OnPointerExited(PointerEventArgs e) { hot = -1; base.OnPointerExited(e); }
  protected override void OnPointerPressed(PointerPressedEventArgs e) { Set(e.GetPosition(this).X >= Bounds.Width / 2); base.OnPointerPressed(e); }
  protected override void OnClick() { }
  public override void Render(DrawingContext g) {
    var outer = R.Deflate(0.5);
    Look.FillRound(g, Look.Bg, outer, 8);
    Look.StrokeRound(g, Look.Border, outer, 8);
    for (int i = 0; i < 2; ++i) {
      bool on = (i == 1) == Grid;
      var cell = new Rect(3 + i * (Bounds.Width / 2 - 3), 3, Bounds.Width / 2 - 3, Bounds.Height - 6);
      if (on || hot == i) Look.FillRound(g, on ? Look.Surface2 : Look.Hover, cell, 6);
      double s = 18;
      Glyphs.Draw(g, i == 0 ? Glyph.List : Glyph.Grid, new Rect(cell.X + (cell.Width - s) / 2, cell.Y + (cell.Height - s) / 2, s, s), on ? Look.Accent : Look.Text3);
    }
  }
}

// The console tabs above the library: "All" and one tab per console that has games, each with its count.
sealed class ConsoleTabs : Drawn {
  public sealed class Tab { public string Id, Text; public int Count; }
  public List<Tab> Tabs = new List<Tab>();
  public string Selected;
  int hot = -1;
  readonly List<Rect> rects = new List<Rect>();
  public event EventHandler Changed;
  public ConsoleTabs() { Height = 44; }
  public void Set(List<Tab> tabs, string selected) { Tabs = tabs; Selected = tabs.Any(t => t.Id == selected) ? selected : null; InvalidateVisual(); }
  int HitTest(Point p) { for (int i = 0; i < rects.Count; ++i) if (rects[i].Contains(p)) return i; return -1; }
  protected override void OnPointerMoved(PointerEventArgs e) { int h = HitTest(e.GetPosition(this)); if (h != hot) { hot = h; InvalidateVisual(); } base.OnPointerMoved(e); }
  protected override void OnPointerExited(PointerEventArgs e) { hot = -1; base.OnPointerExited(e); }
  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    int h = HitTest(e.GetPosition(this));
    if (h >= 0 && h < Tabs.Count && Tabs[h].Id != Selected) { Selected = Tabs[h].Id; InvalidateVisual(); Changed?.Invoke(this, EventArgs.Empty); }
    base.OnPointerPressed(e);
  }
  protected override void OnClick() { }
  public override void Render(DrawingContext g) {
    g.FillRectangle(Look.B(Look.Bg), R);
    rects.Clear();
    double x = 18, h = Bounds.Height;
    for (int i = 0; i < Tabs.Count; ++i) {
      var t = Tabs[i];
      string text = t.Text + "  " + t.Count;
      var size = Look.Measure(text, Look.Semi, 10);
      var r = new Rect(x, 0, size.Width + 30, h);
      rects.Add(r);
      bool on = t.Id == Selected;
      if (hot == i && !on) Look.FillRound(g, Look.Hover, new Rect(r.X, 6, r.Width, h - 14), 6);
      Look.DrawCentered(g, text, Look.Semi, 10, on ? Look.Text : Look.Text3, new Rect(r.X, 0, r.Width, h - 3));
      if (on) g.FillRectangle(Look.B(Look.Accent), new Rect(r.X + 8, h - 3, r.Width - 16, 3));
      x += r.Width + 4;
    }
    g.DrawLine(Look.P(Look.Border), new Point(0, h - 0.5), new Point(Bounds.Width, h - 0.5));
  }
}

// A rounded search field with a magnifier and a hint.
sealed class SearchBox : Grid {
  public readonly TextBox Box;
  public SearchBox(string hint) {
    Height = 34;
    Box = new TextBox {
      Watermark = hint, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Look.B(Look.Text),
      FontSize = Look.Pt(10), Margin = new Thickness(34, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
      MinHeight = 0, VerticalContentAlignment = VerticalAlignment.Center,
    };
    Box.Resources["TextControlBackgroundFocused"] = Brushes.Transparent;
    Box.Resources["TextControlBackgroundPointerOver"] = Brushes.Transparent;
    Box.Resources["TextControlBorderBrushFocused"] = Brushes.Transparent;
    Box.Resources["TextControlBorderBrushPointerOver"] = Brushes.Transparent;
    var back = new Painter((g, size) => {
      var r = new Rect(size).Deflate(0.5);
      Look.FillRound(g, Look.Bg, r, size.Height / 2 - 1);
      Look.StrokeRound(g, Box.IsFocused ? Look.Accent : Look.Border, r, size.Height / 2 - 1, Box.IsFocused ? 1.5 : 1);
      double s = 16;
      Glyphs.Draw(g, Glyph.Search, new Rect(12, (size.Height - s) / 2, s, s), Look.Text3);
    });
    Box.GotFocus += (s, e) => back.InvalidateVisual();
    Box.LostFocus += (s, e) => back.InvalidateVisual();
    Children.Add(back);
    Children.Add(Box);
    Cursor = new Cursor(StandardCursorType.Ibeam);
  }
}

// A control that draws with a function (the background of panels, which cannot draw themselves).
sealed class Painter : Control {
  readonly Action<DrawingContext, Size> draw;
  public Painter(Action<DrawingContext, Size> draw) { this.draw = draw; IsHitTestVisible = false; }
  public override void Render(DrawingContext g) => draw(g, Bounds.Size);
}

// A message strip under the toolbar: an icon, a sentence and up to two buttons.
sealed class InfoBar : Grid {
  readonly TextBlock text;
  readonly StackPanel buttons;
  readonly Painter back;
  Glyph icon = Glyph.Update;
  Color tone;
  public InfoBar() {
    Height = 50; IsVisible = false; tone = Look.Blue;
    buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    text = new TextBlock { Foreground = Look.B(Look.Text), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(52, 0, 12, 0), FontSize = Look.Pt(10) };
    back = new Painter((g, size) => {
      var r = new Rect(size);
      g.FillRectangle(Look.B(Look.Surface), r);
      g.FillRectangle(Look.B(Look.A(Look.Dark ? 34 : 26, tone)), r);
      g.FillRectangle(Look.B(tone), new Rect(0, 0, 3, r.Height));
      g.DrawLine(Look.P(Look.Border), new Point(0, r.Height - 0.5), new Point(r.Width, r.Height - 0.5));
      double s = 20;
      Glyphs.Draw(g, icon, new Rect(20, (r.Height - s) / 2, s, s), tone);
    });
    var dock = new DockPanel();
    DockPanel.SetDock(buttons, Dock.Right);
    dock.Children.Add(buttons);
    dock.Children.Add(text);
    Children.Add(back);
    Children.Add(dock);
  }
  public void Show(Glyph glyph, Color color, string message, params KeyValuePair<string, Action>[] actions) {
    icon = glyph; tone = color; text.Text = message;
    buttons.Children.Clear();
    foreach (var a in actions) {
      var b = new FlatButton(a.Key, buttons.Children.Count == 0 ? ButtonKind.Primary : ButtonKind.Secondary, Look.Measure(a.Key, Look.Semi, 10).Width + 34, 34) { FontPt = 10, Margin = new Thickness(8, 0, 0, 0) };
      var act = a.Value;
      b.Click += (s, e) => act();
      buttons.Children.Add(b);
    }
    IsVisible = true;
    back.InvalidateVisual();
  }
}

// A rounded card that holds other controls.
sealed class Card : Border {
  public Card() {
    Background = Look.B(Look.Surface); BorderBrush = Look.B(Look.Border); BorderThickness = new Thickness(1);
    CornerRadius = new CornerRadius(10); Padding = new Thickness(22, 18, 22, 20);
  }
}

}
