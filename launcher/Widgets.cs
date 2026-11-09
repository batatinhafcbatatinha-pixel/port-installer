// The library window's own controls: line icons, toolbar buttons, the search field, the view switch,
// message bars, and the game library itself (a list or a grid of covers). All drawn with the Theme colours.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PortLauncher {

enum Glyph { Disc, FolderAdd, Refresh, Play, Stop, Install, Gear, Box, List, Grid, Log, Update, Search, Folder, Image, Close, ChevronDown, ChevronUp }

static class Glyphs {
  // A line icon in a square box (24 units drawn into r).
  public static void Draw(Graphics g, Glyph glyph, RectangleF r, Color color, float stroke = 1.7f) {
    var state = g.Save();
    g.TranslateTransform(r.X, r.Y);
    g.ScaleTransform(r.Width / 24f, r.Height / 24f);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    using (var p = new Pen(color, Math.Max(1.2f, stroke * 24f / r.Width)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
    using (var b = new SolidBrush(color)) {
      switch (glyph) {
        case Glyph.Disc:
          g.DrawEllipse(p, 3, 3, 18, 18);
          g.DrawEllipse(p, 9.5f, 9.5f, 5, 5);
          g.DrawArc(p, 6, 6, 12, 12, 200, 70);
          break;
        case Glyph.FolderAdd:
        case Glyph.Folder:
          using (var path = new GraphicsPath()) {
            path.AddLines(new[] { new PointF(3, 7), new PointF(3, 5.5f), new PointF(4.5f, 4), new PointF(9, 4), new PointF(11, 6.5f), new PointF(19.5f, 6.5f),
                                  new PointF(21, 8), new PointF(21, 18.5f), new PointF(19.5f, 20), new PointF(4.5f, 20), new PointF(3, 18.5f) });
            path.CloseFigure();
            g.DrawPath(p, path);
          }
          if (glyph == Glyph.FolderAdd) { g.DrawLine(p, 12, 10.5f, 12, 16.5f); g.DrawLine(p, 9, 13.5f, 15, 13.5f); }
          break;
        case Glyph.Refresh:
          g.DrawArc(p, 4.5f, 4.5f, 15, 15, -60, 290);
          g.DrawLines(p, new[] { new PointF(15.5f, 3.2f), new PointF(16.3f, 6.4f), new PointF(13.1f, 7.4f) });
          break;
        case Glyph.Play:
          using (var path = new GraphicsPath()) {
            path.AddLines(new[] { new PointF(7.5f, 4.5f), new PointF(19.5f, 12), new PointF(7.5f, 19.5f) });
            path.CloseFigure();
            g.FillPath(b, path);
            using (var round = new Pen(color, 2.4f) { LineJoin = LineJoin.Round }) g.DrawPath(round, path);
          }
          break;
        case Glyph.Stop:
          using (var path = Theme.Round(new RectangleF(5.5f, 5.5f, 13, 13), 2.5f)) g.FillPath(b, path);
          break;
        case Glyph.Install:
          g.DrawLine(p, 12, 3.5f, 12, 14.5f);
          g.DrawLines(p, new[] { new PointF(7.5f, 10.5f), new PointF(12, 15), new PointF(16.5f, 10.5f) });
          g.DrawLines(p, new[] { new PointF(4, 15.5f), new PointF(4, 19.5f), new PointF(20, 19.5f), new PointF(20, 15.5f) });
          break;
        case Glyph.Gear: {
          var pts = new List<PointF>();
          for (int i = 0; i < 32; ++i) {
            double a = i * Math.PI / 16 - Math.PI / 32;
            float rad = (i / 2) % 2 == 0 ? 9.2f : 7.0f;
            pts.Add(new PointF(12 + rad * (float)Math.Cos(a), 12 + rad * (float)Math.Sin(a)));
          }
          g.DrawPolygon(p, pts.ToArray());
          g.DrawEllipse(p, 9, 9, 6, 6);
          break;
        }
        case Glyph.Box:
          g.DrawPolygon(p, new[] { new PointF(12, 3), new PointF(20, 7.5f), new PointF(20, 16.5f), new PointF(12, 21), new PointF(4, 16.5f), new PointF(4, 7.5f) });
          g.DrawLines(p, new[] { new PointF(4, 7.5f), new PointF(12, 12), new PointF(20, 7.5f) });
          g.DrawLine(p, 12, 12, 12, 21);
          break;
        case Glyph.List:
          for (int i = 0; i < 3; ++i) { float y = 6.5f + i * 5.5f; g.FillEllipse(b, 3.5f, y - 1.3f, 2.6f, 2.6f); g.DrawLine(p, 9, y, 20.5f, y); }
          break;
        case Glyph.Grid:
          foreach (var o in new[] { new PointF(4, 4), new PointF(13.5f, 4), new PointF(4, 13.5f), new PointF(13.5f, 13.5f) })
            using (var path = Theme.Round(new RectangleF(o.X, o.Y, 6.5f, 6.5f), 1.5f)) g.DrawPath(p, path);
          break;
        case Glyph.Log:
          using (var path = Theme.Round(new RectangleF(3.5f, 4.5f, 17, 15), 2.5f)) g.DrawPath(p, path);
          g.DrawLines(p, new[] { new PointF(7, 9.5f), new PointF(9.5f, 12), new PointF(7, 14.5f) });
          g.DrawLine(p, 11.5f, 14.5f, 16.5f, 14.5f);
          break;
        case Glyph.Update:
          g.DrawArc(p, 4, 4, 16, 16, 30, 300);
          g.DrawLine(p, 12, 8, 12, 15.5f);
          g.DrawLines(p, new[] { new PointF(9, 11), new PointF(12, 8), new PointF(15, 11) });
          break;
        case Glyph.Search:
          g.DrawEllipse(p, 4, 4, 11.5f, 11.5f);
          g.DrawLine(p, 14, 14, 19.5f, 19.5f);
          break;
        case Glyph.Image:
          using (var path = Theme.Round(new RectangleF(3.5f, 4.5f, 17, 15), 2.5f)) g.DrawPath(p, path);
          g.DrawLines(p, new[] { new PointF(4, 17), new PointF(9.5f, 11.5f), new PointF(13, 15), new PointF(15.5f, 12.5f), new PointF(20, 17) });
          g.FillEllipse(b, 14.5f, 7.5f, 3, 3);
          break;
        case Glyph.Close:
          g.DrawLine(p, 7, 7, 17, 17); g.DrawLine(p, 17, 7, 7, 17);
          break;
        case Glyph.ChevronDown:
          g.DrawLines(p, new[] { new PointF(7, 10), new PointF(12, 15), new PointF(17, 10) });
          break;
        case Glyph.ChevronUp:
          g.DrawLines(p, new[] { new PointF(7, 14), new PointF(12, 9), new PointF(17, 14) });
          break;
      }
    }
    g.Restore(state);
  }
}

// A toolbar button: an icon over a short label, like an emulator's main toolbar.
sealed class ToolButton : Control {
  readonly Glyph glyph;
  bool hover, down;
  public Color? Tint;              // the icon's colour when enabled (Play: accent)
  public bool Checked { get; set; }

  public ToolButton(Glyph glyph, string text, int width = 74) {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    this.glyph = glyph; Text = text;
    Font = Theme.UI(8.75f);
    Size = new Size(Math.Max(width, TextRenderer.MeasureText(text, Font).Width + 18), 58);
    Margin = new Padding(1, 0, 1, 0);
    Cursor = Cursors.Hand;
  }

  protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
  protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
  protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
  protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
  protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
  protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Parent?.BackColor ?? Theme.Surface);
    Theme.Smooth(g);
    float k = DeviceDpi / 96f;
    if (Enabled && (hover || Checked)) {
      using (var path = Theme.Round(new RectangleF(2, 3 * k, Width - 4, Height - 6 * k), 8 * k))
      using (var b = new SolidBrush(down ? Theme.Border : Checked ? Theme.Surface2 : Theme.Hover)) g.FillPath(b, path);
    }
    var color = !Enabled ? Theme.DisabledText : Tint ?? (hover ? Theme.Text : Theme.Text2);
    float size = 22 * k;
    Glyphs.Draw(g, glyph, new RectangleF((Width - size) / 2f, 8 * k, size, size), color);
    TextRenderer.DrawText(g, Text, Font, new Rectangle(0, (int)(33 * k), Width, (int)(20 * k)), Enabled ? (hover ? Theme.Text : Theme.Text2) : Theme.DisabledText,
                          TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
  }
}

// A thin vertical line between toolbar groups.
sealed class ToolSeparator : Control {
  public ToolSeparator() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true); Size = new Size(17, 58); Margin = Padding.Empty; }
  protected override void OnPaint(PaintEventArgs e) {
    e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
    using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, Width / 2, Height * 0.22f, Width / 2, Height * 0.78f);
  }
}

// Two icon buttons side by side: list or grid.
sealed class ViewSwitch : Control {
  public bool Grid;
  int hover = -1;
  public event EventHandler Changed;
  public ViewSwitch() {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    Size = new Size(76, 34); Cursor = Cursors.Hand;
  }
  public void Set(bool grid) { if (grid != Grid) { Grid = grid; Invalidate(); Changed?.Invoke(this, EventArgs.Empty); } }
  protected override void OnMouseMove(MouseEventArgs e) { int h = e.X < Width / 2 ? 0 : 1; if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
  protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
  protected override void OnMouseDown(MouseEventArgs e) { Set(e.X >= Width / 2); base.OnMouseDown(e); }
  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Parent?.BackColor ?? Theme.Surface);
    Theme.Smooth(g);
    float k = DeviceDpi / 96f;
    var outer = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
    using (var path = Theme.Round(outer, 8 * k)) {
      using (var b = new SolidBrush(Theme.Bg)) g.FillPath(b, path);
      using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
    }
    for (int i = 0; i < 2; ++i) {
      bool on = (i == 1) == Grid;
      var cell = new RectangleF(3 * k + i * (Width / 2f - 3 * k), 3 * k, Width / 2f - 3 * k, Height - 6 * k);
      if (on || hover == i)
        using (var path = Theme.Round(cell, 6 * k)) using (var b = new SolidBrush(on ? Theme.Surface2 : Theme.Hover)) g.FillPath(b, path);
      float s = 18 * k;
      Glyphs.Draw(g, i == 0 ? Glyph.List : Glyph.Grid, new RectangleF(cell.X + (cell.Width - s) / 2, cell.Y + (cell.Height - s) / 2, s, s), on ? Theme.Accent : Theme.Text3);
    }
  }
}

// The console tabs above the library: "All" and one tab per console that has games, each with its count.
sealed class ConsoleTabs : Control {
  public sealed class Tab { public string Id, Text; public int Count; }
  public List<Tab> Tabs = new List<Tab>();
  public string Selected;
  int hover = -1;
  readonly List<Rectangle> rects = new List<Rectangle>();
  public event EventHandler Changed;

  public ConsoleTabs() {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    Height = 44; Dock = DockStyle.Top; Cursor = Cursors.Hand;
  }
  float K => DeviceDpi / 96f;
  public void Set(List<Tab> tabs, string selected) { Tabs = tabs; Selected = tabs.Any(t => t.Id == selected) ? selected : null; Invalidate(); }
  int HitTest(Point p) { for (int i = 0; i < rects.Count; ++i) if (rects[i].Contains(p)) return i; return -1; }
  protected override void OnMouseMove(MouseEventArgs e) { int h = HitTest(e.Location); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
  protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
  protected override void OnMouseDown(MouseEventArgs e) {
    int h = HitTest(e.Location);
    if (h >= 0 && h < Tabs.Count && Tabs[h].Id != Selected) { Selected = Tabs[h].Id; Invalidate(); Changed?.Invoke(this, EventArgs.Empty); }
    base.OnMouseDown(e);
  }
  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Theme.Bg);
    Theme.Smooth(g);
    rects.Clear();
    var font = Theme.Semi(10f);
    float x = 18 * K;
    for (int i = 0; i < Tabs.Count; ++i) {
      var t = Tabs[i];
      string text = t.Text + "  " + t.Count;
      var size = TextRenderer.MeasureText(g, text, font);
      var r = new Rectangle((int)x, 0, size.Width + (int)(20 * K), Height);
      rects.Add(r);
      bool on = t.Id == Selected;
      if (hover == i && !on) using (var path = Theme.Round(new RectangleF(r.X, 6 * K, r.Width, Height - 14 * K), 6 * K)) using (var b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
      TextRenderer.DrawText(g, text, font, new Rectangle(r.X, 0, r.Width, Height - (int)(3 * K)), on ? Theme.Text : Theme.Text3,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
      if (on) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X + 8 * K, Height - 3 * K, r.Width - 16 * K, 3 * K);
      x += r.Width + 4 * K;
    }
    using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
  }
}

// A rounded search field with a magnifier and a hint.
sealed class SearchBox : Control {
  public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None };
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, string lp);
  public SearchBox(string hint) {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    Size = new Size(250, 34);
    Box.BackColor = Theme.Bg; Box.ForeColor = Theme.Text; Box.Font = Theme.UI(10f);
    Box.HandleCreated += (s, e) => SendMessage(Box.Handle, 0x1501, (IntPtr)1, hint);   // EM_SETCUEBANNER
    Box.GotFocus += (s, e) => Invalidate();
    Box.LostFocus += (s, e) => Invalidate();
    Controls.Add(Box);
    Cursor = Cursors.IBeam;
  }
  protected override void OnLayout(LayoutEventArgs e) {
    float k = DeviceDpi / 96f;
    Box.Location = new Point((int)(34 * k), (Height - Box.Height) / 2 + 1);
    Box.Width = Width - (int)(46 * k);
    base.OnLayout(e);
  }
  protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }
  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Parent?.BackColor ?? Theme.Surface);
    Theme.Smooth(g);
    float k = DeviceDpi / 96f;
    using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Height / 2f - 1)) {
      using (var b = new SolidBrush(Theme.Bg)) g.FillPath(b, path);
      using (var p = new Pen(Box.Focused ? Theme.Accent : Theme.Border, Box.Focused ? 1.5f : 1f)) g.DrawPath(p, path);
    }
    float s = 16 * k;
    Glyphs.Draw(g, Glyph.Search, new RectangleF(12 * k, (Height - s) / 2, s, s), Theme.Text3);
  }
}

// A message strip under the toolbar: an icon, a sentence and up to two buttons.
sealed class InfoBar : Panel {
  readonly Label text;
  readonly FlowLayoutPanel buttons;
  public Glyph Icon = Glyph.Update;
  public Color Tone;
  public InfoBar() {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    Dock = DockStyle.Top; Height = 50; Visible = false; Tone = Theme.Blue;
    Padding = new Padding(52, 0, 12, 0);
    BackColor = Theme.Surface;
    buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 8, 0, 0) };
    text = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, BackColor = Color.Transparent, AutoEllipsis = true };
    Controls.Add(text);
    Controls.Add(buttons);
  }
  public void Show(Glyph icon, Color tone, string message, params KeyValuePair<string, Action>[] actions) {
    Icon = icon; Tone = tone; text.Text = message;
    buttons.Controls.Clear();
    foreach (var a in actions) {
      var b = new FlatButton(a.Key, buttons.Controls.Count == 0 ? ButtonKind.Primary : ButtonKind.Secondary,
                             TextRenderer.MeasureText(a.Key, Theme.Semi(10f)).Width + 34, 34) { Font = Theme.Semi(10f), Margin = new Padding(8, 0, 0, 0) };
      var act = a.Value;
      b.Click += (s, e) => act();
      buttons.Controls.Add(b);
    }
    Visible = true;
    Invalidate(true);
  }
  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Theme.Surface);
    Theme.Smooth(g);
    using (var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 34 : 26, Tone))) g.FillRectangle(b, ClientRectangle);
    using (var b = new SolidBrush(Tone)) g.FillRectangle(b, 0, 0, 3, Height);
    using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
    float k = DeviceDpi / 96f, s = 20 * k;
    Glyphs.Draw(g, Icon, new RectangleF(20 * k, (Height - s) / 2, s, s), Tone);
  }
}

// ---------------------------------------------------------------------- the library

enum EntryState { Ready, HasDisc, NoDisc, NoProfile, Kit, NeedsRecomp, Unsupported }

sealed class GameEntry {
  public string Key, Name, Serial, Disc, Dir, Platform, Console;
  public RecompGame Game;          // null: a disc without a profile, or the single game of a kit
  public Recomp Recomp;            // the installed recompiler of the game's console
  public KnownRecomp Known;        // the recompiler of the game's console (installed or not)
  public bool Installed;
  public bool NoProfile;           // the recompiler has no profile but can try its generic path
  public bool NeedsRecomp;         // the console's recompiler is not on this PC yet
  public bool Unsupported;         // the recompiler has neither a profile nor a generic path for it
  public Bitmap Cover;
  public bool CoverDone;

  public EntryState State => NeedsRecomp ? EntryState.NeedsRecomp : Unsupported ? EntryState.Unsupported : NoProfile ? EntryState.NoProfile : Installed ? EntryState.Ready : Disc != null ? EntryState.HasDisc : Dir == null ? EntryState.Kit : EntryState.NoDisc;
  public string StateText {
    get {
      switch (State) {
        case EntryState.Ready: return L.T("Pronto para jogar", "Ready to play");
        case EntryState.HasDisc: return L.T("Disco encontrado", "Disc found");
        case EntryState.NoProfile: return L.T("Experimental", "Experimental");
        case EntryState.NeedsRecomp: return L.T("Baixar recompilador", "Get recompiler");
        case EntryState.Unsupported: return L.T("Ainda sem suporte", "Not supported yet");
        case EntryState.Kit: return L.T("Não instalado", "Not installed");
        default: return L.T("Sem disco", "No disc");
      }
    }
  }
  public Color StateColor {
    get {
      switch (State) {
        case EntryState.Ready: return Theme.Accent;
        case EntryState.HasDisc: return Theme.Blue;
        case EntryState.NoProfile: return Theme.Warn;
        case EntryState.NeedsRecomp: return Theme.Link;
        default: return Theme.Text3;
      }
    }
  }
}

sealed class GameLibrary : Control {
  List<GameEntry> all = new List<GameEntry>();
  List<GameEntry> view = new List<GameEntry>();
  string filter = "";
  int sortColumn, scroll;
  bool sortDown, grid;
  int tileSize = 1;               // 0 small, 1 medium, 2 large
  GameEntry selected, hover;
  int hoverHeader = -1;
  bool barHot, barDrag;
  int dragFrom, dragScroll;
  public bool Locked;             // while something runs, the selection stays on its game
  public string EmptyText = "";
  public event EventHandler SelectionChanged, Activated;
  public event Action<GameEntry, Point> MenuRequested;

  public GameLibrary() {
    SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    TabStop = true;
    BackColor = Theme.Bg;
  }

  float K => DeviceDpi / 96f;
  int S(float v) => (int)Math.Round(v * K);

  public IReadOnlyList<GameEntry> Entries => all;
  public GameEntry Selected => selected;
  public bool Grid { get => grid; set { grid = value; Relayout(); } }
  public int TileSize { get => tileSize; set { tileSize = Math.Max(0, Math.Min(2, value)); Relayout(); } }
  public string Filter { get => filter; set { filter = value ?? ""; Rebuild(); } }
  string console;                 // the console tab (null: every console)
  public string Console { get => console; set { console = value; Rebuild(); } }
  public int CountFor(string c) => all.Count(x => c == null || x.Console == c);

  public void SetEntries(List<GameEntry> entries, string selectKey) {
    all = entries;
    Rebuild();
    var pick = view.FirstOrDefault(x => x.Key == selectKey) ?? view.FirstOrDefault();
    Select(pick, true);
  }

  public void SelectKey(string key) {
    var e = all.FirstOrDefault(x => x.Key == key);
    if (e == null) return;
    if (!view.Contains(e) && console != null && e.Console != console) { console = null; ConsoleReset?.Invoke(this, EventArgs.Empty); }
    if (!view.Contains(e)) Filter = "";
    Select(e, true);
  }
  public event EventHandler ConsoleReset;

  void Rebuild() {
    IEnumerable<GameEntry> q = all;
    if (console != null) q = q.Where(x => x.Console == console);
    if (filter.Trim().Length > 0) {
      string f = filter.Trim();
      q = q.Where(x => (x.Name ?? "").IndexOf(f, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                       (x.Serial ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
    }
    Func<GameEntry, string> key;
    switch (sortColumn) {
      case 1: key = x => x.Serial ?? ""; break;
      case 2: key = x => ((int)x.State).ToString() + (x.Name ?? ""); break;
      case 3: key = x => x.Disc ?? "￿"; break;
      default: key = x => x.Name ?? ""; break;
    }
    view = (sortDown ? q.OrderByDescending(key, StringComparer.CurrentCultureIgnoreCase) : q.OrderBy(key, StringComparer.CurrentCultureIgnoreCase)).ToList();
    Relayout();
  }

  void Select(GameEntry e, bool force = false) {
    if (e == selected) { EnsureVisible(); return; }
    if (Locked && !force && selected != null) { System.Media.SystemSounds.Beep.Play(); return; }
    selected = e;
    EnsureVisible();
    Invalidate();
    SelectionChanged?.Invoke(this, EventArgs.Empty);
  }

  // ---- geometry

  int HeaderH => grid ? 0 : S(34);
  int RowH => S(62);
  int TileW => S(new[] { 120, 156, 200 }[tileSize]);
  int TileH => (int)(TileW * 1.38f) + S(54);
  int Gap => S(18);
  int Pad => S(22);
  int ViewW => Width - S(10);
  int MaxScroll => Math.Max(0, ContentH - Height);
  int Columns => Math.Max(1, (ViewW - Pad * 2 + Gap) / (TileW + Gap));

  int ContentH {
    get {
      if (!grid) return HeaderH + view.Count * RowH + S(12);
      int rows = (view.Count + Columns - 1) / Columns;
      return Pad * 2 + rows * TileH + Math.Max(0, rows - 1) * Gap;
    }
  }

  Rectangle ItemRect(int i) {
    if (!grid) return new Rectangle(0, HeaderH + i * RowH - scroll, ViewW, RowH);
    int cols = Columns, used = cols * TileW + (cols - 1) * Gap;
    int left = Math.Max(Pad, (ViewW - used) / 2);
    return new Rectangle(left + (i % cols) * (TileW + Gap), Pad + (i / cols) * (TileH + Gap) - scroll, TileW, TileH);
  }

  // List columns: cover | name | serial | state | disc
  int[] ColumnX() {
    int w = ViewW, thumb = S(76), serial = S(130), state = S(180);
    int rest = Math.Max(S(300), w - thumb - serial - state - S(16));
    int name = (int)(rest * 0.58f);
    int x0 = thumb, x1 = x0 + name, x2 = x1 + serial, x3 = x2 + state;
    return new[] { x0, x1, x2, x3, w - S(16) };
  }

  void Relayout() {
    scroll = Math.Max(0, Math.Min(scroll, MaxScroll));
    Invalidate();
  }

  // A thin overlay scroll bar (Windows' own ignores the dark colours); wider while the mouse is on it.
  RectangleF Track => new RectangleF(Width - S(10), HeaderH + S(4), S(10), Height - HeaderH - S(8));
  RectangleF Thumb {
    get {
      var t = Track;
      float h = Math.Max(S(36), t.Height * Height / Math.Max(1f, ContentH));
      float y = t.Y + (t.Height - h) * (MaxScroll == 0 ? 0 : scroll / (float)MaxScroll);
      return new RectangleF(t.X, y, t.Width, h);
    }
  }

  protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

  void EnsureVisible() {
    int i = view.IndexOf(selected);
    if (i < 0) return;
    var r = ItemRect(i);
    int top = HeaderH + (grid ? S(8) : 0), bottom = Height - (grid ? S(8) : 0);
    if (r.Top < top) scroll += r.Top - top;
    else if (r.Bottom > bottom) scroll += r.Bottom - bottom;
    scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, ContentH - Height)));
    Relayout();
  }

  GameEntry HitTest(Point p) {
    if (p.Y < HeaderH) return null;
    for (int i = 0; i < view.Count; ++i) if (ItemRect(i).Contains(p)) return view[i];
    return null;
  }

  // ---- input

  protected override void OnMouseDown(MouseEventArgs e) {
    Focus();
    if (MaxScroll > 0 && e.X >= Track.X) {
      var th = Thumb;
      if (!th.Contains(e.Location)) { scroll += (e.Y < th.Y ? -1 : 1) * (Height - HeaderH); Relayout(); }
      barDrag = true; dragFrom = e.Y; dragScroll = scroll;
      return;
    }
    if (!grid && e.Y < HeaderH) {
      var cx = ColumnX();
      int col = e.X < cx[1] ? 0 : e.X < cx[2] ? 1 : e.X < cx[3] ? 2 : 3;
      if (col == sortColumn) sortDown = !sortDown; else { sortColumn = col; sortDown = false; }
      Rebuild();
      return;
    }
    var hit = HitTest(e.Location);
    if (hit != null) Select(hit);
    if (e.Button == MouseButtons.Right && hit != null && hit == selected) MenuRequested?.Invoke(hit, e.Location);
    base.OnMouseDown(e);
  }

  protected override void OnMouseDoubleClick(MouseEventArgs e) {
    if (e.Button == MouseButtons.Left && HitTest(e.Location) is GameEntry hit && hit == selected) Activated?.Invoke(this, EventArgs.Empty);
    base.OnMouseDoubleClick(e);
  }

  protected override void OnMouseUp(MouseEventArgs e) { barDrag = false; base.OnMouseUp(e); }

  protected override void OnMouseMove(MouseEventArgs e) {
    if (barDrag) {
      var t = Track;
      float room = Math.Max(1f, t.Height - Thumb.Height);
      scroll = dragScroll + (int)((e.Y - dragFrom) * MaxScroll / room);
      Relayout();
      return;
    }
    bool hot = MaxScroll > 0 && e.X >= Track.X;
    if (hot != barHot) { barHot = hot; Invalidate(); }
    var h = hot ? null : HitTest(e.Location);
    int hh = -1;
    if (!grid && e.Y < HeaderH) { var cx = ColumnX(); hh = e.X < cx[1] ? 0 : e.X < cx[2] ? 1 : e.X < cx[3] ? 2 : 3; }
    if (h != hover || hh != hoverHeader) { hover = h; hoverHeader = hh; Invalidate(); }
    Cursor = hh >= 0 || h != null ? Cursors.Hand : Cursors.Default;
    base.OnMouseMove(e);
  }

  protected override void OnMouseLeave(EventArgs e) { hover = null; hoverHeader = -1; barHot = false; Invalidate(); base.OnMouseLeave(e); }

  protected override void OnMouseWheel(MouseEventArgs e) {
    scroll -= e.Delta * S(grid ? 90 : 62) / 120;
    Relayout();
    base.OnMouseWheel(e);
  }

  protected override bool IsInputKey(Keys k) {
    switch (k & Keys.KeyCode) {
      case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right: case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown: case Keys.Enter: return true;
    }
    return base.IsInputKey(k);
  }

  protected override void OnKeyDown(KeyEventArgs e) {
    int i = Math.Max(0, view.IndexOf(selected)), step = grid ? Columns : 1, page = Math.Max(1, (Height - HeaderH) / (grid ? TileH + Gap : RowH)) * step;
    int next = i;
    switch (e.KeyCode) {
      case Keys.Up: next = i - step; break;
      case Keys.Down: next = i + step; break;
      case Keys.Left: next = grid ? i - 1 : i; break;
      case Keys.Right: next = grid ? i + 1 : i; break;
      case Keys.Home: next = 0; break;
      case Keys.End: next = view.Count - 1; break;
      case Keys.PageUp: next = i - page; break;
      case Keys.PageDown: next = i + page; break;
      case Keys.Enter: Activated?.Invoke(this, EventArgs.Empty); e.Handled = true; return;
      case Keys.Apps: if (selected != null) MenuRequested?.Invoke(selected, ItemRect(i).Location + new Size(S(40), S(20))); return;
    }
    if (view.Count > 0 && next != i) { Select(view[Math.Max(0, Math.Min(view.Count - 1, next))]); e.Handled = true; }
    base.OnKeyDown(e);
  }

  protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
  protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

  // ---- painting

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Theme.Bg);
    Theme.Smooth(g);
    if (view.Count == 0) {
      var r = new Rectangle(S(40), HeaderH, ViewW - S(80), Height - HeaderH);
      Glyphs.Draw(g, Glyph.Disc, new RectangleF(ViewW / 2f - S(28), Height / 2f - S(80), S(56), S(56)), Theme.Border, 1.4f);
      TextRenderer.DrawText(g, filter.Length > 0 ? L.T("Nenhum jogo com esse nome.", "No game matches.") : EmptyText, Theme.UI(10.5f),
                            new Rectangle(r.X, Height / 2 - S(10), r.Width, S(60)), Theme.Text3, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }
    for (int i = 0; i < view.Count; ++i) {
      var r = ItemRect(i);
      if (r.Bottom < HeaderH || r.Top > Height) continue;
      if (grid) PaintTile(g, view[i], r); else PaintRow(g, view[i], r);
    }
    if (!grid) PaintHeader(g);
    if (MaxScroll > 0) {
      var th = Thumb;
      float w = barHot || barDrag ? S(8) : S(4);
      using (var path = Theme.Round(new RectangleF(Width - w - S(3), th.Y, w, th.Height), w / 2))
      using (var b = new SolidBrush(barHot || barDrag ? Theme.Text3 : Theme.Border)) g.FillPath(b, path);
    }
  }

  void PaintHeader(Graphics g) {
    var cx = ColumnX();
    using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 0, 0, ViewW, HeaderH);
    using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, HeaderH - 1, ViewW, HeaderH - 1);
    string[] names = { L.T("NOME", "NAME"), L.T("SERIAL", "SERIAL"), L.T("ESTADO", "STATUS"), L.T("DISCO", "DISC") };
    int[] starts = { S(18), cx[1], cx[2], cx[3] };
    int[] ends = { cx[1], cx[2], cx[3], cx[4] };
    var font = Theme.Semi(8.5f);
    for (int c = 0; c < 4; ++c) {
      var color = c == sortColumn ? Theme.Text : hoverHeader == c ? Theme.Text2 : Theme.Text3;
      int tx = c == 0 ? cx[0] : starts[c];
      var size = TextRenderer.MeasureText(names[c], font);
      TextRenderer.DrawText(g, names[c], font, new Point(tx, (HeaderH - size.Height) / 2), color, TextFormatFlags.NoPadding);
      if (c == sortColumn) {
        float s = S(14);
        Glyphs.Draw(g, sortDown ? Glyph.ChevronDown : Glyph.ChevronUp, new RectangleF(tx + size.Width + S(4), (HeaderH - s) / 2f, s, s), Theme.Accent, 2f);
      }
    }
  }

  void PaintRow(Graphics g, GameEntry x, Rectangle r) {
    var cx = ColumnX();
    bool sel = x == selected;
    var band = new RectangleF(S(8), r.Y + S(3), r.Width - S(16), r.Height - S(6));
    if (sel || x == hover)
      using (var path = Theme.Round(band, S(8)))
      using (var b = new SolidBrush(sel ? (Focused ? Theme.Selection : Theme.Surface2) : Theme.Surface)) g.FillPath(b, path);
    if (sel) using (var b = new SolidBrush(Theme.Accent)) using (var path = Theme.Round(new RectangleF(S(8), r.Y + S(16), S(3), r.Height - S(32)), S(1.5f))) g.FillPath(b, path);
    // cover
    var cover = new RectangleF(S(22), r.Y + S(8), S(38), r.Height - S(16));
    DrawCover(g, x, cover, S(4), false);
    // name and the line under it
    int textW = cx[1] - cx[0] - S(16);
    TextRenderer.DrawText(g, x.Name, Theme.Semi(10.5f), new Rectangle(cx[0], r.Y + S(12), textW, S(22)), Theme.Text,
                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    TextRenderer.DrawText(g, x.Platform ?? "", Theme.UI(9f), new Rectangle(cx[0], r.Y + S(34), textW, S(18)), Theme.Text3,
                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
    var mid = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    TextRenderer.DrawText(g, x.Serial ?? "—", new Font("Consolas", 9.5f), new Rectangle(cx[1], r.Y, cx[2] - cx[1] - S(12), r.Height), x.Serial != null ? Theme.Text2 : Theme.Text3, mid);
    Pill(g, x.StateText, x.StateColor, new Point(cx[2], r.Y + r.Height / 2), false);
    TextRenderer.DrawText(g, x.Disc != null ? System.IO.Path.GetFileName(x.Disc) : "—", Theme.UI(9f), new Rectangle(cx[3], r.Y, cx[4] - cx[3], r.Height), x.Disc != null ? Theme.Text2 : Theme.Text3,
                          mid | TextFormatFlags.PathEllipsis);
  }

  void PaintTile(Graphics g, GameEntry x, Rectangle r) {
    bool sel = x == selected, hot = x == hover;
    var cover = new RectangleF(r.X, r.Y, r.Width, (int)(r.Width * 1.38f));
    float lift = hot || sel ? S(3) : 0;
    cover.Y -= lift;
    // soft shadow
    for (int i = 3; i >= 1; --i)
      using (var path = Theme.Round(new RectangleF(cover.X - i + S(1), cover.Y + i * 1.6f + S(1), cover.Width + i * 2 - S(2), cover.Height), S(10)))
      using (var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 26 : 12, 0, 0, 0))) g.FillPath(b, path);
    DrawCover(g, x, cover, S(10), true);
    if (sel) {
      var ring = RectangleF.Inflate(cover, S(3), S(3));
      using (var path = Theme.Round(ring, S(12))) using (var p = new Pen(Focused ? Theme.Accent : Theme.Text3, S(2.2f))) g.DrawPath(p, path);
    }
    // status badge on the cover
    Pill(g, x.StateText, x.StateColor, new Point((int)cover.X + S(8), (int)cover.Bottom - S(18)), true);
    var nameRect = new Rectangle(r.X - S(2), (int)(r.Y + r.Width * 1.38f) + S(10), r.Width + S(4), S(36));
    TextRenderer.DrawText(g, x.Name, Theme.Semi(tileSize == 0 ? 9f : 9.75f), nameRect, sel ? Theme.Text : Theme.Text2,
                          TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
  }

  // A rounded tinted label: "● Ready". solid: drawn over a cover (dark backing for contrast).
  public static void Pill(Graphics g, string text, Color color, Point leftMiddle, bool solid) {
    float k = g.DpiX / 96f;
    var font = Theme.Semi(solid ? 8f : 8.75f);
    var size = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding);
    float h = (solid ? 20 : 24) * k, dot = 6 * k, padL = 10 * k, padR = 11 * k;
    var r = new RectangleF(leftMiddle.X, leftMiddle.Y - h / 2, padL + dot + 6 * k + size.Width + padR, h);
    using (var path = Theme.Round(r, h / 2)) {
      using (var b = new SolidBrush(solid ? Color.FromArgb(215, 14, 16, 20) : Color.FromArgb(Theme.Dark ? 40 : 30, color))) g.FillPath(b, path);
    }
    using (var b = new SolidBrush(solid ? Lighten(color) : color)) g.FillEllipse(b, r.X + padL, r.Y + (h - dot) / 2, dot, dot);
    TextRenderer.DrawText(g, text, font, new Point((int)(r.X + padL + dot + 6 * k), (int)(r.Y + (h - size.Height) / 2)),
                          solid ? Color.FromArgb(236, 238, 242) : (Theme.Dark ? Lighten(color) : color), TextFormatFlags.NoPadding);
  }

  static Color Lighten(Color c) => c == Theme.Text3 ? Color.FromArgb(180, 186, 196)
      : Color.FromArgb(Math.Min(255, c.R + 30), Math.Min(255, c.G + 30), Math.Min(255, c.B + 30));

  // The cover, or a tile drawn from the name when there is none.
  public static void DrawCover(Graphics g, GameEntry x, RectangleF r, float radius, bool large) {
    using (var clip = Theme.Round(r, radius)) {
      var state = g.Save();
      g.SetClip(clip, CombineMode.Intersect);
      if (x.Cover != null) {
        using (var b = new SolidBrush(Color.FromArgb(18, 20, 24))) g.FillRectangle(b, r);
        // Fill the box (a cover is about the box's shape); a wide icon (PSP) is fitted whole on a blurred-looking backdrop.
        float ar = x.Cover.Width / (float)x.Cover.Height, br = r.Width / r.Height;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (Math.Abs(ar - br) < 0.25f) {
          float w = ar > br ? r.Height * ar : r.Width, h = ar > br ? r.Height : r.Width / ar;
          g.DrawImage(x.Cover, r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        } else {
          float hb = r.Height, wb = hb * ar;
          using (var ia = new System.Drawing.Imaging.ImageAttributes()) {
            var m = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f };
            ia.SetColorMatrix(m);
            g.DrawImage(x.Cover, new Rectangle((int)(r.X + (r.Width - wb) / 2), (int)r.Y, (int)wb, (int)hb), 0, 0, x.Cover.Width, x.Cover.Height, GraphicsUnit.Pixel, ia);
          }
          float w = r.Width, h = w / ar;
          g.DrawImage(x.Cover, r.X, r.Y + (r.Height - h) / 2, w, h);
        }
      } else {
        int hash = 0;
        foreach (char c in x.Name ?? "") hash = hash * 31 + c;
        float hue = (Math.Abs(hash) % 360);
        var c1 = FromHsl(hue, 0.42f, Theme.Dark ? 0.30f : 0.46f);
        var c2 = FromHsl((hue + 38) % 360, 0.50f, Theme.Dark ? 0.16f : 0.30f);
        using (var b = new LinearGradientBrush(r, c1, c2, 55f)) g.FillRectangle(b, r);
        // concentric rings: a disc, our motif
        float d = Math.Max(r.Width, r.Height) * 0.95f;
        var center = new PointF(r.Right - r.Width * 0.18f, r.Bottom - r.Height * 0.16f);
        using (var p = new Pen(Color.FromArgb(30, 255, 255, 255), Math.Max(1f, r.Width / 60f)))
          for (int i = 1; i <= 4; ++i) { float rr = d * i / 7f; g.DrawEllipse(p, center.X - rr, center.Y - rr, rr * 2, rr * 2); }
        string initials = Initials(x.Name);
        using (var f = new Font("Segoe UI Semibold", Math.Max(7f, r.Width / (large ? 4.2f : 3.4f) / (g.DpiX / 96f)), GraphicsUnit.Point)) {
          var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;
          TextRenderer.DrawText(g, initials, f, Rectangle.Round(new RectangleF(r.X, r.Y, r.Width, r.Height * (large ? 0.78f : 1f))), Color.FromArgb(235, 255, 255, 255), flags);
        }
      }
      // a fine inner edge
      g.Restore(state);
      using (var p = new Pen(Color.FromArgb(Theme.Dark ? 30 : 40, Theme.Dark ? 255 : 0, Theme.Dark ? 255 : 0, Theme.Dark ? 255 : 0))) g.DrawPath(p, clip);
    }
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

  static Color FromHsl(float h, float s, float l) {
    float c = (1 - Math.Abs(2 * l - 1)) * s, hp = h / 60f, x = c * (1 - Math.Abs(hp % 2 - 1)), m = l - c / 2;
    float r = 0, g = 0, b = 0;
    if (hp < 1) { r = c; g = x; } else if (hp < 2) { r = x; g = c; } else if (hp < 3) { g = c; b = x; }
    else if (hp < 4) { g = x; b = c; } else if (hp < 5) { r = x; b = c; } else { r = c; b = x; }
    return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
  }
}

// The selected game, large, at the right of the library: cover, name, state and what to do next.
sealed class CoverView : Control {
  public GameEntry Entry;
  public CoverView() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Parent?.BackColor ?? Theme.Surface);
    Theme.Smooth(g);
    if (Entry == null) return;
    float k = DeviceDpi / 96f;
    float w = Math.Min(Width, Height / 1.38f), h = w * 1.38f;
    var r = new RectangleF((Width - w) / 2, 0, w, h - 6 * k);
    for (int i = 4; i >= 1; --i)
      using (var path = Theme.Round(new RectangleF(r.X - i, r.Y + i * 2f, r.Width + i * 2, r.Height), 12 * k))
      using (var b = new SolidBrush(Color.FromArgb(Theme.Dark ? 24 : 10, 0, 0, 0))) g.FillPath(b, path);
    GameLibrary.DrawCover(g, Entry, r, 12 * k, true);
  }
}

// Folder picker that can take several folders at once (the Vista+ file dialog in folder mode).
static class FolderPicker {
  [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogCom { }
  [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IFileOpenDialog {
    [PreserveSig] int Show(IntPtr parent);
    void SetFileTypes(); void SetFileTypeIndex(); void GetFileTypeIndex(); void Advise(); void Unadvise();
    void SetOptions(uint fos); void GetOptions(out uint fos);
    void SetDefaultFolder(IShellItem si); void SetFolder(IShellItem si); void GetFolder(); void GetCurrentSelection();
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name); void GetFileName();
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text); void SetFileNameLabel(); void GetResult(out IShellItem item);
    void AddPlace(); void SetDefaultExtension(); void Close(); void SetClientGuid(); void ClearClientData(); void SetFilter();
    void GetResults(out IShellItemArray items); void GetSelectedItems();
  }
  [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellItem { void BindToHandler(); void GetParent(); void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name); void GetAttributes(); void Compare(); }
  [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IShellItemArray { void BindToHandler(); void GetPropertyStore(); void GetPropertyDescriptionList(); void GetAttributes(); void GetCount(out uint n); void GetItemAt(uint i, out IShellItem item); void EnumItems(); }

  public static List<string> Pick(IWin32Window owner, string title) {
    var list = new List<string>();
    try {
      var dlg = (IFileOpenDialog)new FileOpenDialogCom();
      dlg.SetOptions(0x20 | 0x40 | 0x200 | 0x800);   // PICKFOLDERS | FORCEFILESYSTEM | ALLOWMULTISELECT | PATHMUSTEXIST
      dlg.SetTitle(title);
      if (dlg.Show(owner?.Handle ?? IntPtr.Zero) != 0) return list;
      dlg.GetResults(out var items);
      items.GetCount(out uint n);
      for (uint i = 0; i < n; ++i) { items.GetItemAt(i, out var item); item.GetDisplayName(0x80058000, out string path); list.Add(path); }
      return list;
    } catch (Exception) {
      using (var d = new FolderBrowserDialog { Description = title }) if (d.ShowDialog(owner) == DialogResult.OK) list.Add(d.SelectedPath);
      return list;
    }
  }
}

}
