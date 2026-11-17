// The launcher's look on Linux and macOS: the same palette, sizes and line icons as the Windows launcher
// (launcher/Theme.cs, launcher/Widgets.cs), drawn with Avalonia. Sizes given in points like the Windows code
// (Look.Pt converts to Avalonia's device-independent pixels).
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace PortLauncher {

static class Look {
  public static Color Bg, Surface, Surface2, Hover, Border, Text, Text2, Text3, Accent, AccentHover, OnAccent,
      Blue, BlueHover, Danger, DangerHover, Disabled, DisabledText, Link, Warn, Selection;
  public static bool Dark;

  // "system" follows the desktop's light/dark setting, else "dark" or "light" (launcher.ini "tema": 0, 1, 2 like Windows).
  public static string Mode = "system";

  public static bool SystemDark() {
    try {
      var settings = Avalonia.Application.Current?.PlatformSettings;
      if (settings != null) return settings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
    } catch (Exception) { }
    return true;
  }

  public static void Load() {
    int m = Prefs.Int("tema", 0);
    Apply(m == 1 ? "dark" : m == 2 ? "light" : "system");
  }

  public static void Save() => Prefs.Set("tema", Mode == "dark" ? 1 : Mode == "light" ? 2 : 0);

  static Color C(int r, int g, int b) => Color.FromRgb((byte)r, (byte)g, (byte)b);

  public static void Apply(string mode) {
    Mode = mode;
    Dark = mode == "dark" || (mode == "system" && SystemDark());
    if (Dark) {
      Bg = C(15, 17, 21); Surface = C(24, 27, 33); Surface2 = C(33, 37, 45);
      Hover = C(44, 49, 59); Border = C(52, 58, 69);
      Text = C(240, 242, 245); Text2 = C(198, 203, 212); Text3 = C(150, 157, 170);
      Accent = C(70, 184, 110); AccentHover = C(90, 205, 130); OnAccent = C(8, 26, 15);
      Blue = C(64, 122, 230); BlueHover = C(86, 142, 246);
      Danger = C(226, 92, 84); DangerHover = C(240, 114, 106);
      Disabled = C(40, 44, 52); DisabledText = C(118, 125, 137); Link = C(118, 168, 248);
      Warn = C(230, 172, 62); Selection = C(38, 70, 120);
    } else {
      Bg = C(241, 243, 247); Surface = Colors.White; Surface2 = C(240, 242, 246);
      Hover = C(228, 232, 239); Border = C(214, 219, 228);
      Text = C(20, 23, 29); Text2 = C(62, 69, 81); Text3 = C(102, 110, 124);
      Accent = C(28, 150, 80); AccentHover = C(22, 132, 70); OnAccent = Colors.White;
      Blue = C(38, 99, 214); BlueHover = C(30, 86, 192);
      Danger = C(206, 62, 54); DangerHover = C(186, 50, 44);
      Disabled = C(229, 232, 237); DisabledText = C(146, 152, 164); Link = C(30, 96, 210);
      Warn = C(196, 128, 18); Selection = C(206, 224, 250);
    }
  }

  // ---- brushes, pens, fonts

  static readonly Dictionary<uint, IBrush> brushes = new Dictionary<uint, IBrush>();
  public static IBrush B(Color c) {
    if (!brushes.TryGetValue(c.ToUInt32(), out var b)) brushes[c.ToUInt32()] = b = new SolidColorBrush(c).ToImmutable();
    return b;
  }
  public static IPen P(Color c, double width = 1) => new Pen(B(c), width);
  public static IPen RoundPen(Color c, double width) => new Pen(B(c), width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
  public static Color A(int alpha, Color c) => Color.FromArgb((byte)alpha, c.R, c.G, c.B);

  // Point sizes of the Windows launcher (Segoe UI 10 pt) in Avalonia units (96 per inch).
  public static double Pt(double points) => points * 96.0 / 72.0;

  public static readonly FontFamily UiFamily = new FontFamily("fonts:Inter#Inter, $Default");
  public static readonly FontFamily MonoFamily = new FontFamily("DejaVu Sans Mono, Liberation Mono, Menlo, Monaco, Consolas, monospace");
  public static Typeface UI(FontWeight weight = FontWeight.Normal) => new Typeface(UiFamily, FontStyle.Normal, weight);
  public static Typeface Semi => UI(FontWeight.SemiBold);
  public static Typeface Mono => new Typeface(MonoFamily);

  public static FormattedText Format(string text, Typeface face, double points, Color color, double maxWidth = double.PositiveInfinity,
                                   TextAlignment align = TextAlignment.Left, bool wrap = false, double maxHeight = double.PositiveInfinity) {
    var ft = new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Pt(points), B(color)) {
      TextAlignment = align, Trimming = TextTrimming.CharacterEllipsis,
    };
    if (!double.IsInfinity(maxWidth)) ft.MaxTextWidth = Math.Max(1, maxWidth);
    if (!double.IsInfinity(maxHeight)) ft.MaxTextHeight = Math.Max(1, maxHeight);
    else if (!wrap) ft.MaxLineCount = 1;
    return ft;
  }

  // Text centred in a rectangle (one line, ellipsis).
  public static void DrawCentered(DrawingContext g, string text, Typeface face, double points, Color color, Rect r) {
    var ft = Format(text, face, points, color, r.Width, TextAlignment.Center);
    g.DrawText(ft, new Point(r.X, r.Y + (r.Height - ft.Height) / 2));
  }

  public static void DrawLeft(DrawingContext g, string text, Typeface face, double points, Color color, Rect r, bool middle = true) {
    var ft = Format(text, face, points, color, r.Width);
    g.DrawText(ft, new Point(r.X, middle ? r.Y + (r.Height - ft.Height) / 2 : r.Y));
  }

  public static Size Measure(string text, Typeface face, double points) {
    var ft = Format(text, face, points, Colors.Black);
    return new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
  }

  public static void FillRound(DrawingContext g, Color c, Rect r, double radius) => g.DrawRectangle(B(c), null, new RoundedRect(r, Math.Min(radius, Math.Min(r.Width, r.Height) / 2)));
  public static void StrokeRound(DrawingContext g, Color c, Rect r, double radius, double width = 1) =>
      g.DrawRectangle(null, P(c, width), new RoundedRect(r.Deflate(width / 2), Math.Min(radius, Math.Min(r.Width, r.Height) / 2)));
}

enum Glyph { Disc, FolderAdd, Refresh, Play, Stop, Install, Gear, Box, List, Grid, Log, Update, Search, Folder, Image, Close, ChevronDown, ChevronUp }

static class Glyphs {
  // A line icon in a square box (24 units drawn into r), the same shapes as the Windows launcher.
  public static void Draw(DrawingContext g, Glyph glyph, Rect r, Color color, double stroke = 1.7) {
    double k = r.Width / 24.0;
    Point P(double x, double y) => new Point(r.X + x * k, r.Y + y * k);
    var pen = Look.RoundPen(color, Math.Max(1.2, stroke));
    var fill = Look.B(color);
    void Line(double x1, double y1, double x2, double y2) => g.DrawLine(pen, P(x1, y1), P(x2, y2));
    void Lines(params double[] xy) { for (int i = 0; i + 3 < xy.Length; i += 2) Line(xy[i], xy[i + 1], xy[i + 2], xy[i + 3]); }
    void Ellipse(double x, double y, double w, double h, bool filled = false) =>
        g.DrawEllipse(filled ? fill : null, filled ? null : pen, P(x + w / 2, y + h / 2), w / 2 * k, h / 2 * k);
    void Poly(bool filled, bool closed, params double[] xy) {
      var geo = new StreamGeometry();
      using (var c = geo.Open()) {
        c.BeginFigure(P(xy[0], xy[1]), filled);
        for (int i = 2; i + 1 < xy.Length; i += 2) c.LineTo(P(xy[i], xy[i + 1]));
        c.EndFigure(closed);
      }
      g.DrawGeometry(filled ? fill : null, pen, geo);
    }
    // An arc of the ellipse in (x, y, w, h) from start, sweep degrees (GDI+ angles: clockwise from 3 o'clock).
    void Arc(double x, double y, double w, double h, double start, double sweep) {
      double cx = x + w / 2, cy = y + h / 2, rx = w / 2, ry = h / 2;
      var geo = new StreamGeometry();
      using (var c = geo.Open()) {
        double a0 = start * Math.PI / 180, a1 = (start + sweep) * Math.PI / 180;
        c.BeginFigure(P(cx + rx * Math.Cos(a0), cy + ry * Math.Sin(a0)), false);
        c.ArcTo(P(cx + rx * Math.Cos(a1), cy + ry * Math.Sin(a1)), new Size(rx * k, ry * k), 0, Math.Abs(sweep) > 180, SweepDirection.Clockwise);
        c.EndFigure(false);
      }
      g.DrawGeometry(null, pen, geo);
    }
    void RoundBox(double x, double y, double w, double h, double rad, bool filled = false) =>
        g.DrawRectangle(filled ? fill : null, filled ? null : pen, new RoundedRect(new Rect(P(x, y), new Size(w * k, h * k)), rad * k));

    switch (glyph) {
      case Glyph.Disc:
        Ellipse(3, 3, 18, 18); Ellipse(9.5, 9.5, 5, 5); Arc(6, 6, 12, 12, 200, 70);
        break;
      case Glyph.FolderAdd:
      case Glyph.Folder:
        Poly(false, true, 3, 7, 3, 5.5, 4.5, 4, 9, 4, 11, 6.5, 19.5, 6.5, 21, 8, 21, 18.5, 19.5, 20, 4.5, 20, 3, 18.5);
        if (glyph == Glyph.FolderAdd) { Line(12, 10.5, 12, 16.5); Line(9, 13.5, 15, 13.5); }
        break;
      case Glyph.Refresh:
        Arc(4.5, 4.5, 15, 15, -60, 290);
        Lines(15.5, 3.2, 16.3, 6.4, 13.1, 7.4);
        break;
      case Glyph.Play:
        Poly(true, true, 7.5, 4.5, 19.5, 12, 7.5, 19.5);
        break;
      case Glyph.Stop:
        RoundBox(5.5, 5.5, 13, 13, 2.5, true);
        break;
      case Glyph.Install:
        Line(12, 3.5, 12, 14.5);
        Lines(7.5, 10.5, 12, 15, 16.5, 10.5);
        Lines(4, 15.5, 4, 19.5, 20, 19.5, 20, 15.5);
        break;
      case Glyph.Gear: {
        var pts = new List<double>();
        for (int i = 0; i < 32; ++i) {
          double a = i * Math.PI / 16 - Math.PI / 32;
          double rad = (i / 2) % 2 == 0 ? 9.2 : 7.0;
          pts.Add(12 + rad * Math.Cos(a)); pts.Add(12 + rad * Math.Sin(a));
        }
        Poly(false, true, pts.ToArray());
        Ellipse(9, 9, 6, 6);
        break;
      }
      case Glyph.Box:
        Poly(false, true, 12, 3, 20, 7.5, 20, 16.5, 12, 21, 4, 16.5, 4, 7.5);
        Lines(4, 7.5, 12, 12, 20, 7.5);
        Line(12, 12, 12, 21);
        break;
      case Glyph.List:
        for (int i = 0; i < 3; ++i) { double y = 6.5 + i * 5.5; Ellipse(3.5, y - 1.3, 2.6, 2.6, true); Line(9, y, 20.5, y); }
        break;
      case Glyph.Grid:
        foreach (var o in new[] { new Point(4, 4), new Point(13.5, 4), new Point(4, 13.5), new Point(13.5, 13.5) }) RoundBox(o.X, o.Y, 6.5, 6.5, 1.5);
        break;
      case Glyph.Log:
        RoundBox(3.5, 4.5, 17, 15, 2.5);
        Lines(7, 9.5, 9.5, 12, 7, 14.5);
        Line(11.5, 14.5, 16.5, 14.5);
        break;
      case Glyph.Update:
        Arc(4, 4, 16, 16, 30, 300);
        Line(12, 8, 12, 15.5);
        Lines(9, 11, 12, 8, 15, 11);
        break;
      case Glyph.Search:
        Ellipse(4, 4, 11.5, 11.5);
        Line(14, 14, 19.5, 19.5);
        break;
      case Glyph.Image:
        RoundBox(3.5, 4.5, 17, 15, 2.5);
        Lines(4, 17, 9.5, 11.5, 13, 15, 15.5, 12.5, 20, 17);
        Ellipse(14.5, 7.5, 3, 3, true);
        break;
      case Glyph.Close:
        Line(7, 7, 17, 17); Line(17, 7, 7, 17);
        break;
      case Glyph.ChevronDown:
        Lines(7, 10, 12, 15, 17, 10);
        break;
      case Glyph.ChevronUp:
        Lines(7, 14, 12, 9, 17, 14);
        break;
    }
  }
}

}
