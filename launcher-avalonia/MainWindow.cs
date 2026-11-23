// The launcher window on Linux and macOS, laid out like the Windows one (launcher/MainForm.cs): a menu bar (macOS: the
// system's menu bar), a toolbar, the console tabs and the game library, the selected game at the right, the log at the
// bottom and a status bar. The decisions (entries, texts, setup and play commands) come from launcher/Core.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PortLauncher {

sealed partial class MainWindow : Window {
  GameConfig cfg;
  string root;
  Recomp recomp;
  RecompGame game;
  readonly RecompRegistry registry;
  bool Lib => registry != null;
  string pendingSource;
  public string NextGame;
  public bool Restart;
  public string RootForRestart => root;

  readonly GameLibrary library;
  readonly ConsoleTabs tabs;
  readonly FlatButton emptyButton;
  readonly InfoBar folderBar = new InfoBar(), updateBar = new InfoBar();
  Border detail, logPanel;
  GridSplitter logSplitter;
  RowDefinition logRow;
  CoverView detailCover;
  TextBlock detailName, detailSub, detailNote;
  PillLabel detailState;
  FlatButton detailPrimary, detailSettings, detailMore;
  Grid detailActions;
  StackPanel detailFacts;
  ToolButton tbOpen, tbFolders, tbRefresh, tbPlay, tbStop, tbInstall, tbSettings, tbDeps, tbRecomps;
  ViewSwitch viewSwitch;
  SearchBox search;
  TextBlock statusLeft, statusRight, stepLabel;
  FlatButton cancelButton;
  TextBox log;
  readonly StringBuilder logText = new StringBuilder();
  ProgressLine progress;
  Process running;
  CancellationTokenSource copying;   // the game files being copied from the disc (GameInstall, in this process)
  bool runningSetup;
  List<FoundDisc> found = new List<FoundDisc>();
  CancellationTokenSource scanCancel;
  bool scanning;
  int coverPass;
  string startKey;
  Menus menus;

  public MainWindow(GameConfig config, string kitDir, RecompRegistry registry = null, string selectKey = null) {
    cfg = config;
    root = kitDir;
    this.registry = registry;
    startKey = selectKey;
    Title = TitleText();
    Width = 1240; Height = 760; MinWidth = 980; MinHeight = 620;
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
    Background = Look.B(Look.Bg);
    Foreground = Look.B(Look.Text);
    FontFamily = Look.UiFamily;
    FontSize = Look.Pt(10);
    App.SetIcon(this);

    library = new GameLibrary { Grid = Prefs.Bool("grid", true), TileSize = Prefs.Int("tile_size", 1) };
    library.EmptyText = Lib
        ? L.T("Nenhum jogo ainda. Adicione uma ou mais pastas onde ficam as suas ISOs: o launcher procura nelas (e nas subpastas), reconhece o console e o jogo de cada disco e mostra as capas.",
              "No games yet. Add one or more folders that hold your ISOs: the launcher searches them (and their subfolders), recognises each disc's console and game and shows the covers.")
        : "";
    tabs = new ConsoleTabs { IsVisible = Lib };
    tabs.Changed += (s, e) => { library.Console = tabs.Selected; Prefs.Set("console_tab", tabs.Selected ?? ""); UpdateEmpty(); };
    library.ConsoleReset += (s, e) => { tabs.Selected = null; tabs.InvalidateVisual(); };
    emptyButton = new FlatButton(L.T("ADICIONAR PASTA DE JOGOS", "ADD A GAME FOLDER"), ButtonKind.Primary, 300, 48) {
      FontPt = 11.5, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 128, 0, 0),
    };
    emptyButton.Click += (s, e) => AddFolders();
    library.SelectionChanged += (s, e) => OnSelected();
    library.Activated += (s, e) => PrimaryAction();
    library.MenuRequested += (entry, at) => ShowGameMenu(library, at);

    menus = new Menus(this);
    var toolbar = BuildToolbar();
    var status = BuildStatusBar();
    BuildLogPanel();
    BuildDetail();
    viewSwitch.Grid = library.Grid;

    // Centre: tabs over the library, the log under them; the game panel at the right.
    var libraryHost = new Grid();
    libraryHost.Children.Add(library);
    libraryHost.Children.Add(emptyButton);
    var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
    logRow = left.RowDefinitions[3];
    Grid.SetRow(tabs, 0); Grid.SetRow(libraryHost, 1); Grid.SetRow(logSplitter, 2); Grid.SetRow(logPanel, 3);
    left.Children.Add(tabs); left.Children.Add(libraryHost); left.Children.Add(logSplitter); left.Children.Add(logPanel);
    var centre = new DockPanel();
    DockPanel.SetDock(detail, Dock.Right);
    centre.Children.Add(detail);
    centre.Children.Add(left);

    var rootPanel = new DockPanel();
    var menuBar = menus.Bar();
    if (menuBar != null) { DockPanel.SetDock(menuBar, Dock.Top); rootPanel.Children.Add(menuBar); }
    foreach (var c in new Control[] { toolbar, folderBar, updateBar }) { DockPanel.SetDock(c, Dock.Top); rootPanel.Children.Add(c); }
    DockPanel.SetDock(status, Dock.Bottom);
    rootPanel.Children.Add(status);
    rootPanel.Children.Add(centre);
    Content = rootPanel;

    SetLogVisible(Prefs.Bool("show_log", false), false);
    detail.IsVisible = Prefs.Bool("show_details", true);

    Closing += OnClosing;
    KeyDown += OnKey;
    DragDrop.SetAllowDrop(this, true);
    AddHandler(DragDrop.DragOverEvent, (s, e) => { e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None; });
    AddHandler(DragDrop.DropEvent, (s, e) => OnDrop(e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToArray()));
    Opened += (s, e) => {
      library.Focus();
      Displays.Provider = () => Screens.All.Select((sc, i) => new MonitorInfo { Device = null, Name = sc.Bounds.Width + "x" + sc.Bounds.Height + (sc.IsPrimary ? L.T(" (principal)", " (main)") : "") })
                                            .OrderByDescending(m => m.Name.Contains("(")).ToList();
      if (pendingSource != null) { string src = pendingSource; pendingSource = null; Install(src); }
      Rescan();
      if (ShotFile == null) CheckUpdates(false);
    };
    BuildEntries();
    ShowFolderHint();
    RefreshStatus();
  }

  string TitleText() => Lib ? "Port launcher" : cfg.Game + " — Launcher";
  bool Busy => copying != null || (running != null && !HasExited(running));
  static bool HasExited(Process p) { try { return p.HasExited; } catch (Exception) { return true; } }
  bool SetupRunning => copying != null || (Busy && runningSetup);
  GameEntry Current => library?.Selected;
  string GameExe => root == null || cfg.Exe == null ? null : Path.Combine(root, cfg.Exe);
  string IniPath => root == null || cfg.Ini == null ? null : Path.Combine(root, cfg.Ini);
  bool Installed => GameExe != null && File.Exists(GameExe);
  string RepoName => string.IsNullOrEmpty(cfg.Repo) ? "game" : cfg.Repo.Substring(cfg.Repo.LastIndexOf('/') + 1);
  bool IsCurrent(GameEntry e) => e != null && (!Lib ? e.Key == "kit" : e.Game != null && e.Game == game);

  void UpdateEmpty() {
    emptyButton.IsVisible = Lib && library.Entries.Count == 0 && !scanning;
  }

  // ------------------------------------------------------------------ toolbar

  Control BuildToolbar() {
    tbOpen = new ToolButton(Glyph.Disc, L.T("Abrir ISO", "Open ISO"));
    tbOpen.Click += (s, e) => ChooseDisc(null);
    tbFolders = new ToolButton(Glyph.FolderAdd, L.T("Pastas", "Folders"));
    tbFolders.Click += (s, e) => ManageFolders();
    tbRefresh = new ToolButton(Glyph.Refresh, L.T("Atualizar", "Refresh"));
    tbRefresh.Click += (s, e) => Rescan();
    tbPlay = new ToolButton(Glyph.Play, L.T("Jogar", "Play")) { Tint = Look.Accent };
    tbPlay.Click += (s, e) => Play();
    tbStop = new ToolButton(Glyph.Stop, L.T("Parar", "Stop")) { Tint = Look.Danger };
    tbStop.Click += (s, e) => StopRunning();
    tbInstall = new ToolButton(Glyph.Install, L.T("Instalar", "Install"), 84);
    tbInstall.Click += (s, e) => PrimaryInstall();
    tbSettings = new ToolButton(Glyph.Gear, L.T("Configurar", "Settings"), 80);
    tbSettings.Click += (s, e) => ShowSettings();
    tbDeps = new ToolButton(Glyph.Box, L.T("Dependências", "Dependencies"), 92);
    tbDeps.Click += (s, e) => ShowDeps();
    var tbPads = new ToolButton(Glyph.Gear, L.T("Controles", "Controllers"), 84);
    tbPads.Click += (s, e) => ShowControllers();
    tbRecomps = new ToolButton(Glyph.Update, L.T("Recompiladores", "Recompilers"), 104);
    tbRecomps.Click += (s, e) => ShowRecomps();
    var tbLog = new ToolButton(Glyph.Log, "Log");
    tbLog.Click += (s, e) => SetLogVisible(!logPanel.IsVisible);
    var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
    foreach (var c in new Control[] { tbOpen, tbFolders, tbRefresh, new ToolSeparator(), tbPlay, tbStop, tbInstall, new ToolSeparator(), tbSettings, tbDeps, tbPads, tbRecomps, tbLog })
      left.Children.Add(c);
    if (!Lib) tbFolders.IsVisible = tbRefresh.IsVisible = tbRecomps.IsVisible = false;
    viewSwitch = new ViewSwitch { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    viewSwitch.Changed += (s, e) => { if (library.Grid != viewSwitch.Grid) SetGrid(viewSwitch.Grid); };
    search = new SearchBox(L.T("Procurar jogos", "Search games")) { Width = 260, VerticalAlignment = VerticalAlignment.Center };
    search.Box.TextChanged += (s, e) => library.Filter = search.Box.Text;
    search.Box.KeyDown += (s, e) => {
      if (e.Key == Key.Escape) { search.Box.Text = ""; library.Focus(); e.Handled = true; }
      if (e.Key == Key.Down || e.Key == Key.Enter) { library.Focus(); e.Handled = true; }
    };
    var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    right.Children.Add(viewSwitch);
    right.Children.Add(search);
    var bar = new DockPanel { Margin = new Thickness(10, 6, 16, 6) };
    DockPanel.SetDock(right, Dock.Right);
    bar.Children.Add(right);
    bar.Children.Add(left);
    return new Border { Background = Look.B(Look.Surface), BorderBrush = Look.B(Look.Border), BorderThickness = new Thickness(0, 0, 0, 1), Height = 72, Child = bar };
  }

  void OnKey(object sender, KeyEventArgs e) {
    var mod = OS.Mac ? KeyModifiers.Meta : KeyModifiers.Control;
    if (e.KeyModifiers == mod && e.Key == Key.F) { search.Box.Focus(); search.Box.SelectAll(); e.Handled = true; return; }
    if (menus.HandleKey(e)) e.Handled = true;
  }

  // ------------------------------------------------------------------ the selected game (right panel)

  static TextBlock Words(string text, Typeface face, double points, Color color, Thickness margin, bool wrap = true) =>
      new TextBlock { Text = text, FontFamily = face.FontFamily, FontWeight = face.Weight, FontSize = Look.Pt(points), Foreground = Look.B(color), Margin = margin,
                      TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };

  void BuildDetail() {
    detailCover = new CoverView { Height = 340, Margin = new Thickness(0, 0, 0, 18) };
    detailName = Words("", Look.Semi, 15, Look.Text, new Thickness(0, 0, 0, 4));
    detailSub = Words("", Look.UI(), 9.5, Look.Text3, new Thickness(0, 0, 0, 12));
    detailState = new PillLabel { Margin = new Thickness(0, 0, 0, 18) };
    detailPrimary = new FlatButton(L.T("JOGAR", "PLAY"), ButtonKind.Primary, double.NaN, 48) { FontPt = 12, Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Stretch };
    detailPrimary.Click += (s, e) => PrimaryAction();
    detailSettings = new FlatButton(L.T("Configurar", "Settings"), ButtonKind.Secondary, double.NaN, 38) { Margin = new Thickness(0, 0, 5, 0) };
    detailSettings.Click += (s, e) => ShowSettings();
    detailMore = new FlatButton(L.T("Mais...", "More..."), ButtonKind.Secondary, double.NaN, 38) { Margin = new Thickness(5, 0, 0, 0) };
    detailMore.Click += (s, e) => ShowGameMenu(detailMore, new Point(0, detailMore.Bounds.Height + 4));
    detailActions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 0, 0, 18) };
    Grid.SetColumn(detailMore, 1);
    detailActions.Children.Add(detailSettings);
    detailActions.Children.Add(detailMore);
    detailFacts = new StackPanel();
    detailNote = Words("", Look.UI(), 9, Look.Text3, new Thickness(0, 14, 0, 0));
    detailBelow = new StackPanel();
    foreach (var c in new Control[] { detailName, detailSub, detailState, detailPrimary, detailActions, detailFacts, detailNote }) detailBelow.Children.Add(c);
    var stack = new StackPanel();
    stack.Children.Add(detailCover);
    stack.Children.Add(detailBelow);
    detail = new Border {
      Width = 330, Background = Look.B(Look.Surface), BorderBrush = Look.B(Look.Border), BorderThickness = new Thickness(1, 0, 0, 0),
      Padding = new Thickness(24, 22, 24, 18),
      Child = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
    };
    detail.SizeChanged += (s, e) => FitCover();
  }

  StackPanel detailBelow;

  // The cover takes what the text and buttons leave (no scrolling at the usual window sizes), like the Windows launcher.
  void FitCover() {
    if (detailBelow == null || detail.Bounds.Width <= 0) return;
    double width = detail.Bounds.Width - detail.Padding.Left - detail.Padding.Right - 1;
    detailBelow.Measure(new Size(width, double.PositiveInfinity));
    double room = detail.Bounds.Height - detail.Padding.Top - detail.Padding.Bottom - detailBelow.DesiredSize.Height - detailCover.Margin.Bottom - 4;
    double h = Math.Max(140, Math.Min(width * 1.38, room));
    if (Math.Abs(detailCover.Height - h) > 0.5) detailCover.Height = h;
  }

  void Fact(string label, string value) {
    if (string.IsNullOrEmpty(value)) return;
    detailFacts.Children.Add(Words(label.ToUpperInvariant(), Look.Semi, 8, Look.Text3, new Thickness(0, 10, 0, 2)));
    detailFacts.Children.Add(Words(value, Look.UI(), 9.25, Look.Text2, new Thickness(0)));
  }

  void RenderDetail() {
    var e = Current;
    detailCover.Entry = e;
    detailCover.InvalidateVisual();
    detailFacts.Children.Clear();
    if (e == null) {
      detailName.Text = L.T("Nenhum jogo selecionado", "No game selected");
      detailSub.Text = ""; detailState.IsVisible = false; detailPrimary.IsVisible = false; detailNote.Text = "";
      detailActions.IsVisible = false;
      return;
    }
    bool current = IsCurrent(e);
    detailName.Text = e.Name;
    detailSub.Text = string.Join("   ·   ", new[] { e.Serial, e.Platform }.Where(x => !string.IsNullOrEmpty(x)));
    detailState.IsVisible = true;
    detailState.Set(SetupRunning && current ? L.T("Instalando...", "Installing...") : Busy && current ? L.T("Em jogo", "Playing") : e.StateText,
                    SetupRunning && current ? Look.Blue : e.StateColor);
    detailPrimary.IsVisible = true;
    detailActions.IsVisible = true;
    detailPrimary.Text = Texts.Primary(e, Lib);
    detailPrimary.Kind = e.State == EntryState.Ready ? ButtonKind.Primary : ButtonKind.Blue;
    detailPrimary.IsEnabled = !Busy && !e.NoRuntime;
    detailPrimary.InvalidateVisual();
    detailSettings.IsEnabled = current && !e.NoProfile && !e.NoRuntime;
    if (e.Disc != null) Fact(L.T("Disco", "Disc"), Path.GetFileName(e.Disc) + "\n" + Path.GetDirectoryName(e.Disc));
    if (e.Dir != null) Fact(L.T("Pasta do jogo", "Game folder"), e.Recomp != null && e.Dir.StartsWith(e.Recomp.Root, StringComparison.Ordinal)
                                                                     ? e.Recomp.Name + e.Dir.Substring(e.Recomp.Root.Length) : e.Dir);
    if (Lib && e.Console != null) Fact(L.T("Recompilador", "Recompiler"), e.Recomp?.Name ?? e.Known?.Name ?? L.T("nenhum para este console", "none for this console"));
    if (current && !e.NoProfile && !e.NoRuntime && !Installed) {
      if (cfg.DiskGb > 0) Fact(L.T("Espaço necessário", "Space needed"), "~" + cfg.DiskGb + " GB");
      if (!string.IsNullOrEmpty(cfg.Minutes)) Fact(L.T("Primeira montagem", "First build"), L.T("cerca de ", "about ") + cfg.Minutes + " min");
    }
    detailNote.Text = Texts.DetailNote(e, cfg);
    Dispatcher.UIThread.Post(FitCover, DispatcherPriority.Background);
  }

  // ------------------------------------------------------------------ log drawer and status bar

  void BuildLogPanel() {
    var title = Words("LOG", Look.Semi, 9, Look.Accent, new Thickness(0, 0, 16, 0), false);
    title.VerticalAlignment = VerticalAlignment.Center;
    stepLabel = Words(L.T("Nada em andamento.", "Nothing running."), Look.Semi, 9.5, Look.Text2, new Thickness(0), false);
    stepLabel.VerticalAlignment = VerticalAlignment.Center;
    progress = new ProgressLine { Width = 220, Maximum = 5, Margin = new Thickness(8, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    cancelButton = new FlatButton(L.T("Parar", "Stop"), ButtonKind.Danger, 96, 30) { FontPt = 9.5, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    cancelButton.Click += (s, e) => StopRunning();
    var hide = new FlatButton("", ButtonKind.Ghost, 30, 30) { VerticalAlignment = VerticalAlignment.Center };
    hide.Overlay = g => Glyphs.Draw(g, Glyph.ChevronDown, new Rect(5, 5, 20, 20), Look.Text2);
    hide.Click += (s, e) => SetLogVisible(false);
    var head = new DockPanel { Height = 42, Margin = new Thickness(18, 6, 10, 4) };
    foreach (var c in new Control[] { hide, cancelButton, progress }) { DockPanel.SetDock(c, Dock.Right); head.Children.Add(c); }
    DockPanel.SetDock(title, Dock.Left);
    head.Children.Add(title);
    head.Children.Add(stepLabel);
    log = new TextBox {
      AcceptsReturn = true, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0), Background = Look.B(Look.Bg),
      Foreground = Look.B(Look.Text2), FontFamily = Look.MonoFamily, FontSize = Look.Pt(9.5), Padding = new Thickness(18, 10, 6, 8),
    };
    log.Resources["TextControlBackgroundFocused"] = Look.B(Look.Bg);
    log.Resources["TextControlBackgroundPointerOver"] = Look.B(Look.Bg);
    var body = new DockPanel();
    DockPanel.SetDock(head, Dock.Top);
    body.Children.Add(head);
    body.Children.Add(new Border { BorderBrush = Look.B(Look.Border), BorderThickness = new Thickness(0, 1, 0, 0), Child = log });
    logPanel = new Border { Background = Look.B(Look.Surface), Child = body };
    logSplitter = new GridSplitter { Height = 5, Background = Look.B(Look.Border), ResizeDirection = GridResizeDirection.Rows, HorizontalAlignment = HorizontalAlignment.Stretch };
  }

  Control BuildStatusBar() {
    statusLeft = Words("", Look.UI(), 9, Look.Text2, new Thickness(0), false);
    statusLeft.VerticalAlignment = VerticalAlignment.Center;
    statusRight = Words("", Look.UI(), 9, Look.Text3, new Thickness(12, 0, 0, 0), false);
    statusRight.VerticalAlignment = VerticalAlignment.Center;
    var bar = new DockPanel { Margin = new Thickness(16, 0) };
    DockPanel.SetDock(statusRight, Dock.Right);
    bar.Children.Add(statusRight);
    bar.Children.Add(statusLeft);
    return new Border { Height = 30, Background = Look.B(Look.Surface), BorderBrush = Look.B(Look.Border), BorderThickness = new Thickness(0, 1, 0, 0), Child = bar };
  }

  public void SetLogVisible(bool on, bool save = true) {
    logPanel.IsVisible = logSplitter.IsVisible = on;
    logRow.Height = on ? new GridLength(Math.Max(120, logRow.Height.IsAbsolute && logRow.Height.Value > 0 ? logRow.Height.Value : 230)) : GridLength.Auto;
    if (save) Prefs.Set("show_log", on);
    menus?.Sync();
  }

  public void SetDetailVisible(bool on) { detail.IsVisible = on; Prefs.Set("show_details", on); }
  public bool DetailVisible => detail.IsVisible;
  public bool LogVisible => logPanel.IsVisible;

  void UpdateCounts() {
    if (!Lib) { statusRight.Text = Updater.Describe(); return; }
    int games = library.Entries.Count, ready = library.Entries.Count(x => x.State == EntryState.Ready);
    int folders = GameFolders.Load().Count;
    string text = L.T($"{games} jogos  ·  {ready} prontos  ·  {found.Count} discos em {folders} pasta{(folders == 1 ? "" : "s")}",
                      $"{games} games  ·  {ready} ready  ·  {found.Count} discs in {folders} folder{(folders == 1 ? "" : "s")}");
    if (scanning) text = L.T("Procurando jogos...  ·  ", "Scanning...  ·  ") + text;
    statusRight.Text = text + "  ·  " + Updater.Describe();
  }

  // ------------------------------------------------------------------ the library

  void BuildEntries() {
    var old = library.Entries.ToDictionary(x => x.Key + "|" + x.Disc, x => x);
    var entries = new List<GameEntry>();
    if (!Lib) entries.Add(new GameEntry { Key = "kit", Name = cfg.Game, Serial = cfg.Disc, Dir = root, Installed = Installed, Platform = L.T("Port para PC", "PC port") });
    else entries = Entries.Library(registry, found, game, cfg);
    foreach (var e in entries)
      if (old.TryGetValue(e.Key + "|" + e.Disc, out var prev)) { e.Cover = prev.Cover; e.CoverDone = prev.CoverDone; }
    library.SetEntries(entries, startKey ?? Current?.Key ?? (Prefs.Text("last_game", "") is var lg && lg.Length > 0 ? lg : null) ?? "kit");
    if (startKey != null && entries.Any(x => x.Key == startKey)) startKey = null;
    if (Lib) {
      var list = new List<ConsoleTabs.Tab> { new ConsoleTabs.Tab { Id = null, Text = L.T("Todos", "All"), Count = entries.Count } };
      foreach (string c in Consoles.Order) {
        int n = entries.Count(x => x.Console == c);
        if (n > 0) list.Add(new ConsoleTabs.Tab { Id = c, Text = Consoles.Short(c), Count = n });
      }
      string want = tabs.Selected ?? Prefs.Text("console_tab", "");
      tabs.Set(list, want.Length > 0 ? want : null);
      library.Console = tabs.Selected;
    }
    LoadCovers();
    UpdateCounts();
    UpdateEmpty();
  }

  void LoadCovers() {
    int pass = ++coverPass;
    var todo = library.Entries.Where(x => !x.CoverDone).ToList();
    if (todo.Count == 0) return;
    bool online = Prefs.Bool("online_covers", true);
    Task.Run(() => {
      foreach (var e in todo) {
        if (pass != coverPass) return;
        Bitmap b = null;
        string url = e.Recomp?.CoverUrl ?? (e.Console == "ps2" ? Covers.DefaultPs2Url : null);
        try { b = Covers.Find(e.Key, e.Serial, e.Dir, e.Disc, url, online, CoverThumb.Load); } catch (Exception) { }
        Dispatcher.UIThread.Post(() => {
          e.Cover = b; e.CoverDone = true;
          library.InvalidateVisual();
          if (e == Current) detailCover.InvalidateVisual();
        });
      }
    });
  }

  public void ReloadCovers() {
    foreach (var e in library.Entries) { e.CoverDone = false; e.Cover = null; }
    library.InvalidateVisual();
    LoadCovers();
  }

  public async void ChooseCover() {
    var e = Current;
    if (e == null) return;
    string file = await Pickers.File(this, L.T("Escolha a capa de ", "Choose the cover of ") + e.Name, L.T("Imagens", "Images"), "*.png", "*.jpg", "*.jpeg");
    if (file == null) return;
    try { Covers.SetCustom(e.Key, file); } catch (Exception ex) { await Dialog.Info(this, Title, ex.Message); return; }
    e.CoverDone = false; e.Cover = null;
    LoadCovers();
  }

  public void ClearCover() {
    var e = Current;
    if (e == null) return;
    Covers.ClearCustom(e.Key);
    ReloadCovers();
  }

  void OnSelected() {
    var e = Current;
    if (e != null && Lib && !Busy) {
      recomp = e.Recomp;
      if (e.Game != null && e.Game != game) {
        try {
          cfg = e.Recomp.ConfigFor(e.Game);
          game = e.Game;
          root = game.Dir;
        } catch (Exception ex) { AppendLog(e.Game.Id + ": " + ex.Message); }
      } else if (e.Game == null) {
        game = null;
        root = null;
      }
      Prefs.Set("last_game", e.Key);
    }
    RefreshStatus();
  }

  void SelectGame(string id) {
    library.SelectKey(id.Contains("/") || id.StartsWith("disc:") ? id : (registry?.KnownFor(recomp)?.Id + "/" + id));
    OnSelected();
  }

  // Enter / double-click / the panel's big button: whatever brings the game closer to playing.
  public async void PrimaryAction() {
    var e = Current;
    if (e == null || Busy) return;
    if (e.NoRuntime) { await Dialog.Info(this, e.Name, Texts.NoRuntimeNote(e)); return; }
    if (e.NeedsRecomp) { if (e.Known != null) DownloadRecomp(e.Known); return; }
    if (e.Unsupported) { await Dialog.Info(this, e.Name, Texts.DetailNote(e, cfg)); return; }
    if (e.NoProfile) { AddGenericPort(e.Disc, null); return; }
    if (!IsCurrent(e)) return;
    if (Installed) { Play(); return; }
    if (e.Disc != null || !Lib) { Install(e.Disc); return; }
    ChooseDisc(e);
  }

  public void PrimaryInstall() {
    var e = Current;
    if (e == null || Busy) return;
    if (e.NeedsRecomp || e.Unsupported || e.NoRuntime) PrimaryAction();
    else if (e.NoProfile) AddGenericPort(e.Disc, null);
    else if (IsCurrent(e)) Install(e.Disc);
  }

  void ShowFolderHint() {
    if (!Lib || GameFolders.Load().Count > 0 || library.Entries.Count == 0) { folderBar.IsVisible = false; return; }
    folderBar.Show(Glyph.FolderAdd, Look.Accent,
        L.T("Adicione as pastas onde ficam as suas ISOs: o launcher procura nelas (e nas subpastas) e reconhece cada jogo pelo disco.",
            "Add the folders that hold your ISOs: the launcher searches them (and their subfolders) and recognises each game from its disc."),
        new KeyValuePair<string, Action>(L.T("Adicionar pastas...", "Add folders..."), AddFolders),
        new KeyValuePair<string, Action>(L.T("Agora não", "Not now"), () => folderBar.IsVisible = false));
  }

  public async void AddFolders() {
    if (!Lib) return;
    var picked = await Pickers.Folders(this, L.T("Escolha uma ou mais pastas com jogos (ISOs)", "Choose one or more folders with games (ISOs)"));
    if (picked.Count == 0) return;
    GameFolders.Add(picked);
    ShowFolderHint();
    Rescan();
  }

  public async void ManageFolders() {
    if (!Lib) return;
    await new FoldersWindow().ShowDialog(this);
    ShowFolderHint();
    Rescan();
  }

  public async void Rescan() {
    if (!Lib) return;
    scanCancel?.Cancel();
    var cancel = scanCancel = new CancellationTokenSource();
    var folders = GameFolders.Load();
    if (folders.Count == 0) { found = new List<FoundDisc>(); BuildEntries(); RefreshStatus(); return; }
    scanning = true;
    tbRefresh.IsEnabled = false;
    UpdateCounts();
    var r = registry;
    List<FoundDisc> result;
    try {
      result = await Task.Run(() => DiscScanner.Scan(r, folders, path => Dispatcher.UIThread.Post(() => { if (!Busy) statusLeft.Text = L.T("Lendo ", "Reading ") + path; }), cancel.Token));
    } catch (OperationCanceledException) { return; }
    catch (Exception ex) { result = found; AppendLog("scan: " + ex.Message); }
    if (cancel != scanCancel) return;
    scanning = false;
    tbRefresh.IsEnabled = true;
    found = result;
    BuildEntries();
    RefreshStatus();
  }

  void OnDrop(string[] paths) {
    if (paths == null || paths.Length == 0) return;
    var dirs = paths.Where(Directory.Exists).Where(d => !DiscScanner.IsExtractedDisc(d)).ToList();
    var disc = paths.FirstOrDefault(p => File.Exists(p) && p.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || Directory.Exists(p) && DiscScanner.IsExtractedDisc(p));
    if (dirs.Count > 0 && Lib) {
      int added = GameFolders.Add(dirs);
      statusLeft.Text = L.T($"{added} pasta(s) adicionada(s).", $"{added} folder(s) added.");
      ShowFolderHint();
      Rescan();
    }
    if (disc != null) Dispatcher.UIThread.Post(() => OpenDisc(disc));
  }

  void ReloadRecomp(string selectId) {
    if (recomp == null) { Rescan(); return; }
    recomp = registry.Reload(recomp);
    var g = recomp.Games.FirstOrDefault(x => x.Id == (selectId ?? game?.Id));
    if (g != null) { game = g; cfg = recomp.ConfigFor(g); root = g.Dir; startKey = registry.KnownFor(recomp)?.Id + "/" + g.Id; }
    BuildEntries();
    if (g != null) SelectGame(g.Id);
    Rescan();
  }

  // ------------------------------------------------------------------ discs

  public async void ChooseDisc(GameEntry forGame) {
    if (Busy) return;
    string file = await Pickers.File(this, forGame != null ? L.T("Disco de ", "Disc of ") + forGame.Name : L.T("Escolha a imagem do disco do jogo", "Choose the game's disc image"),
                                     L.T("Imagem de disco (*.iso)", "Disc image (*.iso)"), "*.iso");
    if (file != null) OpenDisc(file);
  }

  async void OpenDisc(string path) {
    if (Busy) return;
    if (!Lib) { Install(path); return; }
    string console = Consoles.Identify(path, out _);
    if (console == null) { await Dialog.Info(this, Title, L.T("Não reconheci este disco (PS2, Xbox, Xbox 360, PSP ou dump de PS5). Imagens .iso precisam ter setores de 2048 bytes.", "This disc was not recognised (PS2, Xbox, Xbox 360, PSP or a PS5 dump). .iso images need 2048-byte sectors.")); return; }
    recomp = registry.RecompForConsole(console);
    if (recomp == null) {
      var k = registry.ForConsole(console);
      if (k != null && !k.Supported) { await Dialog.Info(this, Title, L.T($"Este é um disco de {Consoles.Name(console)}. O {k.Name} ainda não tem versão para {OS.Name}.", $"This is a {Consoles.Name(console)} disc. {k.Name} has no {OS.Name} version yet.")); return; }
      if (k != null && await Dialog.Ask(this, Title, L.T($"Este é um disco de {Consoles.Name(console)}. O recompilador dele ({k.Name}) ainda não está neste PC. Baixar agora?", $"This is a {Consoles.Name(console)} disc. Its recompiler ({k.Name}) is not on this PC yet. Download it now?"),
                                        L.T("Baixar", "Download"), L.T("Cancelar", "Cancel"))) DownloadRecomp(k);
      return;
    }
    if (!recomp.Supported) { await Dialog.Info(this, Title, L.T($"O {recomp.Name} ainda não tem versão para {OS.Name}.", $"{recomp.Name} has no {OS.Name} version yet.")); return; }
    var match = recomp.Detect(path, out string boot);
    if (match == null && boot != null && recomp.Generic != null) { AddGenericPort(path, boot); return; }
    if (match == null) {
      await Dialog.Info(this, Title, boot == null
          ? L.T("Este disco não é de nenhum jogo de " + recomp.Name + ", ou a imagem não é uma .iso de setores de 2048 bytes.", "This disc is not one of the " + recomp.Name + " games, or the image is not an .iso with 2048-byte sectors.")
          : L.T($"Este disco ({boot}) não é de nenhum jogo de {recomp.Name}. Jogos suportados:\n\n", $"This disc ({boot}) is not one of the {recomp.Name} games. Supported games:\n\n") +
            string.Join("\n", recomp.Games.Select(g => "•  " + g.Name + "  (" + g.Disc + ")")));
      return;
    }
    SelectGame(match.Id);
    if (game != match) return;
    var e = Current;
    if (e != null && e.Disc == null) { e.Disc = path; library.InvalidateVisual(); }
    if (!match.Supported) { await Dialog.Info(this, match.Name, Texts.NoRuntimeNote(e)); return; }
    Install(path);
  }

  async void AddGenericPort(string path, string boot) {
    if (Current != null && Current.Recomp != null && Current.Disc == path) recomp = Current.Recomp;
    if (Busy || recomp == null || path == null) return;
    if (boot == null) recomp.Detect(path, out boot);
    if (boot == null) { await Dialog.Info(this, Title, L.T("Não foi possível ler este disco.", "This disc could not be read.")); return; }
    bool ps5 = Recomp.Ps5Title(path, out _) != null;
    bool yes = ps5
        ? await Dialog.Ask(this, Title, L.T(
            $"Este jogo ({boot}) ainda não foi convertido.\n\nO {recomp.Name} converte o executável do seu dump para um programa do Windows " +
            "(sem emulação) e liga os arquivos do jogo no lugar, sem copiar. Leva poucos minutos. Jogos ainda podem não funcionar ou parar em algum ponto.\n\nConverter?",
            $"This game ({boot}) has not been converted yet.\n\n{recomp.Name} converts your dump's executable into a Windows program " +
            "(no emulation) and links the game files in place, without copying. It takes a few minutes. Games may still not work or may stop somewhere.\n\nConvert it?"),
            L.T("Converter", "Convert"), L.T("Cancelar", "Cancel"))
        : await Dialog.Ask(this, Title, Texts.GenericAsk(recomp.Name, boot, Recomp.BootExecutable(path) != null),
            L.T("Tentar", "Try"), L.T("Cancelar", "Cancel"));
    if (!yes) return;
    string id = recomp.CreateGenericPort(path, boot);
    if (id == null) { await Dialog.Info(this, Title, L.T("Não foi possível ler este disco.", "This disc could not be read.")); return; }
    ReloadRecomp(id);
    if (game?.Id == id) Install(path);
  }

  // ------------------------------------------------------------------ status

  void RefreshStatus() {
    bool busy = Busy;
    var e = Current;
    bool current = IsCurrent(e);
    if (current && !e.NoRuntime && e.Installed != Installed) { e.Installed = Installed; library.InvalidateVisual(); }
    library.Locked = busy;
    bool runnable = e != null && !e.NoRuntime;
    tbPlay.IsEnabled = current && runnable && Installed && !busy;
    tbStop.IsEnabled = busy;
    tbInstall.IsEnabled = e != null && runnable && !busy && (current || e.NoProfile || e.NeedsRecomp);
    tbInstall.Text = e != null && e.NeedsRecomp ? L.T("Baixar", "Get") : e != null && e.NoProfile ? L.T("Montar", "Build") : current && Installed ? L.T("Reparar", "Repair") : L.T("Instalar", "Install");
    tbSettings.IsEnabled = current && runnable && !e.NoProfile;
    tbDeps.IsEnabled = current && runnable && !e.NoProfile;
    tbRecomps.IsEnabled = !busy;
    tbOpen.IsEnabled = !busy;
    cancelButton.IsEnabled = busy;
    cancelButton.Text = SetupRunning || !busy ? L.T("Parar", "Stop") : L.T("Fechar jogo", "Close game");
    if (SetupRunning) statusLeft.Text = L.T("Instalando ", "Setting up ") + cfg.Game + L.T("... acompanhe no log. Pode demorar; deixe esta janela aberta.", "... follow it in the log. It takes a while; keep this window open.");
    else if (busy) statusLeft.Text = L.T("Em jogo: ", "Playing: ") + cfg.Game + L.T(". O launcher volta quando o jogo fechar.", ". The launcher comes back when the game closes.");
    else if (e == null) statusLeft.Text = "";
    else if (e.NoRuntime) statusLeft.Text = L.T("Sem versão para ", "No version for ") + OS.Name + ": " + (e.Disc ?? e.Name);
    else if (e.NeedsRecomp || e.Unsupported) statusLeft.Text = e.Disc ?? "";
    else if (e.NoProfile) statusLeft.Text = L.T("Disco sem perfil: ", "Disc without a profile: ") + e.Disc;
    else if (current && Installed) statusLeft.Text = L.T("Pronto para jogar  ·  ", "Ready to play  ·  ") + root;
    else if (root == null) statusLeft.Text = L.T($"Não instalado. Precisa de cerca de {cfg.DiskGb} GB livres e {cfg.Minutes} minutos.", $"Not installed. About {cfg.DiskGb} GB free and {cfg.Minutes} minutes are needed.");
    else if (e.Disc != null) statusLeft.Text = L.T("Disco encontrado: ", "Disc found: ") + e.Disc;
    else statusLeft.Text = L.T("Sem disco. Escolha a ISO ou adicione a pasta dela.", "No disc. Choose the ISO or add its folder.");
    Title = TitleText() + (e != null ? "  ·  " + e.Name : "");
    RenderDetail();
    UpdateCounts();
    DepsChanged?.Invoke();
  }

  public event Action DepsChanged;

  // ------------------------------------------------------------------ updates

  public async void CheckUpdates(bool manual) {
    if (!manual && !Prefs.Bool("update_check", true)) return;
    var channel = Updater.Channel;
    LauncherRelease rel = null;
    string target = null, local = null, label = null, error = null;
    bool recompNewer = false;
    Recomp r = null;
    var all = registry?.Installed.Values.ToList() ?? new List<Recomp>();
    await Task.Run(() => {
      try { rel = Updater.Latest(channel); } catch (Exception ex) { error = ex.Message; }
      foreach (var x in all) {
        if (x.Repo == null) continue;
        try {
          string t = Updater.RecompTarget(x, channel, out string lb), lo = Updater.LocalHead(x);
          if (r == null) { r = x; target = t; label = lb; local = lo; }
          if (t != null && (lo == null || !string.Equals(lo, t, StringComparison.OrdinalIgnoreCase))) { r = x; target = t; label = lb; local = lo; recompNewer = true; break; }
        } catch (Exception ex) { error = error ?? ex.Message; }
      }
    });
    bool newer = Updater.IsNewer(rel, channel);
    bool launcherNewer = Updater.CanReplaceSelf && newer;
    bool launcherDownload = !Updater.CanReplaceSelf && newer && rel?.Page != null && !Updater.IsLocalBuild;
    if (launcherNewer && !manual && Prefs.Bool("update_auto", true) && !Updater.IsLocalBuild && !Busy) { UpdateLauncher(rel); return; }
    if (recompNewer && !manual && Prefs.Bool("update_recomp_auto", false) && !Busy) UpdateRecomp(r, target, label);
    else if (launcherNewer || launcherDownload || recompNewer) {
      var actions = new List<KeyValuePair<string, Action>>();
      string msg;
      if (launcherNewer || launcherDownload) {
        msg = L.T($"Nova versão do launcher: {rel.Display} (você tem {Updater.Describe()}).", $"New launcher version: {rel.Display} (you have {Updater.Describe()}).");
        if (launcherNewer) actions.Add(new KeyValuePair<string, Action>(L.T("Atualizar launcher", "Update launcher"), () => UpdateLauncher(rel)));
        else actions.Add(new KeyValuePair<string, Action>(L.T("Baixar", "Download"), () => OS.OpenUrl(rel.Page)));
        if (recompNewer) {
          msg += L.T($"  O {r.Name} também tem novidades ({label}).", $"  {r.Name} has news too ({label}).");
          actions.Add(new KeyValuePair<string, Action>(L.T("Atualizar ", "Update ") + r.Name, () => UpdateRecomp(r, target, label)));
        }
      } else {
        msg = L.T($"Há uma versão nova do {r.Name} ({label}{(target != null ? " " + target.Substring(0, 7) : "")}): perfis de jogos e correções.",
                  $"A new version of {r.Name} is out ({label}{(target != null ? " " + target.Substring(0, 7) : "")}): game profiles and fixes.");
        actions.Add(new KeyValuePair<string, Action>(L.T("Atualizar agora", "Update now"), () => UpdateRecomp(r, target, label)));
      }
      actions.Add(new KeyValuePair<string, Action>(L.T("Depois", "Later"), () => updateBar.IsVisible = false));
      updateBar.Show(Glyph.Update, Look.Blue, msg, actions.ToArray());
    } else if (manual) {
      await Dialog.Info(this, L.T("Atualizações", "Updates"), error != null
          ? L.T("Não foi possível procurar atualizações: ", "Could not check for updates: ") + error
          : L.T("Tudo atualizado.", "Everything is up to date.") + "\n\nLauncher: " + Updater.Describe() +
            (rel == null && channel == UpdateChannel.Nightly ? L.T(" (o canal nightly ainda não tem compilação)", " (the nightly channel has no build yet)") : "") +
            (r != null && local != null ? "\n" + r.Name + ": " + local.Substring(0, Math.Min(7, local.Length)) : ""));
    }
  }

  async void UpdateLauncher(LauncherRelease rel) {
    if (Busy) return;
    updateBar.IsVisible = false;
    SetLogVisible(true);
    stepLabel.Text = L.T("Atualizando o launcher para ", "Updating the launcher to ") + rel.Display + "...";
    try { await Task.Run(() => Updater.ReplaceSelf(rel, AppendLog)); }
    catch (Exception ex) {
      AppendLog(ex.Message);
      stepLabel.Text = L.T("A atualização do launcher falhou.", "The launcher update failed.");
      return;
    }
    Updater.Relaunch();
    Restart = false;
    Close();
  }

  async void UpdateRecomp(Recomp target, string sha, string label) {
    if (Busy || target == null || sha == null) return;
    recomp = target;
    updateBar.IsVisible = false;
    SetLogVisible(true);
    ClearLog();
    stepLabel.Text = L.T("Atualizando ", "Updating ") + recomp.Name + " (" + label + ")...";
    tbInstall.IsEnabled = tbOpen.IsEnabled = false;
    var r = recomp;
    try {
      await Task.Run(() => { Updater.UpdateRecomp(r, sha, AppendLog); r.EnsureInstallerFiles(AppendLog); });
      stepLabel.Text = r.Name + L.T(" atualizado.", " updated.");
    } catch (Exception ex) {
      AppendLog(ex.Message);
      stepLabel.Text = L.T("A atualização parou. Veja o log.", "The update stopped. See the log.");
    }
    ReloadRecomp(game?.Id);
  }

  // ------------------------------------------------------------------ install

  void ClearLog() { lock (pendingLines) pendingLines.Clear(); logText.Clear(); log.Text = ""; }

  // Lines from the setup arrive by the thousand during a build: they are kept here and shown a few times a second.
  readonly List<string> pendingLines = new List<string>();
  DispatcherTimer logTimer;

  public void AppendLog(string line) {
    if (line == null) return;
    lock (pendingLines) pendingLines.Add(line);
    Dispatcher.UIThread.Post(() => {
      if (logTimer != null) return;
      logTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (s, e) => FlushLog());
      logTimer.Start();
      FlushLog();
    });
  }

  void FlushLog() {
    List<string> lines;
    lock (pendingLines) { lines = new List<string>(pendingLines); pendingLines.Clear(); }
    if (lines.Count == 0) return;
    foreach (string line in lines) { logText.Append(line).Append('\n'); StepFrom(line); }
    if (logText.Length > 400000) logText.Remove(0, logText.Length - 300000);
    log.Text = logText.ToString();
    log.CaretIndex = log.Text.Length;
  }

  void StepFrom(string line) {
    var m = Runner.StepLine.Match(line);
    if (m.Success) {
      progress.Maximum = int.Parse(m.Groups[2].Value);
      progress.Value = int.Parse(m.Groups[1].Value) - 1;
      stepLabel.Text = m.Groups[1].Value + "/" + m.Groups[2].Value + "   " + m.Groups[3].Value;
    }
  }

  string DefaultInstallFolder() => Path.Combine(Texts.DefaultGamesFolder(), RepoName);

  public async void Install(string source = null) {
    if (Busy) return;
    if (Current != null && !IsCurrent(Current)) return;
    if (Current != null && Current.NoRuntime) { await Dialog.Info(this, Current.Name, Texts.NoRuntimeNote(Current)); return; }
    pendingSource = source;
    string target = root;
    if (target == null) {
      target = DefaultInstallFolder();
      string parent = await Pickers.Folder(this, L.T("Onde instalar o jogo (uma pasta " + RepoName + " é criada dentro)", "Where to install the game (a " + RepoName + " folder is created inside)"), Path.GetDirectoryName(target));
      if (parent == null) return;
      target = Path.Combine(parent, RepoName);
    }
    if (!await Dialog.Ask(this, cfg.Game, Texts.InstallPlan(cfg, root == null, source, target), L.T("Instalar", "Install"), L.T("Cancelar", "Cancel"))) return;
    SetLogVisible(true);
    ClearLog();
    progress.Value = 0;
    tbInstall.IsEnabled = false;
    try {
      if (root == null) {
        stepLabel.Text = L.T("Baixando o kit do port...", "Downloading the port kit...");
        string t = target;
        root = await Task.Run(() => KitDownload.Fetch(cfg, t, AppendLog));
        if (Current != null) Current.Dir = root;
      }
    } catch (Exception ex) {
      AppendLog(ex.Message);
      stepLabel.Text = L.T("O download do kit falhou.", "Downloading the kit failed.");
      RefreshStatus();
      return;
    }
    if (!await CopyGameFiles()) { RefreshStatus(); if (autoLog != null) FinishAuto(1); return; }
    RunSetup();
  }

  // The game files, copied and checked from the disc by the launcher itself before the setup runs (the setup then
  // finds them in place). Without a disc chosen yet, the launcher asks for it here.
  async Task<bool> CopyGameFiles() {
    if (!Runner.NeedsGameFiles(root)) return true;
    string source = pendingSource;
    if (source == null) {
      source = await Pickers.File(this, L.T("Escolha o disco do jogo: ", "Choose the game's disc: ") + cfg.Disc, L.T("Imagem de disco (*.iso)", "Disc image (*.iso)"), "*.iso");
      if (source == null) { stepLabel.Text = L.T("Nenhum disco escolhido.", "No disc chosen."); return false; }
    }
    pendingSource = null;
    copying = new CancellationTokenSource();
    var token = copying.Token;
    RefreshStatus();
    stepLabel.Text = L.T("Copiando os arquivos do jogo...", "Copying the game files...");
    AppendLog(L.T("== Arquivos do jogo (", "== Game files (") + source + ")");
    long shown = 0;
    try {
      var result = await Task.Run(() => GameInstall.Install(source, root, Path.Combine(root, "installer.json"), AppendLog, (done, total) => {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref shown) < 200 && done < total) return;
        Interlocked.Exchange(ref shown, now);
        Dispatcher.UIThread.Post(() => stepLabel.Text = L.T("Copiando e conferindo os arquivos do jogo: ", "Copying and checking the game files: ") + (100 * done / Math.Max(1, total)) + "%");
      }, token));
      AppendLog(L.T("Arquivos do jogo copiados e conferidos.", "Game files copied and checked."));
      if (result.CanDeleteSource && autoLog == null && await Dialog.Ask(this, cfg.Game,
            L.T("Todos os arquivos foram conferidos e o jogo não precisa mais da imagem do disco.\n\nApagar permanentemente a ISO original?\n", "Every file was checked and the game no longer needs the disc image.\n\nPermanently delete the original ISO?\n") + result.Source,
            L.T("Apagar", "Delete"), L.T("Manter", "Keep"), true)) {
        try { GameInstall.DeleteSource(result); AppendLog(L.T("ISO original apagada.", "Original ISO deleted.")); }
        catch (Exception ex) { AppendLog(ex.Message); }
      }
      return true;
    } catch (Exception ex) {
      AppendLog(ex.Message);
      stepLabel.Text = token.IsCancellationRequested ? L.T("Cópia cancelada. O disco não foi alterado.", "Copy cancelled. The disc was not changed.")
                                                     : L.T("A cópia dos arquivos do jogo parou: ", "Copying the game files stopped: ") + ex.Message;
      return false;
    } finally {
      copying.Dispose();
      copying = null;
    }
  }

  public void RunSetup(string only = null) {
    ProcessStartInfo psi;
    try { psi = Runner.Setup(Lib ? recomp : null, cfg, root, only, pendingSource, AppendLog); }
    catch (Exception ex) { AppendLog(ex.Message); return; }
    pendingSource = null;
    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
    p.OutputDataReceived += (s, e) => AppendLog(e.Data);
    p.ErrorDataReceived += (s, e) => AppendLog(e.Data);
    p.Exited += (s, e) => Dispatcher.UIThread.Post(() => SetupFinished(p.ExitCode, only));
    running = p;
    runningSetup = true;
    stepLabel.Text = L.T("Preparando...", "Starting...");
    AppendLog("$ " + psi.FileName + " " + psi.Arguments);
    try {
      p.Start();
      p.StandardInput.Close();
      p.BeginOutputReadLine();
      p.BeginErrorReadLine();
    } catch (Exception ex) {
      running = null;
      AppendLog(ex.Message);
      stepLabel.Text = L.T("Não foi possível iniciar o setup.", "Could not start the setup.");
    }
    RefreshStatus();
  }

  void SetupFinished(int code, string only) {
    running = null;
    runningSetup = false;
    if (autoLog != null) Dispatcher.UIThread.Post(() => FinishAuto(code));
    if (only != null) {
      stepLabel.Text = code == 0 ? L.T("Pronto.", "Done.") : L.T("Parou com erro. Veja o log (também em setup-log.txt).", "Stopped with an error. See the log (also in setup-log.txt).");
      RefreshStatus();
      return;
    }
    if (code == 0 && Installed) {
      progress.Value = progress.Maximum;
      stepLabel.Text = L.T("Pronto! O jogo está instalado.", "Done! The game is installed.");
      if (Prefs.Bool("shortcut", true)) CreateShortcut();
    } else {
      stepLabel.Text = L.T("A instalação parou. Veja o log (também em setup-log.txt) e tente de novo: as etapas prontas são puladas.",
                           "Setup stopped. See the log (also in setup-log.txt) and try again: finished steps are skipped.");
    }
    RefreshStatus();
  }

  public async void StopRunning() {
    if (!Busy) return;
    bool setup = SetupRunning;
    if (!await Dialog.Ask(this, Title, setup ? L.T("Parar agora? Dá para continuar depois: as etapas prontas são puladas.", "Stop now? You can continue later: finished steps are skipped.")
                                             : L.T("Fechar o jogo agora? O que não foi salvo no jogo se perde.", "Close the game now? Anything not saved in the game is lost."),
                          setup ? L.T("Parar", "Stop") : L.T("Fechar o jogo", "Close the game"), L.T("Continuar", "Keep going"), true)) return;
    if (copying != null) { copying.Cancel(); return; }
    if (running != null) OS.KillTree(running.Id);
  }

  // The launcher itself is the shortcut target: it plays and keeps the settings at hand.
  public void CreateShortcut() {
    try {
      string args = Lib && recomp != null && game != null ? "--game " + OS.Quote(registry.KnownFor(recomp)?.Id + "/" + game.Id) : "";
      string file = Shortcuts.Create(cfg.Game, OS.ExePath, args, root);
      AppendLog(L.T("Atalho: ", "Shortcut: ") + file);
    } catch (Exception ex) {
      AppendLog("Shortcut: " + ex.Message);
    }
  }

  bool closingConfirmed;
  async void OnClosing(object sender, WindowClosingEventArgs e) {
    scanCancel?.Cancel();
    ++coverPass;
    if (!Busy || closingConfirmed) return;
    e.Cancel = true;
    bool stop = await Dialog.Ask(this, Title, SetupRunning ? L.T("A instalação ainda está rodando. Parar e fechar?", "Setup is still running. Stop it and close?")
                                                          : L.T("O jogo ainda está aberto. Fechar o jogo e o launcher?", "The game is still open. Close the game and the launcher?"),
                                 L.T("Parar e fechar", "Stop and close"), L.T("Voltar", "Back"), true);
    if (!stop) { Restart = false; return; }
    copying?.Cancel();
    if (running != null) OS.KillTree(running.Id);
    closingConfirmed = true;
    Close();
  }

  // ------------------------------------------------------------------ play

  public void Play() {
    if (!Installed || Busy || !IsCurrent(Current) || Current.NoRuntime) return;
    var output = new StringBuilder();
    ProcessStartInfo psi = Runner.Play(cfg, root);
    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
    p.Exited += (s, e) => Dispatcher.UIThread.Post(async () => {
      running = null;
      WindowState = WindowState.Normal;
      Activate();
      if (p.ExitCode != 0) {
        string text;
        lock (output) text = output.ToString().Trim();
        if (text.Length > 2000) text = "..." + text.Substring(text.Length - 2000);
        await Dialog.Info(this, Title, L.T("O jogo fechou com erro (código ", "The game closed with an error (code ") + p.ExitCode + ").\n\n" + text);
      }
      RefreshStatus();
    });
    running = p;
    runningSetup = false;
    try {
      p.Start();
      p.StandardInput.Close();
      p.BeginOutputReadLine();
      p.BeginErrorReadLine();
    } catch (Exception ex) { running = null; AppendLog(ex.Message); RefreshStatus(); return; }
    WindowState = WindowState.Minimized;
    RefreshStatus();
  }

  // ------------------------------------------------------------------ menus

  public void SetGrid(bool grid) {
    library.Grid = grid;
    viewSwitch.Set(grid);
    Prefs.Set("grid", grid);
    menus.Sync();
  }

  public int TileSize { get => library.TileSize; set { library.TileSize = value; Prefs.Set("tile_size", value); } }

  public async void SetLanguage(string mode) {
    if (mode == L.Mode) return;
    if (Busy) { await Dialog.Info(this, Title, L.T("Espere terminar para trocar o idioma.", "Wait for it to finish before changing the language.")); return; }
    L.Set(mode);
    NextGame = Current?.Key ?? game?.Id;
    Restart = true;
    Close();
  }

  public async void SetTheme(string mode) {
    if (Busy) { await Dialog.Info(this, Title, L.T("Espere terminar para trocar o tema.", "Wait for it to finish before changing the theme.")); return; }
    Look.Apply(mode);
    Look.Save();
    NextGame = Current?.Key ?? game?.Id;
    Restart = true;
    Close();
  }

  public async void About() {
    await Dialog.Info(this, L.T("Sobre", "About"),
        "Port launcher  " + Updater.Describe() + "  (" + OS.Name + ")\n\n" +
        L.T("Instala, monta e abre jogos recompilados para PC a partir do seu próprio disco. Nada de nenhum jogo é baixado ou enviado: só o código-fonte dos recompiladores.\n\n",
            "Installs, builds and starts games recompiled for PC from your own disc. Nothing from any game is downloaded or uploaded: only the recompilers' source code.\n\n") +
        "github.com/" + Updater.LauncherRepo + "\n" +
        L.T("Canal: ", "Channel: ") + (Updater.Channel == UpdateChannel.Nightly ? "nightly" : L.T("estável", "stable")));
  }

  public void OpenRecompFolder() => OS.OpenFolder(recomp?.Root ?? root);

  // The actions on the selected game: the Game menu, the right-click menu and the panel's "More..." button.
  public List<MenuSpec> GameMenuItems() {
    var e = Current;
    bool busy = Busy, selected = e != null, current = selected && IsCurrent(e), runnable = selected && !e.NoRuntime;
    var list = new List<MenuSpec>();
    list.Add(new MenuSpec(L.T("Jogar", "Play"), Play) { Enabled = current && runnable && Installed && !busy, Bold = true, GestureText = "Enter" });
    if (selected && e.NoProfile)
      list.Add(new MenuSpec(L.T("Montar com o caminho genérico...", "Build with the generic path..."), () => AddGenericPort(e.Disc, e.Serial == null ? null : e.Game?.Id)) { Enabled = !busy && runnable });
    else
      list.Add(new MenuSpec(Installed && current ? L.T("Reparar / atualizar", "Repair / update") : L.T("Instalar e montar", "Install and build"), () => Install(e?.Disc)) { Enabled = selected && runnable && !busy });
    list.Add(new MenuSpec(SetupRunning ? L.T("Parar a instalação", "Stop the setup") : L.T("Fechar o jogo", "Close the game"), StopRunning) { Enabled = busy });
    list.Add(MenuSpec.Separator);
    list.Add(new MenuSpec(L.T("Escolher o disco deste jogo...", "Choose this game's disc..."), () => ChooseDisc(e)) { Enabled = selected && !busy && Lib && e.Game != null });
    list.Add(new MenuSpec(L.T("Patches...", "Patches..."), () => ShowPatches(PatchKind.Patch)) { Enabled = selected && !busy && Lib && e.Game != null });
    list.Add(new MenuSpec(L.T("Cheats...", "Cheats..."), () => ShowPatches(PatchKind.Cheat)) { Enabled = selected && !busy && Lib && e.Game != null });
    list.Add(MenuSpec.Separator);
    list.Add(new MenuSpec(L.T("Escolher capa...", "Choose cover..."), ChooseCover) { Enabled = selected });
    list.Add(new MenuSpec(L.T("Remover capa escolhida", "Remove chosen cover"), ClearCover) { Enabled = selected && Covers.CustomFile(e.Key) != null });
    list.Add(MenuSpec.Separator);
    list.Add(new MenuSpec(L.T("Abrir a pasta do jogo", "Open the game folder"), () => OS.OpenFolder(e?.Dir)) { Enabled = selected && e.Dir != null && Directory.Exists(e.Dir) });
    list.Add(new MenuSpec(L.T("Mostrar o disco no ", "Show the disc in the ") + OS.FileManager(L.Pt), () => OS.Reveal(e.Disc)) { Enabled = selected && e.Disc != null });
    list.Add(new MenuSpec(L.T("Criar atalho na área de trabalho", "Create a desktop shortcut"), CreateShortcut) { Enabled = current && runnable && Installed });
    list.Add(new MenuSpec(L.T("Criar atalho ao terminar a instalação", "Create a shortcut when setup finishes"), null) { Check = Prefs.Bool("shortcut", true), Toggled = on => Prefs.Set("shortcut", on) });
    list.Add(MenuSpec.Separator);
    list.Add(new MenuSpec(L.T("Configurações do PC...", "PC settings..."), ShowSettings) { Enabled = current && runnable });
    return list;
  }

  void ShowGameMenu(Control at, Point where) {
    var menu = Menus.Context(GameMenuItems());
    menu.PlacementTarget = at;
    menu.Placement = PlacementMode.AnchorAndGravity;
    menu.PlacementAnchor = Avalonia.Controls.Primitives.PopupPositioning.PopupAnchor.TopLeft;
    menu.PlacementGravity = Avalonia.Controls.Primitives.PopupPositioning.PopupGravity.BottomRight;
    menu.HorizontalOffset = where.X;
    menu.VerticalOffset = where.Y;
    menu.Open(at);
  }

  // ------------------------------------------------------------------ test aid (--install)

  string autoLog;

  // Selects the game (its library key, e.g. ps2recomp/some-game) once the scan finds it, installs it with every
  // question answered yes, writes the log to logFile and closes when the setup ends (exit code in the log's last line).
  public void AutoInstall(string key, string logFile, string only) {
    autoLog = logFile;
    Dialog.Auto = line => AppendLog(line);
    Opened += async (o, e) => {
      for (int i = 0; i < 600 && (scanning || !library.Entries.Any(x => x.Key == key)); ++i) await Task.Delay(1000);
      library.SelectKey(key);
      OnSelected();
      AppendLog("auto-install: " + key + " selected=" + (Current?.Key ?? "-") + " state=" + Current?.State + " disc=" + Current?.Disc);
      if (Current?.Key != key) { FinishAuto(2); return; }
      if (only != null) { SetLogVisible(true); RunSetup(only); }
      else Install(Current.Disc);
      if (!Busy) FinishAuto(3);
    };
  }

  void FinishAuto(int code) {
    if (autoLog == null) return;
    AppendLog("auto-install: exit " + code + " installed=" + Installed);
    FlushLog();
    try { File.WriteAllText(autoLog, logText.ToString()); } catch (Exception) { }
    autoLog = null;
    Close();
  }

  // ------------------------------------------------------------------ test aid (--shot)

  public string ShotFile;

  // 0 library, 1 dependencies, 2 log, 3 PC settings, 4 list view, 5 recompilers, 6 patches, 7 cheats, 8 folders.
  public void ShotOnLoad(string file, int tab, int seconds) {
    ShotFile = file;
    Opened += async (o, e) => {
      Window target = this;
      // The library first (the game asked for with --game is selected when the scan finds it).
      for (int i = 0; i < 120 && (scanning || startKey != null); ++i) await Task.Delay(500);
      if (tab == 2) { SetLogVisible(true); AppendLog("== 2/5 " + L.T("Exemplo de etapa", "Example step")); }
      if (tab == 4) SetGrid(false);
      if (tab == 6 || tab == 7) {
        await Task.Delay(6000);
        if (Current?.Game == null) { Close(); return; }
        var pd = new PatchesWindow(tab == 6 ? PatchKind.Patch : PatchKind.Cheat, PatchChoices.GameOf(Current), Current.Game.Dir, Current.Recomp.Name);
        pd.Show(this);
        target = pd;
      }
      if (tab == 5) { var rd = new RecompsWindow(registry, this); rd.Show(this); target = rd; }
      if (tab == 8) { var fd = new FoldersWindow(); fd.Show(this); target = fd; }
      if (tab == 1 || tab == 3) {
        var dlg = tab == 1 ? OpenDepsWindow() : OpenSettingsWindow();
        dlg.Show(this);
        target = dlg;
      }
      await Task.Delay(seconds * 1000);
      App.Snapshot(target, file);
      if (target != this) target.Close();
      Close();
    };
  }
}

}
