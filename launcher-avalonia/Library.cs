// The game library on Linux and macOS: a list or a grid of covers, drawn like the Windows launcher's
// (launcher/Widgets.cs GameLibrary), and the large cover of the selected game.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PortLauncher {

// The library entry (Core/Flow.cs) with what this window draws: the cover bitmap and the state colour.
sealed partial class GameEntry {
  public Bitmap Cover;
  public Color StateColor {
    get {
      switch (State) {
        case EntryState.Ready: return Look.Accent;
        case EntryState.HasDisc: return Look.Blue;
        case EntryState.NoProfile: return Look.Warn;
        case EntryState.NeedsRecomp: return Look.Link;
        case EntryState.NoRuntime: return Look.Warn;
        default: return Look.Text3;
      }
    }
  }
}

// A cover file as a bitmap at most 360 px tall or wide, without keeping the file open.
static class CoverThumb {
  public static Bitmap Load(string file) {
    try {
      using (var s = new MemoryStream(File.ReadAllBytes(file))) {
        var full = new Bitmap(s);
        double k = Math.Min(1.0, Math.Min(360.0 / full.PixelSize.Height, 360.0 / full.PixelSize.Width));
        if (k >= 1) return full;
        var small = full.CreateScaledBitmap(new PixelSize(Math.Max(1, (int)(full.PixelSize.Width * k)), Math.Max(1, (int)(full.PixelSize.Height * k))), BitmapInterpolationMode.HighQuality);
        full.Dispose();
        return small;
      }
    } catch (Exception) { return null; }
  }
}

sealed class GameLibrary : Control {
  List<GameEntry> all = new List<GameEntry>();
  List<GameEntry> view = new List<GameEntry>();
  string filter = "";
  int sortColumn;
  double scroll;
  bool sortDown, grid;
  int tileSize = 1;               // 0 small, 1 medium, 2 large
  GameEntry selected, hot;
  int hotHeader = -1;
  bool barHot, barDrag;
  double dragFrom, dragScroll;
  public bool Locked;             // while something runs, the selection stays on its game
  public string EmptyText = "";
  public event EventHandler SelectionChanged, Activated, ConsoleReset;
  public event Action<GameEntry, Point> MenuRequested;

  public GameLibrary() { Focusable = true; ClipToBounds = true; }

  public IReadOnlyList<GameEntry> Entries => all;
  public GameEntry Selected => selected;
  public bool Grid { get => grid; set { grid = value; Relayout(); } }
  public int TileSize { get => tileSize; set { tileSize = Math.Max(0, Math.Min(2, value)); Relayout(); } }
  public string Filter { get => filter; set { filter = value ?? ""; Rebuild(); } }
  string console;
  public string Console { get => console; set { console = value; Rebuild(); } }

  public void SetEntries(List<GameEntry> entries, string selectKey) {
    all = entries;
    Rebuild();
    var pick = view.FirstOrDefault(x => x.Key == selectKey) ?? view.FirstOrDefault();
    Select(pick, true);
  }

  public void SelectKey(string key) {
    var e = all.FirstOrDefault(x => x.Key == key);
    if (e == null) return;
    if (!view.Contains(e) && console != null && e.Console != console) { console = null; ConsoleReset?.Invoke(this, EventArgs.Empty); Rebuild(); }
    if (!view.Contains(e)) Filter = "";
    Select(e, true);
  }

  void Rebuild() {
    IEnumerable<GameEntry> q = all;
    if (console != null) q = q.Where(x => x.Console == console);
    if (filter.Trim().Length > 0) {
      string f = filter.Trim();
      q = q.Where(x => (x.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || (x.Serial ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
    }
    Func<GameEntry, string> key;
    switch (sortColumn) {
      case 1: key = x => x.Serial ?? ""; break;
      case 2: key = x => ((int)x.State).ToString() + (x.Name ?? ""); break;
      case 3: key = x => x.Disc ?? "￿"; break;
      default: key = x => x.Name ?? ""; break;
    }
    view = (sortDown ? q.OrderByDescending(key, StringComparer.OrdinalIgnoreCase) : q.OrderBy(key, StringComparer.OrdinalIgnoreCase)).ToList();
    Relayout();
  }

  void Select(GameEntry e, bool force = false) {
    if (e == selected) { EnsureVisible(); return; }
    if (Locked && !force && selected != null) return;
    selected = e;
    EnsureVisible();
    InvalidateVisual();
    SelectionChanged?.Invoke(this, EventArgs.Empty);
  }

  // ---- geometry (the same numbers as the Windows launcher at 100 %)

  double W => Bounds.Width;
  double H => Bounds.Height;
  double HeaderH => grid ? 0 : 34;
  double RowH => 62;
  double TileW => new[] { 120, 156, 200 }[tileSize];
  double TileH => Math.Floor(TileW * 1.38) + 54;
  double Gap => 18;
  double Pad => 22;
  double ViewW => W - 10;
  double MaxScroll => Math.Max(0, ContentH - H);
  int Columns => Math.Max(1, (int)((ViewW - Pad * 2 + Gap) / (TileW + Gap)));

  double ContentH {
    get {
      if (!grid) return HeaderH + view.Count * RowH + 12;
      int rows = (view.Count + Columns - 1) / Columns;
      return Pad * 2 + rows * TileH + Math.Max(0, rows - 1) * Gap;
    }
  }

  Rect ItemRect(int i) {
    if (!grid) return new Rect(0, HeaderH + i * RowH - scroll, ViewW, RowH);
    int cols = Columns;
    double used = cols * TileW + (cols - 1) * Gap;
    double left = Math.Max(Pad, Math.Floor((ViewW - used) / 2));
    return new Rect(left + (i % cols) * (TileW + Gap), Pad + (i / cols) * (TileH + Gap) - scroll, TileW, TileH);
  }

  double[] ColumnX() {
    double w = ViewW, thumb = 76, serial = 130, state = 180;
    double rest = Math.Max(300, w - thumb - serial - state - 16);
    double name = Math.Floor(rest * 0.58);
    double x0 = thumb, x1 = x0 + name, x2 = x1 + serial, x3 = x2 + state;
    return new[] { x0, x1, x2, x3, w - 16 };
  }

  void Relayout() {
    scroll = Math.Max(0, Math.Min(scroll, MaxScroll));
    InvalidateVisual();
  }

  Rect Track => new Rect(W - 10, HeaderH + 4, 10, Math.Max(0, H - HeaderH - 8));
  Rect Thumb {
    get {
      var t = Track;
      double h = Math.Max(36, t.Height * H / Math.Max(1, ContentH));
      double y = t.Y + (t.Height - h) * (MaxScroll == 0 ? 0 : scroll / MaxScroll);
      return new Rect(t.X, y, t.Width, h);
    }
  }

  protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); Relayout(); }

  void EnsureVisible() {
    int i = view.IndexOf(selected);
    if (i < 0) return;
    var r = ItemRect(i);
    double top = HeaderH + (grid ? 8 : 0), bottom = H - (grid ? 8 : 0);
    if (r.Top < top) scroll += r.Top - top;
    else if (r.Bottom > bottom) scroll += r.Bottom - bottom;
    scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, ContentH - H)));
    Relayout();
  }

  GameEntry HitTest(Point p) {
    if (p.Y < HeaderH) return null;
    for (int i = 0; i < view.Count; ++i) if (ItemRect(i).Contains(p)) return view[i];
    return null;
  }

  int HeaderColumn(double x) { var cx = ColumnX(); return x < cx[1] ? 0 : x < cx[2] ? 1 : x < cx[3] ? 2 : 3; }

  // ---- input

  protected override void OnPointerPressed(PointerPressedEventArgs e) {
    Focus();
    var p = e.GetPosition(this);
    var props = e.GetCurrentPoint(this).Properties;
    if (MaxScroll > 0 && p.X >= Track.X) {
      var th = Thumb;
      if (!th.Contains(p)) { scroll += (p.Y < th.Y ? -1 : 1) * (H - HeaderH); Relayout(); }
      barDrag = true; dragFrom = p.Y; dragScroll = scroll;
      e.Pointer.Capture(this);
      return;
    }
    if (!grid && p.Y < HeaderH) {
      int col = HeaderColumn(p.X);
      if (col == sortColumn) sortDown = !sortDown; else { sortColumn = col; sortDown = false; }
      Rebuild();
      return;
    }
    var hit = HitTest(p);
    if (hit != null) Select(hit);
    if (props.IsRightButtonPressed && hit != null && hit == selected) MenuRequested?.Invoke(hit, p);
    if (props.IsLeftButtonPressed && e.ClickCount == 2 && hit != null && hit == selected) Activated?.Invoke(this, EventArgs.Empty);
    base.OnPointerPressed(e);
  }

  protected override void OnPointerReleased(PointerReleasedEventArgs e) { if (barDrag) { barDrag = false; e.Pointer.Capture(null); InvalidateVisual(); } base.OnPointerReleased(e); }

  protected override void OnPointerMoved(PointerEventArgs e) {
    var p = e.GetPosition(this);
    if (barDrag) {
      double room = Math.Max(1, Track.Height - Thumb.Height);
      scroll = dragScroll + (p.Y - dragFrom) * MaxScroll / room;
      Relayout();
      return;
    }
    bool barNow = MaxScroll > 0 && p.X >= Track.X;
    if (barNow != barHot) { barHot = barNow; InvalidateVisual(); }
    var h = barNow ? null : HitTest(p);
    int hh = !grid && p.Y < HeaderH ? HeaderColumn(p.X) : -1;
    if (h != hot || hh != hotHeader) { hot = h; hotHeader = hh; InvalidateVisual(); }
    Cursor = hh >= 0 || h != null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    base.OnPointerMoved(e);
  }

  protected override void OnPointerExited(PointerEventArgs e) { hot = null; hotHeader = -1; barHot = false; InvalidateVisual(); base.OnPointerExited(e); }

  protected override void OnPointerWheelChanged(PointerWheelEventArgs e) {
    scroll -= e.Delta.Y * (grid ? 90 : 62);
    Relayout();
    e.Handled = true;
    base.OnPointerWheelChanged(e);
  }

  protected override void OnKeyDown(KeyEventArgs e) {
    int i = Math.Max(0, view.IndexOf(selected)), step = grid ? Columns : 1;
    int page = Math.Max(1, (int)((H - HeaderH) / (grid ? TileH + Gap : RowH))) * step;
    int next = i;
    switch (e.Key) {
      case Key.Up: next = i - step; break;
      case Key.Down: next = i + step; break;
      case Key.Left: next = grid ? i - 1 : i; break;
      case Key.Right: next = grid ? i + 1 : i; break;
      case Key.Home: next = 0; break;
      case Key.End: next = view.Count - 1; break;
      case Key.PageUp: next = i - page; break;
      case Key.PageDown: next = i + page; break;
      case Key.Enter: Activated?.Invoke(this, EventArgs.Empty); e.Handled = true; return;
      case Key.Apps: if (selected != null) MenuRequested?.Invoke(selected, ItemRect(i).TopLeft + new Point(40, 20)); e.Handled = true; return;
      default: base.OnKeyDown(e); return;
    }
    if (view.Count > 0 && next != i) Select(view[Math.Max(0, Math.Min(view.Count - 1, next))]);
    e.Handled = true;
  }

  protected override void OnGotFocus(GotFocusEventArgs e) { InvalidateVisual(); base.OnGotFocus(e); }
  protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e) { InvalidateVisual(); base.OnLostFocus(e); }

  // ---- painting

  public override void Render(DrawingContext g) {
    g.FillRectangle(Look.B(Look.Bg), new Rect(Bounds.Size));
    if (view.Count == 0) {
      Glyphs.Draw(g, Glyph.Disc, new Rect(ViewW / 2 - 28, H / 2 - 80, 56, 56), Look.Border, 1.4);
      var ft = Look.Format(filter.Length > 0 ? L.T("Nenhum jogo com esse nome.", "No game matches.") : EmptyText, Look.UI(), 10.5, Look.Text3,
                          Math.Max(50, ViewW - 80), TextAlignment.Center, true);
      g.DrawText(ft, new Point(40, H / 2 - 10));
    }
    for (int i = 0; i < view.Count; ++i) {
      var r = ItemRect(i);
      if (r.Bottom < HeaderH || r.Top > H) continue;
      if (grid) PaintTile(g, view[i], r); else PaintRow(g, view[i], r);
    }
    if (!grid) PaintHeader(g);
    if (MaxScroll > 0) {
      var th = Thumb;
      double w = barHot || barDrag ? 8 : 4;
      Look.FillRound(g, barHot || barDrag ? Look.Text3 : Look.Border, new Rect(W - w - 3, th.Y, w, th.Height), w / 2);
    }
  }

  void PaintHeader(DrawingContext g) {
    var cx = ColumnX();
    g.FillRectangle(Look.B(Look.Bg), new Rect(0, 0, ViewW, HeaderH));
    g.DrawLine(Look.P(Look.Border), new Point(0, HeaderH - 0.5), new Point(ViewW, HeaderH - 0.5));
    string[] names = { L.T("NOME", "NAME"), L.T("SERIAL", "SERIAL"), L.T("ESTADO", "STATUS"), L.T("DISCO", "DISC") };
    double[] starts = { 18, cx[1], cx[2], cx[3] };
    for (int c = 0; c < 4; ++c) {
      var color = c == sortColumn ? Look.Text : hotHeader == c ? Look.Text2 : Look.Text3;
      double tx = c == 0 ? cx[0] : starts[c];
      var ft = Look.Format(names[c], Look.Semi, 8.5, color);
      g.DrawText(ft, new Point(tx, (HeaderH - ft.Height) / 2));
      if (c == sortColumn) {
        double s = 14;
        Glyphs.Draw(g, sortDown ? Glyph.ChevronDown : Glyph.ChevronUp, new Rect(tx + ft.WidthIncludingTrailingWhitespace + 4, (HeaderH - s) / 2, s, s), Look.Accent, 2);
      }
    }
  }

  void PaintRow(DrawingContext g, GameEntry x, Rect r) {
    var cx = ColumnX();
    bool sel = x == selected;
    var band = new Rect(8, r.Y + 3, r.Width - 16, r.Height - 6);
    if (sel || x == hot) Look.FillRound(g, sel ? (IsFocused ? Look.Selection : Look.Surface2) : Look.Surface, band, 8);
    if (sel) Look.FillRound(g, Look.Accent, new Rect(8, r.Y + 16, 3, r.Height - 32), 1.5);
    DrawCover(g, x, new Rect(22, r.Y + 8, 38, r.Height - 16), 4, false);
    double textW = cx[1] - cx[0] - 16;
    Look.DrawLeft(g, x.Name, Look.Semi, 10.5, Look.Text, new Rect(cx[0], r.Y + 12, textW, 22), false);
    Look.DrawLeft(g, x.Platform ?? "", Look.UI(), 9, Look.Text3, new Rect(cx[0], r.Y + 34, textW, 18), false);
    Look.DrawLeft(g, x.Serial ?? "—", Look.Mono, 9.5, x.Serial != null ? Look.Text2 : Look.Text3, new Rect(cx[1], r.Y, cx[2] - cx[1] - 12, r.Height));
    Pill.Draw(g, x.StateText, x.StateColor, new Point(cx[2], r.Y + r.Height / 2), false);
    Look.DrawLeft(g, x.Disc != null ? Path.GetFileName(x.Disc) : "—", Look.UI(), 9, x.Disc != null ? Look.Text2 : Look.Text3, new Rect(cx[3], r.Y, cx[4] - cx[3], r.Height));
  }

  void PaintTile(DrawingContext g, GameEntry x, Rect r) {
    bool sel = x == selected, isHot = x == hot;
    double lift = isHot || sel ? 3 : 0;
    var cover = new Rect(r.X, r.Y - lift, r.Width, Math.Floor(r.Width * 1.38));
    for (int i = 3; i >= 1; --i)
      Look.FillRound(g, Color.FromArgb((byte)(Look.Dark ? 26 : 12), 0, 0, 0), new Rect(cover.X - i + 1, cover.Y + i * 1.6 + 1, cover.Width + i * 2 - 2, cover.Height), 10);
    DrawCover(g, x, cover, 10, true);
    if (sel) Look.StrokeRound(g, IsFocused ? Look.Accent : Look.Text3, cover.Inflate(3), 12, 2.2);
    Pill.Draw(g, x.StateText, x.StateColor, new Point(cover.X + 8, cover.Bottom - 18), true);
    var ft = Look.Format(x.Name, Look.Semi, tileSize == 0 ? 9 : 9.75, sel ? Look.Text : Look.Text2, r.Width + 4, TextAlignment.Center, true, 36);
    g.DrawText(ft, new Point(r.X - 2, r.Y + Math.Floor(r.Width * 1.38) + 10));
  }

  // The cover, or a tile drawn from the name when there is none.
  public static void DrawCover(DrawingContext g, GameEntry x, Rect r, double radius, bool large) {
    var rr = new RoundedRect(r, radius);
    using (g.PushClip(rr)) {
      if (x.Cover != null) {
        g.FillRectangle(Look.B(Color.FromRgb(18, 20, 24)), r);
        double ar = x.Cover.Size.Width / x.Cover.Size.Height, br = r.Width / r.Height;
        var src = new Rect(x.Cover.Size);
        if (Math.Abs(ar - br) < 0.25) {
          double w = ar > br ? r.Height * ar : r.Width, h = ar > br ? r.Height : r.Width / ar;
          g.DrawImage(x.Cover, src, new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h));
        } else {
          double hb = r.Height, wb = hb * ar;
          using (g.PushOpacity(0.35)) g.DrawImage(x.Cover, src, new Rect(r.X + (r.Width - wb) / 2, r.Y, wb, hb));
          double w = r.Width, h = w / ar;
          g.DrawImage(x.Cover, src, new Rect(r.X, r.Y + (r.Height - h) / 2, w, h));
        }
      } else {
        int hash = 0;
        foreach (char c in x.Name ?? "") hash = unchecked(hash * 31 + c);
        double hue = Math.Abs(hash % 360);
        var c1 = FromHsl(hue, 0.42, Look.Dark ? 0.30 : 0.46);
        var c2 = FromHsl((hue + 38) % 360, 0.50, Look.Dark ? 0.16 : 0.30);
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.6, 1, RelativeUnit.Relative) };
        brush.GradientStops.Add(new GradientStop(c1, 0));
        brush.GradientStops.Add(new GradientStop(c2, 1));
        g.FillRectangle(brush, r);
        double d = Math.Max(r.Width, r.Height) * 0.95;
        var center = new Point(r.Right - r.Width * 0.18, r.Bottom - r.Height * 0.16);
        var ring = Look.P(Color.FromArgb(30, 255, 255, 255), Math.Max(1, r.Width / 60));
        for (int i = 1; i <= 4; ++i) { double rad = d * i / 7; g.DrawEllipse(null, ring, center, rad, rad); }
        double size = Math.Max(7, r.Width / (large ? 4.2 : 3.4)) * 72 / 96;
        var ft = Look.Format(Initials(x.Name), Look.Semi, size, Color.FromArgb(235, 255, 255, 255), r.Width, TextAlignment.Center);
        double area = r.Height * (large ? 0.78 : 1);
        g.DrawText(ft, new Point(r.X, r.Y + (area - ft.Height) / 2));
      }
    }
    g.DrawRectangle(null, Look.P(Color.FromArgb((byte)(Look.Dark ? 30 : 40), (byte)(Look.Dark ? 255 : 0), (byte)(Look.Dark ? 255 : 0), (byte)(Look.Dark ? 255 : 0))), rr);
  }

  static string Initials(string name) {
    var words = (name ?? "?").Split(new[] { ' ', '-', ':', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                             .Where(w => char.IsLetterOrDigit(w[0]) && !new[] { "the", "of", "de", "do", "da", "no", "a" }.Contains(w.ToLowerInvariant())).ToList();
    var letters = words.Where(w => char.IsLetter(w[0])).ToList();
    if (letters.Count >= 2) words = letters;
    if (words.Count == 0) return "?";
    if (words.Count == 1) return words[0].Substring(0, Math.Min(2, words[0].Length)).ToUpperInvariant();
    return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
  }

  static Color FromHsl(double h, double s, double l) {
    double c = (1 - Math.Abs(2 * l - 1)) * s, hp = h / 60, x = c * (1 - Math.Abs(hp % 2 - 1)), m = l - c / 2;
    double r = 0, g = 0, b = 0;
    if (hp < 1) { r = c; g = x; } else if (hp < 2) { r = x; g = c; } else if (hp < 3) { g = c; b = x; }
    else if (hp < 4) { g = x; b = c; } else if (hp < 5) { r = x; b = c; } else { r = c; b = x; }
    return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
  }
}

// The selected game, large, at the right of the library.
sealed class CoverView : Control {
  public GameEntry Entry;
  public override void Render(DrawingContext g) {
    if (Entry == null) return;
    double w = Math.Min(Bounds.Width, Bounds.Height / 1.38), h = w * 1.38;
    var r = new Rect((Bounds.Width - w) / 2, 0, w, Math.Max(1, h - 6));
    for (int i = 4; i >= 1; --i)
      Look.FillRound(g, Color.FromArgb((byte)(Look.Dark ? 24 : 10), 0, 0, 0), new Rect(r.X - i, r.Y + i * 2, r.Width + i * 2, r.Height), 12);
    GameLibrary.DrawCover(g, Entry, r, 12, true);
  }
}

}
