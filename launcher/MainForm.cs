// The launcher window, laid out like an emulator's: a menu bar, a toolbar, the game library (a list or a grid
// of covers, filled from the recompiler's games and the discs found in the player's game folders), the
// selected game at the right, the log at the bottom and a status bar. PC settings and dependencies open in
// their own windows. Drawn with Theme's controls.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortLauncher {

sealed partial class MainForm : Form {
  GameConfig cfg;
  string root;                // the selected game's folder (kit: the kit folder; null before it is downloaded)
  readonly string launcherPath = Application.ExecutablePath;
  // Recompiler mode (games/<game> of PS2Recomp, xboxrecomp...): the repository and the selected game; null for a single-game kit.
  Recomp recomp;
  RecompGame game;
  // Library mode: every installed recompiler (null for a single-game kit).
  RecompRegistry registry;
  bool Lib => registry != null;
  ConsoleTabs tabs;
  FlatButton emptyButton;
  ToolButton tbRecomps;
  string pendingSource;       // the disc image the next setup copies the game from
  public string NextGame;
  public string NextSource => null;

  // Set when the theme changes: Program opens the window again in the new colours.
  public bool Restart;
  public string RootForRestart => root;
  public int PageForRestart => 0;

  GameLibrary library;
  InfoBar folderBar, updateBar;
  Panel detail, logPanel;
  Splitter logSplitter;
  CoverView detailCover;
  Label detailName, detailSub, detailNote;
  PillLabel detailState;
  FlatButton detailPrimary, detailSettings, detailFolder;
  TableLayoutPanel detailFacts, detailStack;
  ToolButton tbOpen, tbFolders, tbRefresh, tbPlay, tbStop, tbInstall, tbSettings, tbDeps;
  ViewSwitch viewSwitch;
  SearchBox search;
  Label statusLeft, statusRight;
  Label stepLabel, settingsNote, saveState;
  FlatButton saveButton, defaultsButton, cancelButton;
  TextBox log;
  ProgressLine progress;
  Panel pageDeps, pageSettings;
  Form depsDialog;
  readonly Dictionary<Setting, Control> editors = new Dictionary<Setting, Control>();
  readonly Dictionary<Setting, Control> rows = new Dictionary<Setting, Control>();
  ChoiceBox monitorCombo, refreshCombo;
  Process running;
  bool dirty, loading;
  List<FoundDisc> found = new List<FoundDisc>();
  CancellationTokenSource scanCancel;
  bool scanning;
  int coverPass;
  string shotFile;

  string startKey;

  public MainForm(GameConfig config, string kitDir, int page = 0, RecompRegistry registry = null, string selectKey = null) {
    cfg = config;
    root = kitDir;
    this.registry = registry;
    startKey = selectKey;
    Text = Title();
    try { Icon = Icon.ExtractAssociatedIcon(launcherPath); } catch (Exception) { }
    Font = Theme.UI(10f);
    AutoScaleMode = AutoScaleMode.Dpi;
    ClientSize = new Size(1240, 760);
    MinimumSize = new Size(980, 620);
    StartPosition = FormStartPosition.CenterScreen;
    BackColor = Theme.Bg;
    ForeColor = Theme.Text;
    KeyPreview = true;
    AllowDrop = true;
    Theme.DarkTitle(this);

    var menu = BuildMenu();
    var toolbar = BuildToolbar();
    folderBar = new InfoBar();
    updateBar = new InfoBar();
    var status = BuildStatusBar();
    BuildLogPanel();
    BuildDetail();
    library = new GameLibrary { Dock = DockStyle.Fill, Grid = Prefs.Bool("grid", true), TileSize = Prefs.Int("tile_size", 1) };
    library.EmptyText = Lib
        ? L.T("Nenhum jogo ainda. Adicione uma ou mais pastas onde ficam as suas ISOs: o launcher procura nelas (e nas subpastas), reconhece o console e o jogo de cada disco e mostra as capas.",
              "No games yet. Add one or more folders that hold your ISOs: the launcher searches them (and their subfolders), recognises each disc's console and game and shows the covers.")
        : "";
    tabs = new ConsoleTabs { Visible = Lib };
    tabs.Changed += (s, e) => { library.Console = tabs.Selected; Prefs.Set("console_tab", tabs.Selected ?? ""); UpdateEmpty(); };
    library.ConsoleReset += (s, e) => { tabs.Selected = null; tabs.Invalidate(); };
    emptyButton = new FlatButton(L.T("ADICIONAR PASTA DE JOGOS", "ADD A GAME FOLDER"), ButtonKind.Primary, 300, 48) { Font = Theme.Semi(11.5f), Visible = false };
    emptyButton.Click += (s, e) => AddFolders();
    library.Controls.Add(emptyButton);
    library.Resize += (s, e) => PlaceEmpty();
    library.SelectionChanged += (s, e) => OnSelected();
    library.Activated += (s, e) => PrimaryAction();
    library.MenuRequested += (entry, at) => GameMenu().Show(library, at);
    viewSwitch.Grid = library.Grid;

    // Docking order: the last added sits outermost.
    Controls.Add(library);
    Controls.Add(tabs);
    Controls.Add(logSplitter);
    Controls.Add(logPanel);
    Controls.Add(detail);
    Controls.Add(updateBar);
    Controls.Add(folderBar);
    Controls.Add(toolbar);
    Controls.Add(menu);
    Controls.Add(status);
    MainMenuStrip = menu;
    logPanel.Visible = logSplitter.Visible = Prefs.Bool("show_log", false);
    detail.Visible = Prefs.Bool("show_details", true);

    FormClosing += OnClosing;
    KeyDown += OnKey;
    DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
    DragDrop += (s, e) => OnDrop(e.Data.GetData(DataFormats.FileDrop) as string[]);
    Shown += (s, e) => {
      ActiveControl = library;
      if (pendingSource != null) { string src = pendingSource; pendingSource = null; Install(src); }
      Rescan();
      if (shotFile == null) CheckUpdates(false);
    };
    BuildEntries();
    ShowFolderHint();
    RefreshStatus();
  }

  string Title() => Lib ? "Port launcher" : cfg.Game + " — Launcher";

  void PlaceEmpty() {
    if (emptyButton == null) return;
    emptyButton.Location = new Point((library.ClientSize.Width - emptyButton.Width) / 2, library.ClientSize.Height / 2 + K(64));
  }

  void UpdateEmpty() {
    if (emptyButton == null) return;
    emptyButton.Visible = Lib && library.Entries.Count == 0 && !scanning;
    PlaceEmpty();
  }

  // Test aid (--shot): 0 library, 1 dependencies, 2 log, 3 PC settings, 4 list view, 5 recompilers.
  public void ShotOnLoad(string file, int tab) {
    shotFile = file;
    Shown += (o, e) => {
      Control target = this;
      if (tab == 2) { SetLogVisible(true); AppendLog("== 2/5 " + L.T("Exemplo de etapa", "Example step")); }
      if (tab == 4) library.Grid = false;
      if (tab == 5) { var rd = new RecompsDialog(registry); rd.Show(this); target = rd; }
      if (tab == 1 || tab == 3) {
        var dlg = tab == 1 ? OpenDepsDialog() : OpenSettingsDialog();
        dlg.Show(this);
        target = dlg;
      }
      var timer = new System.Windows.Forms.Timer { Interval = 15000 };
      timer.Tick += (t, a) => {
        timer.Stop();
        var f = (Form)target;
        using (var bmp = new Bitmap(f.Width, f.Height)) {
          f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
          bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        Close();
      };
      timer.Start();
    };
  }

  public static bool IsKit(string dir) => dir != null && File.Exists(Path.Combine(dir, "setup.json")) && File.Exists(Path.Combine(dir, "tools", "setup.ps1"));
  string GameExe => root == null || cfg.Exe == null ? null : Path.Combine(root, cfg.Exe);
  string IniPath => root == null || cfg.Ini == null ? null : Path.Combine(root, cfg.Ini);
  bool Installed => GameExe != null && File.Exists(GameExe);
  bool Busy => running != null && !running.HasExited;
  bool SetupRunning => Busy && running.StartInfo.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase);
  GameEntry Current => library?.Selected;
  string RepoName => string.IsNullOrEmpty(cfg.Repo) ? "game" : cfg.Repo.Substring(cfg.Repo.LastIndexOf('/') + 1);
  int K(float v) => (int)Math.Round(v * DeviceDpi / 96f);

  // ------------------------------------------------------------------ layout helpers (also used by the dialogs)

  static TableLayoutPanel Stack(Color back) {
    var t = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = back, Margin = Padding.Empty, Padding = Padding.Empty };
    t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    return t;
  }

  static Label Words(string text, Font font, Color color, Padding margin) =>
      new Label { Text = text, Font = font, ForeColor = color, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = margin };

  static TableLayoutPanel AddCard(TableLayoutPanel parent, out Card card, Padding? margin = null) {
    // The card follows its content's height: an AutoSize panel around a docked stack is measured before the
    // help texts wrap to the final width, which cut off the last row of short cards.
    card = new Card { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = margin ?? new Padding(0, 0, 0, 16) };
    var inner = Stack(Theme.Surface);
    var c = card;
    inner.SizeChanged += (s, e) => { int h = inner.Height + c.Padding.Vertical; if (c.Height != h) c.Height = h; };
    card.Controls.Add(inner);
    parent.Controls.Add(card);
    return inner;
  }

  static Panel Scroller(TableLayoutPanel content) {
    var p = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
    p.Controls.Add(content);
    Theme.DarkScroll(p);
    return p;
  }

  static FlowLayoutPanel Row(Color back, Padding margin) =>
      new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = back, Margin = margin, Padding = Padding.Empty };

  Form PageDialog(string title, Control content, Size size) {
    var f = new Form {
      Text = title, Font = Theme.UI(10f), AutoScaleMode = AutoScaleMode.Dpi, ClientSize = size, MinimumSize = new Size(760, 480),
      StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg, ForeColor = Theme.Text, ShowInTaskbar = false, MinimizeBox = false, Icon = Icon,
      KeyPreview = true,
    };
    f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
    Theme.DarkTitle(f);
    f.Controls.Add(content);
    return f;
  }

  // ------------------------------------------------------------------ menu bar

  ToolStripMenuItem Item(string text, Action action, Keys keys = Keys.None, string shortcutText = null) {
    var i = new ToolStripMenuItem(text) { ForeColor = Theme.Text, Padding = new Padding(2, 3, 2, 3) };
    if (keys != Keys.None) { i.ShortcutKeys = keys; }
    if (shortcutText != null) i.ShortcutKeyDisplayString = shortcutText;
    if (action != null) i.Click += (s, e) => action();
    return i;
  }

  ToolStripMenuItem Check(string text, bool on, Action<bool> changed) {
    var i = new ToolStripMenuItem(text) { ForeColor = Theme.Text, Checked = on, CheckOnClick = true, Padding = new Padding(2, 3, 2, 3) };
    i.CheckedChanged += (s, e) => changed(i.Checked);
    return i;
  }

  ToolStripMenuItem MenuTop(string text, params ToolStripItem[] items) {
    var top = new ToolStripMenuItem(text) { ForeColor = Theme.Text, Padding = new Padding(8, 2, 8, 2) };
    top.DropDownItems.AddRange(items);
    PrepareDropDown(top);
    return top;
  }

  static void PrepareDropDown(ToolStripMenuItem item) {
    if (item.DropDown is ToolStripDropDownMenu dd) {
      dd.ShowImageMargin = false; dd.ShowCheckMargin = true; dd.BackColor = Theme.Surface2; dd.ForeColor = Theme.Text;
      dd.Renderer = new DarkMenuRenderer();
    }
    foreach (var sub in item.DropDownItems.OfType<ToolStripMenuItem>()) if (sub.HasDropDownItems) PrepareDropDown(sub);
  }

  static ToolStripSeparator Sep() => new ToolStripSeparator();

  MenuStrip BuildMenu() {
    var menu = new MenuStrip { Renderer = new DarkMenuRenderer(), BackColor = Theme.Surface, ForeColor = Theme.Text, Font = Theme.UI(9.5f), Padding = new Padding(8, 3, 0, 3) };
    bool rc = Lib;

    var file = MenuTop(L.T("Arquivo", "File"),
        Item(L.T("Abrir disco (ISO)...", "Open disc (ISO)..."), () => ChooseDisc(null), Keys.Control | Keys.O),
        Item(L.T("Adicionar pastas de jogos...", "Add game folders..."), AddFolders, Keys.Control | Keys.Shift | Keys.O),
        Item(L.T("Pastas de jogos...", "Game folders..."), ManageFolders),
        Item(L.T("Procurar jogos de novo", "Rescan game folders"), Rescan, Keys.F5),
        Sep(),
        Item(L.T("Recompiladores...", "Recompilers..."), ShowRecomps, Keys.Control | Keys.R),
        Item(L.T("Abrir a pasta do recompilador", "Open the recompiler folder"), () => Explore(recomp?.Root ?? root)),
        Sep(),
        Item(L.T("Sair", "Exit"), Close, Keys.Alt | Keys.F4));
    foreach (ToolStripItem i in file.DropDownItems) if (i.Text != L.T("Sair", "Exit") && !(i is ToolStripSeparator) && i.Text != L.T("Abrir disco (ISO)...", "Open disc (ISO)...")) i.Enabled = rc;

    var gameMenu = new ToolStripMenuItem(L.T("Jogo", "Game")) { ForeColor = Theme.Text, Padding = new Padding(8, 2, 8, 2) };
    gameMenu.DropDownItems.Add(new ToolStripMenuItem("-"));
    gameMenu.DropDownOpening += (s, e) => {
      gameMenu.DropDownItems.Clear();
      foreach (var i in GameMenuItems()) gameMenu.DropDownItems.Add(i);
      PrepareDropDown(gameMenu);
    };
    PrepareDropDown(gameMenu);

    var config = MenuTop(L.T("Configurações", "Settings"),
        Item(L.T("Configurações do PC...", "PC settings..."), ShowSettings, Keys.Control | Keys.P),
        Item(L.T("Dependências...", "Dependencies..."), ShowDeps, Keys.Control | Keys.D));

    var sizes = new[] { L.T("Pequenas", "Small"), L.T("Médias", "Medium"), L.T("Grandes", "Large") };
    var sizeMenu = new ToolStripMenuItem(L.T("Tamanho das capas", "Cover size")) { ForeColor = Theme.Text };
    for (int i = 0; i < 3; ++i) {
      int n = i;
      var it = new ToolStripMenuItem(sizes[i]) { ForeColor = Theme.Text, Checked = library?.TileSize == i };
      it.Click += (s, e) => { library.TileSize = n; Prefs.Set("tile_size", n); foreach (ToolStripMenuItem x in sizeMenu.DropDownItems) x.Checked = x == it; };
      sizeMenu.DropDownItems.Add(it);
    }
    sizeMenu.DropDownOpening += (s, e) => { for (int i = 0; i < 3; ++i) ((ToolStripMenuItem)sizeMenu.DropDownItems[i]).Checked = library.TileSize == i; };
    var themeMenu = new ToolStripMenuItem(L.T("Tema", "Theme")) { ForeColor = Theme.Text };
    foreach (var t in new[] { new[] { "system", L.T("Igual ao Windows", "Same as Windows") }, new[] { "dark", L.T("Escuro", "Dark") }, new[] { "light", L.T("Claro", "Light") } }) {
      string mode = t[0];
      var it = new ToolStripMenuItem(t[1]) { ForeColor = Theme.Text, Checked = Theme.Mode == mode };
      it.Click += (s, e) => SetTheme(mode);
      themeMenu.DropDownItems.Add(it);
    }
    var view = MenuTop(L.T("Exibir", "View"),
        Item(L.T("Lista", "List"), () => SetGrid(false), Keys.Control | Keys.D1),
        Item(L.T("Grade de capas", "Cover grid"), () => SetGrid(true), Keys.Control | Keys.D2),
        sizeMenu,
        Sep(),
        Check(L.T("Painel do jogo", "Game panel"), Prefs.Bool("show_details", true), on => { detail.Visible = on; Prefs.Set("show_details", on); }),
        Check(L.T("Log", "Log"), Prefs.Bool("show_log", false), on => SetLogVisible(on)),
        Sep(),
        Check(L.T("Baixar capas da internet", "Download covers from the internet"), Prefs.Bool("online_covers", true), on => { Prefs.Set("online_covers", on); ReloadCovers(); }),
        Sep(),
        themeMenu);

    var channel = Updater.Channel;
    var stable = new ToolStripMenuItem(L.T("Estável (último release)", "Stable (latest release)")) { ForeColor = Theme.Text, Checked = channel == UpdateChannel.Stable };
    var nightly = new ToolStripMenuItem(L.T("Nightly (última compilação)", "Nightly (latest build)")) { ForeColor = Theme.Text, Checked = channel == UpdateChannel.Nightly };
    stable.Click += (s, e) => { Updater.Channel = UpdateChannel.Stable; stable.Checked = true; nightly.Checked = false; CheckUpdates(true); };
    nightly.Click += (s, e) => { Updater.Channel = UpdateChannel.Nightly; stable.Checked = false; nightly.Checked = true; CheckUpdates(true); };
    var channelMenu = new ToolStripMenuItem(L.T("Canal de atualização", "Update channel")) { ForeColor = Theme.Text };
    channelMenu.DropDownItems.Add(stable);
    channelMenu.DropDownItems.Add(nightly);
    var updates = MenuTop(L.T("Atualizações", "Updates"),
        Item(L.T("Procurar atualizações agora", "Check for updates now"), () => CheckUpdates(true)),
        channelMenu,
        Sep(),
        Check(L.T("Procurar ao abrir", "Check when the launcher opens"), Prefs.Bool("update_check", true), on => Prefs.Set("update_check", on)),
        Check(L.T("Atualizar o launcher sozinho", "Update the launcher automatically"), Prefs.Bool("update_auto", true), on => Prefs.Set("update_auto", on)),
        Check(L.T("Atualizar o recompilador sozinho", "Update the recompiler automatically"), Prefs.Bool("update_recomp_auto", false), on => Prefs.Set("update_recomp_auto", on)),
        Sep(),
        Item(L.T("Sobre o launcher", "About the launcher"), About));

    menu.Items.AddRange(new ToolStripItem[] { file, gameMenu, config, view, updates });
    return menu;
  }

  void About() {
    Dialog.Info(this, L.T("Sobre", "About"),
        "Port launcher  " + Updater.Describe() + "\n\n" +
        L.T("Instala, monta e abre jogos recompilados para PC a partir do seu próprio disco. Nada de nenhum jogo é baixado ou enviado: só o código-fonte dos recompiladores.\n\n",
            "Installs, builds and starts games recompiled for PC from your own disc. Nothing from any game is downloaded or uploaded: only the recompilers' source code.\n\n") +
        "github.com/" + Updater.LauncherRepo + "\n" +
        L.T("Canal: ", "Channel: ") + (Updater.Channel == UpdateChannel.Nightly ? "nightly" : L.T("estável", "stable")));
  }

  void SetTheme(string mode) {
    if (Busy) { Dialog.Info(this, Text, L.T("Espere terminar para trocar o tema.", "Wait for it to finish before changing the theme.")); return; }
    Theme.Apply(mode);
    Theme.Save();
    NextGame = Current?.Key ?? game?.Id;
    Restart = true;
    Close();
  }

  void SetGrid(bool grid) {
    library.Grid = grid;
    viewSwitch.Set(grid);
    Prefs.Set("grid", grid);
  }

  void SetLogVisible(bool on) {
    logPanel.Visible = logSplitter.Visible = on;
    Prefs.Set("show_log", on);
  }

  // The actions on the selected game: the Game menu and the right-click menu.
  List<ToolStripItem> GameMenuItems() {
    var e = Current;
    bool busy = Busy, selected = e != null, current = selected && IsCurrent(e);
    var list = new List<ToolStripItem>();
    var play = Item(L.T("Jogar", "Play"), Play, Keys.None, "Enter");
    play.Enabled = current && Installed && !busy;
    play.Font = new Font(play.Font, FontStyle.Bold);
    list.Add(play);
    if (selected && e.NoProfile) {
      var build = Item(L.T("Montar com o caminho genérico...", "Build with the generic path..."), () => AddGenericPort(e.Disc, e.Serial == null ? null : e.Game?.Id));
      build.Enabled = !busy;
      list.Add(build);
    } else {
      var inst = Item(Installed && current ? L.T("Reparar / atualizar", "Repair / update") : L.T("Instalar e montar", "Install and build"), () => Install(e?.Disc));
      inst.Enabled = selected && !busy;
      list.Add(inst);
    }
    var stop = Item(SetupRunning ? L.T("Parar a instalação", "Stop the setup") : L.T("Fechar o jogo", "Close the game"), StopRunning);
    stop.Enabled = busy;
    list.Add(stop);
    list.Add(Sep());
    var disc = Item(L.T("Escolher o disco deste jogo...", "Choose this game's disc..."), () => ChooseDisc(e));
    disc.Enabled = selected && !busy && Lib && e.Game != null;
    list.Add(disc);
    var cover = Item(L.T("Escolher capa...", "Choose cover..."), ChooseCover);
    cover.Enabled = selected;
    list.Add(cover);
    var clear = Item(L.T("Remover capa escolhida", "Remove chosen cover"), () => { Covers.ClearCustom(e.Key); ReloadCovers(); });
    clear.Enabled = selected && Covers.CustomFile(e.Key) != null;
    list.Add(clear);
    list.Add(Sep());
    var folder = Item(L.T("Abrir a pasta do jogo", "Open the game folder"), () => Explore(e?.Dir));
    folder.Enabled = selected && e.Dir != null && Directory.Exists(e.Dir);
    list.Add(folder);
    var discFolder = Item(L.T("Mostrar o disco no Explorer", "Show the disc in Explorer"), () => Process.Start("explorer.exe", "/select,\"" + e.Disc + "\""));
    discFolder.Enabled = selected && e.Disc != null;
    list.Add(discFolder);
    var shortcut = Item(L.T("Criar atalho na área de trabalho", "Create a desktop shortcut"), CreateShortcut);
    shortcut.Enabled = current && Installed;
    list.Add(shortcut);
    list.Add(Check(L.T("Criar atalho ao terminar a instalação", "Create a shortcut when setup finishes"), Prefs.Bool("shortcut", true), on => Prefs.Set("shortcut", on)));
    list.Add(Sep());
    var settings = Item(L.T("Configurações do PC...", "PC settings..."), ShowSettings);
    settings.Enabled = current;
    list.Add(settings);
    return list;
  }

  ContextMenuStrip GameMenu() {
    var m = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.UI(9.5f) };
    m.Items.AddRange(GameMenuItems().ToArray());
    m.Closed += (s, e) => BeginInvoke(new Action(m.Dispose));
    return m;
  }

  static void Explore(string dir) { if (dir != null && Directory.Exists(dir)) Process.Start("explorer.exe", "\"" + dir + "\""); }

  // ------------------------------------------------------------------ toolbar

  Control BuildToolbar() {
    var bar = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Theme.Surface, Padding = new Padding(10, 6, 16, 6) };
    var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border };
    var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Theme.Surface, Padding = Padding.Empty };
    tbOpen = new ToolButton(Glyph.Disc, L.T("Abrir ISO", "Open ISO"));
    tbOpen.Click += (s, e) => ChooseDisc(null);
    tbFolders = new ToolButton(Glyph.FolderAdd, L.T("Pastas", "Folders"));
    tbFolders.Click += (s, e) => ManageFolders();
    tbRefresh = new ToolButton(Glyph.Refresh, L.T("Atualizar", "Refresh"));
    tbRefresh.Click += (s, e) => Rescan();
    tbPlay = new ToolButton(Glyph.Play, L.T("Jogar", "Play")) { Tint = Theme.Accent };
    tbPlay.Click += (s, e) => Play();
    tbStop = new ToolButton(Glyph.Stop, L.T("Parar", "Stop")) { Tint = Theme.Danger };
    tbStop.Click += (s, e) => StopRunning();
    tbInstall = new ToolButton(Glyph.Install, L.T("Instalar", "Install"), 84);
    tbInstall.Click += (s, e) => PrimaryInstall();
    tbSettings = new ToolButton(Glyph.Gear, L.T("Configurar", "Settings"), 80);
    tbSettings.Click += (s, e) => ShowSettings();
    tbDeps = new ToolButton(Glyph.Box, L.T("Dependências", "Dependencies"), 92);
    tbDeps.Click += (s, e) => ShowDeps();
    tbRecomps = new ToolButton(Glyph.Update, L.T("Recompiladores", "Recompilers"), 104);
    tbRecomps.Click += (s, e) => ShowRecomps();
    var tbLog = new ToolButton(Glyph.Log, "Log");
    tbLog.Click += (s, e) => SetLogVisible(!logPanel.Visible);
    left.Controls.AddRange(new Control[] { tbOpen, tbFolders, tbRefresh, new ToolSeparator(), tbPlay, tbStop, tbInstall, new ToolSeparator(), tbSettings, tbDeps, tbRecomps, tbLog });
    if (!Lib) { tbFolders.Visible = tbRefresh.Visible = tbRecomps.Visible = false; }

    var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Theme.Surface, Padding = new Padding(0, 12, 0, 0) };
    viewSwitch = new ViewSwitch { Margin = new Padding(0, 0, 12, 0) };
    viewSwitch.Changed += (s, e) => { if (library.Grid != viewSwitch.Grid) SetGrid(viewSwitch.Grid); };
    search = new SearchBox(L.T("Procurar jogos", "Search games")) { Width = 260, Margin = Padding.Empty };
    search.Box.TextChanged += (s, e) => library.Filter = search.Box.Text;
    search.Box.KeyDown += (s, e) => {
      if (e.KeyCode == Keys.Escape) { search.Box.Text = ""; library.Focus(); e.SuppressKeyPress = true; }
      if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter) { library.Focus(); e.SuppressKeyPress = true; }
    };
    right.Controls.Add(viewSwitch);
    right.Controls.Add(search);
    bar.Controls.Add(left);
    bar.Controls.Add(right);
    bar.Controls.Add(line);
    return bar;
  }

  void OnKey(object sender, KeyEventArgs e) {
    if (e.Control && e.KeyCode == Keys.F) { search.Box.Focus(); search.Box.SelectAll(); e.SuppressKeyPress = true; }
  }

  // ------------------------------------------------------------------ the selected game (right panel)

  void BuildDetail() {
    detail = new Panel { Dock = DockStyle.Right, Width = 330, BackColor = Theme.Surface, Padding = new Padding(24, 22, 24, 18) };
    var edge = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Theme.Border };
    var stack = detailStack = Stack(Theme.Surface);
    detailCover = new CoverView { Height = 340, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 18) };
    detailName = Words("", Theme.Semi(15f), Theme.Text, new Padding(0, 0, 0, 4));
    detailSub = Words("", Theme.UI(9.5f), Theme.Text3, new Padding(0, 0, 0, 12));
    detailState = new PillLabel { Margin = new Padding(0, 0, 0, 18) };
    detailPrimary = new FlatButton(L.T("JOGAR", "PLAY"), ButtonKind.Primary, 280, 48) { Font = Theme.Semi(12f), Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, 10) };
    detailPrimary.Click += (s, e) => PrimaryAction();
    var actions = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, 18) };
    actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
    actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
    detailSettings = new FlatButton(L.T("Configurar", "Settings"), ButtonKind.Secondary, 130, 38) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 5, 0) };
    detailSettings.Click += (s, e) => ShowSettings();
    detailFolder = new FlatButton(L.T("Mais...", "More..."), ButtonKind.Secondary, 130, 38) { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(5, 0, 0, 0) };
    detailFolder.Click += (s, e) => GameMenu().Show(detailFolder, new Point(0, detailFolder.Height + 4));
    actions.Controls.Add(detailSettings, 0, 0);
    actions.Controls.Add(detailFolder, 1, 0);
    detailFacts = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Surface, Margin = Padding.Empty };
    detailFacts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    detailNote = Words("", Theme.UI(9f), Theme.Text3, new Padding(0, 14, 0, 0));
    stack.Controls.Add(detailCover);
    stack.Controls.Add(detailName);
    stack.Controls.Add(detailSub);
    stack.Controls.Add(detailState);
    stack.Controls.Add(detailPrimary);
    stack.Controls.Add(actions);
    stack.Controls.Add(detailFacts);
    stack.Controls.Add(detailNote);
    var scroll = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
    scroll.Controls.Add(stack);
    Theme.DarkScroll(scroll);
    detail.Controls.Add(scroll);
    detail.Controls.Add(edge);
    detail.Resize += (s, e) => FitCover();
  }

  // The cover takes what the text and buttons leave (no scrolling at the usual window sizes).
  void FitCover() {
    if (detailStack == null) return;
    int others = detailStack.PreferredSize.Height - detailCover.Height;
    int room = detail.ClientSize.Height - detail.Padding.Vertical - others - K(4);
    int widest = (int)((detail.ClientSize.Width - detail.Padding.Horizontal) * 1.38f);
    int h = Math.Max(K(140), Math.Min(widest, room));
    if (h != detailCover.Height) detailCover.Height = h;
  }

  void Fact(string label, string value) {
    if (string.IsNullOrEmpty(value)) return;
    detailFacts.Controls.Add(Words(label.ToUpperInvariant(), Theme.Semi(8f), Theme.Text3, new Padding(0, 10, 0, 2)));
    var v = Words(value, Theme.UI(9.25f), Theme.Text2, Padding.Empty);
    v.MaximumSize = new Size(Math.Max(100, detail.ClientSize.Width - detail.Padding.Horizontal - 20), 0);
    detailFacts.Controls.Add(v);
  }

  void RenderDetail() {
    var e = Current;
    detail.SuspendLayout();
    detailCover.Entry = e;
    detailCover.Invalidate();
    detailFacts.Controls.Clear();
    if (e == null) {
      detailName.Text = L.T("Nenhum jogo selecionado", "No game selected");
      detailSub.Text = ""; detailState.Visible = false; detailPrimary.Visible = false; detailNote.Text = "";
      detailSettings.Parent.Visible = false;
      detail.ResumeLayout();
      return;
    }
    bool current = IsCurrent(e);
    detailName.Text = e.Name;
    detailSub.Text = string.Join("   ·   ", new[] { e.Serial, e.Platform }.Where(x => !string.IsNullOrEmpty(x)));
    detailState.Visible = true;
    detailState.Set(SetupRunning && current ? L.T("Instalando...", "Installing...") : Busy && current ? L.T("Em jogo", "Playing") : e.StateText,
                    SetupRunning && current ? Theme.Blue : e.StateColor);
    detailPrimary.Visible = true;
    detailSettings.Parent.Visible = true;
    detailPrimary.Text = PrimaryText(e);
    detailPrimary.Kind = e.State == EntryState.Ready ? ButtonKind.Primary : ButtonKind.Blue;
    detailPrimary.Enabled = !Busy;
    detailPrimary.Invalidate();
    detailSettings.Enabled = current && !e.NoProfile;
    if (e.Disc != null) Fact(L.T("Disco", "Disc"), Path.GetFileName(e.Disc) + Environment.NewLine + Path.GetDirectoryName(e.Disc));
    if (e.Dir != null) Fact(L.T("Pasta do jogo", "Game folder"), e.Recomp != null && e.Dir.StartsWith(e.Recomp.Root, StringComparison.OrdinalIgnoreCase)
                                                                     ? e.Recomp.Name + e.Dir.Substring(e.Recomp.Root.Length) : e.Dir);
    if (Lib && e.Console != null) Fact(L.T("Recompilador", "Recompiler"), e.Recomp?.Name ?? e.Known?.Name ?? L.T("nenhum para este console", "none for this console"));
    if (current && !e.NoProfile && !Installed) {
      if (cfg.DiskGb > 0) Fact(L.T("Espaço necessário", "Space needed"), "~" + cfg.DiskGb + " GB");
      if (!string.IsNullOrEmpty(cfg.Minutes)) Fact(L.T("Primeira montagem", "First build"), L.T("cerca de ", "about ") + cfg.Minutes + " min");
    }
    detailNote.Text = DetailNote(e);
    detail.ResumeLayout();
    FitCover();
  }

  string PrimaryText(GameEntry e) {
    if (e.NeedsRecomp) return e.Known != null ? L.T("BAIXAR ", "GET ") + e.Known.Folder.ToUpperInvariant() : L.T("SEM RECOMPILADOR", "NO RECOMPILER");
    if (e.Unsupported) return L.T("AINDA SEM SUPORTE", "NOT SUPPORTED YET");
    if (e.NoProfile) return L.T("MONTAR (GENÉRICO)", "BUILD (GENERIC)");
    switch (e.State) {
      case EntryState.Ready: return L.T("JOGAR", "PLAY");
      case EntryState.HasDisc: return L.T("INSTALAR E MONTAR", "INSTALL AND BUILD");
      case EntryState.Kit: return L.T("INSTALAR", "INSTALL");
      default: return Lib ? L.T("ESCOLHER O DISCO...", "CHOOSE THE DISC...") : L.T("INSTALAR", "INSTALL");
    }
  }

  string DetailNote(GameEntry e) {
    if (e.NeedsRecomp)
      return e.Known != null
          ? L.T($"Jogos de {Consoles.Name(e.Console)} usam o {e.Known.Name}, que ainda não está neste PC. O launcher baixa o código dele do GitHub (nada de nenhum jogo) e depois monta o jogo a partir do seu disco.",
                $"{Consoles.Name(e.Console)} games use {e.Known.Name}, which is not on this PC yet. The launcher downloads its source from GitHub (nothing from any game), then builds the game from your disc.")
          : L.T("Ainda não há recompilador para este console.", "There is no recompiler for this console yet.");
    if (e.Unsupported)
      return L.T($"O {e.Recomp?.Name} ainda não tem este jogo. Atualizações do recompilador podem trazer suporte (menu Atualizações).",
                 $"{e.Recomp?.Name} does not have this game yet. Recompiler updates may add it (Updates menu).");
    if (e.NoProfile)
      return L.T("Este disco não tem perfil neste recompilador. O caminho genérico tenta montar qualquer jogo; pode não funcionar ou parar em algum ponto.",
                 "This disc has no profile in this recompiler. The generic path tries to build any game; it may not work or may stop somewhere.");
    switch (e.State) {
      case EntryState.Ready: return cfg.PcMenu
          ? L.T("No jogo, F1 (ou Back+Start no controle) abre as mesmas configurações do PC.", "In the game, F1 (or Back+Start on a controller) opens the same PC settings.")
          : L.T("As configurações de PC ficam em Configurar. No jogo: F10 desempenho, F11 tela cheia, F12 captura de tela.", "The PC settings are under Configure. In the game: F10 performance, F11 fullscreen, F12 screenshot.");
      case EntryState.HasDisc: return L.T("O disco foi encontrado nas suas pastas. A montagem instala o que faltar, copia e confere os arquivos e compila o jogo; as etapas prontas são puladas depois.",
                                          "The disc was found in your folders. The build installs what is missing, copies and checks the files and compiles the game; finished steps are skipped later.");
      case EntryState.Kit: return L.T($"O instalador baixa o kit do port (código-fonte), instala as ferramentas, pede o seu disco ({cfg.Disc}) e monta a versão para PC.",
                                      $"The installer downloads the port kit (source code), installs the tools, asks for your disc ({cfg.Disc}) and builds the PC version.");
      default: return L.T($"Nenhum disco deste jogo nas suas pastas{(GameFolders.Load().Count == 0 ? " (nenhuma pasta adicionada)" : "")}. Escolha a ISO ou adicione a pasta onde ela está.",
                          $"No disc of this game in your folders{(GameFolders.Load().Count == 0 ? " (no folder added)" : "")}. Choose the ISO or add the folder that holds it.");
    }
  }

  // ------------------------------------------------------------------ log drawer and status bar

  void BuildLogPanel() {
    logPanel = new Panel { Dock = DockStyle.Bottom, Height = 230, BackColor = Theme.Surface };
    logSplitter = new Splitter { Dock = DockStyle.Bottom, Height = 5, BackColor = Theme.Border, MinSize = 120, MinExtra = 240 };
    var head = new TableLayoutPanel { Dock = DockStyle.Top, Height = 42, ColumnCount = 5, BackColor = Theme.Surface, Padding = new Padding(18, 6, 10, 4) };
    head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
    head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    var title = new Label { Text = "LOG", Font = Theme.Semi(9f), ForeColor = Theme.Accent, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 16, 0) };
    stepLabel = new Label { Text = L.T("Nada em andamento.", "Nothing running."), Font = Theme.Semi(9.5f), ForeColor = Theme.Text2, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    progress = new ProgressLine { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 6, Maximum = 5, Margin = new Padding(8, 0, 14, 0) };
    cancelButton = new FlatButton(L.T("Parar", "Stop"), ButtonKind.Danger, 96, 30) { Margin = new Padding(0, 0, 8, 0), Anchor = AnchorStyles.Right, Font = Theme.Semi(9.5f) };
    cancelButton.Click += (s, e) => StopRunning();
    var hide = new FlatButton("", ButtonKind.Ghost, 30, 30) { Margin = Padding.Empty, Anchor = AnchorStyles.Right };
    hide.Paint += (s, e) => Glyphs.Draw(e.Graphics, Glyph.ChevronDown, new RectangleF(5, 5, hide.Width - 10, hide.Height - 10), Theme.Text2);
    hide.Click += (s, e) => SetLogVisible(false);
    head.Controls.Add(title, 0, 0);
    head.Controls.Add(stepLabel, 1, 0);
    head.Controls.Add(progress, 2, 0);
    head.Controls.Add(cancelButton, 3, 0);
    head.Controls.Add(hide, 4, 0);
    var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(18, 10, 6, 8) };
    log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                        BackColor = Theme.Bg, ForeColor = Theme.Text2, Font = new Font("Consolas", 9.5f) };
    Theme.DarkScroll(log);
    body.Controls.Add(log);
    var top = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border };
    logPanel.Controls.Add(body);
    logPanel.Controls.Add(top);
    logPanel.Controls.Add(head);
  }

  Control BuildStatusBar() {
    var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 30, ColumnCount = 2, BackColor = Theme.Surface, Padding = new Padding(16, 0, 16, 0) };
    bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    statusLeft = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text2, Font = Theme.UI(9f), AutoEllipsis = true };
    statusRight = new Label { AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = Theme.Text3, Font = Theme.UI(9f), Margin = new Padding(12, 0, 0, 0) };
    bar.Controls.Add(statusLeft, 0, 0);
    bar.Controls.Add(statusRight, 1, 0);
    bar.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
    return bar;
  }

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

  bool IsCurrent(GameEntry e) => e != null && (!Lib ? e.Key == "kit" : e.Game != null && e.Game == game);

  // Library mode: the discs found in the player's folders (one entry per game), plus games already built from an
  // earlier disc; each with its console, its recompiler and what it takes to play it. Nothing else is listed.
  void BuildEntries() {
    var old = library.Entries.ToDictionary(x => x.Key + "|" + x.Disc, x => x);
    var entries = new List<GameEntry>();
    if (!Lib) {
      entries.Add(new GameEntry { Key = "kit", Name = cfg.Game, Serial = cfg.Disc, Dir = root, Installed = Installed, Platform = L.T("Port para PC", "PC port") });
    } else {
      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var kv in registry.Installed) {
        var rc = kv.Value;
        string console = registry.Known.FirstOrDefault(k => k.Id == kv.Key)?.Console;
        foreach (var g in rc.Games) {
          var disc = found.FirstOrDefault(f => f.Game == g);
          bool installed = false;
          try { var c = g == game ? cfg : rc.ConfigFor(g); installed = c.Exe != null && File.Exists(Path.Combine(g.Dir, c.Exe)); } catch (Exception) { }
          if (disc == null && !installed) continue;
          string key = kv.Key + "/" + g.Id;
          seen.Add(key);
          string serial = Covers.Serial(disc?.Label) ?? g.Executables.Select(Covers.Serial).FirstOrDefault(x => x != null);
          entries.Add(new GameEntry { Key = key, Game = g, Recomp = rc, Known = registry.KnownFor(rc), Console = console, Name = g.Name,
                                      Serial = serial ?? Short(g.Disc), Disc = disc?.Path, Dir = g.Dir, Installed = installed, Platform = Consoles.Short(console) });
        }
      }
      foreach (var f in found.Where(f => f.Game == null)) {
        string key = "disc:" + f.Console + ":" + (f.Label ?? f.Path);
        if (!seen.Add(key)) continue;
        string name = f.IsFolder ? new DirectoryInfo(f.Path).Name : Path.GetFileNameWithoutExtension(f.Path);
        var known = registry.ForConsole(f.Console);
        bool generic = f.Recomp != null && f.Recomp.Generic != null && f.Label != null && Regex.IsMatch(f.Label, f.Recomp.LabelRegex, RegexOptions.IgnoreCase);
        entries.Add(new GameEntry { Key = key, Name = name, Serial = Covers.Serial(f.Label) ?? f.Label, Disc = f.Path, Console = f.Console,
                                    Recomp = f.Recomp, Known = known, NeedsRecomp = f.Recomp == null, NoProfile = generic,
                                    Unsupported = f.Recomp != null && !generic, Platform = Consoles.Short(f.Console) });
      }
    }
    foreach (var e in entries)
      if (old.TryGetValue(e.Key + "|" + e.Disc, out var prev)) { e.Cover = prev.Cover; e.CoverDone = prev.CoverDone; }
    library.SetEntries(entries, Current?.Key ?? startKey ?? (Prefs.Text("last_game", "") is var lg && lg.Length > 0 ? lg : null) ?? "kit");
    startKey = null;
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

  static string Short(string disc) {
    if (string.IsNullOrEmpty(disc)) return null;
    var m = Regex.Match(disc, @"\(([^)]+)\)");
    return m.Success ? m.Groups[1].Value : null;
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
        try { b = Covers.Find(e.Key, e.Serial, e.Dir, e.Disc, url, online); } catch (Exception) { }
        try {
          BeginInvoke(new Action(() => {
            e.Cover = b; e.CoverDone = true;
            library.Invalidate();
            if (e == Current) detailCover.Invalidate();
          }));
        } catch (Exception) { return; }
      }
    });
  }

  void ReloadCovers() {
    foreach (var e in library.Entries) { e.CoverDone = false; e.Cover = null; }
    library.Invalidate();
    LoadCovers();
  }

  void ChooseCover() {
    var e = Current;
    if (e == null) return;
    using (var d = new OpenFileDialog { Title = L.T("Escolha a capa de ", "Choose the cover of ") + e.Name, Filter = L.T("Imagens|*.png;*.jpg;*.jpeg", "Images|*.png;*.jpg;*.jpeg") }) {
      if (d.ShowDialog(this) != DialogResult.OK) return;
      try { Covers.SetCustom(e.Key, d.FileName); } catch (Exception ex) { Dialog.Info(this, Text, ex.Message); return; }
    }
    e.CoverDone = false; e.Cover = null;
    LoadCovers();
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
  void PrimaryAction() {
    var e = Current;
    if (e == null || Busy) return;
    if (e.NeedsRecomp) { if (e.Known != null) DownloadRecomp(e.Known); return; }
    if (e.Unsupported) { Dialog.Info(this, e.Name, DetailNote(e)); return; }
    if (e.NoProfile) { AddGenericPort(e.Disc, null); return; }
    if (!IsCurrent(e)) return;
    if (Installed) { Play(); return; }
    if (e.Disc != null || !Lib) { Install(e.Disc); return; }
    ChooseDisc(e);
  }

  void PrimaryInstall() {
    var e = Current;
    if (e == null || Busy) return;
    if (e.NeedsRecomp || e.Unsupported) PrimaryAction();
    else if (e.NoProfile) AddGenericPort(e.Disc, null);
    else if (IsCurrent(e)) Install(e.Disc);
  }

  void ShowFolderHint() {
    // Library mode: the empty library has its own big button; the bar would only repeat it.
    if (!Lib || GameFolders.Load().Count > 0 || library.Entries.Count == 0) { folderBar.Visible = false; return; }
    folderBar.Show(Glyph.FolderAdd, Theme.Accent,
        L.T("Adicione as pastas onde ficam as suas ISOs: o launcher procura nelas (e nas subpastas) e reconhece cada jogo pelo disco.",
            "Add the folders that hold your ISOs: the launcher searches them (and their subfolders) and recognises each game from its disc."),
        new KeyValuePair<string, Action>(L.T("Adicionar pastas...", "Add folders..."), AddFolders),
        new KeyValuePair<string, Action>(L.T("Agora não", "Not now"), () => folderBar.Visible = false));
  }

  void AddFolders() {
    if (!Lib) return;
    var picked = FolderPicker.Pick(this, L.T("Escolha uma ou mais pastas com jogos (ISOs)", "Choose one or more folders with games (ISOs)"));
    if (picked.Count == 0) return;
    GameFolders.Add(picked);
    ShowFolderHint();
    Rescan();
  }

  void ManageFolders() {
    if (!Lib) return;
    using (var d = new FoldersDialog()) d.ShowDialog(this);
    ShowFolderHint();
    Rescan();
  }

  async void Rescan() {
    if (!Lib) return;
    scanCancel?.Cancel();
    var cancel = scanCancel = new CancellationTokenSource();
    var folders = GameFolders.Load();
    if (folders.Count == 0) { found = new List<FoundDisc>(); BuildEntries(); RefreshStatus(); return; }
    scanning = true;
    tbRefresh.Enabled = false;
    UpdateCounts();
    var r = registry;
    List<FoundDisc> result;
    try {
      result = await Task.Run(() => DiscScanner.Scan(r, folders, path => {
        try { BeginInvoke(new Action(() => { if (!Busy) statusLeft.Text = L.T("Lendo ", "Reading ") + path; })); } catch (Exception) { }
      }, cancel.Token));
    } catch (OperationCanceledException) { return; }
    catch (Exception ex) { result = found; AppendLog("scan: " + ex.Message); }
    if (cancel != scanCancel) return;
    scanning = false;
    tbRefresh.Enabled = true;
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
    if (disc != null) BeginInvoke(new Action(() => OpenDisc(disc)));
  }

  void ReloadRecomp(string selectId) {
    if (recomp == null) { Rescan(); return; }
    recomp = registry.Reload(recomp);
    var g = recomp.Games.FirstOrDefault(x => x.Id == (selectId ?? game?.Id));
    if (g != null) { game = g; cfg = recomp.ConfigFor(g); root = g.Dir; startKey = registry.KnownFor(recomp)?.Id + "/" + g.Id; }
    // A new profile may now match discs found before: the next scan reads them again.
    BuildEntries();
    if (g != null) SelectGame(g.Id);
    Rescan();
  }

  // ------------------------------------------------------------------ discs

  void ChooseDisc(GameEntry forGame) {
    if (Busy) return;
    using (var d = new OpenFileDialog { Title = forGame != null ? L.T("Disco de ", "Disc of ") + forGame.Name : L.T("Escolha a imagem do disco do jogo", "Choose the game's disc image"),
                                        Filter = L.T("Imagem de disco (*.iso)|*.iso|Todos os arquivos|*.*", "Disc image (*.iso)|*.iso|All files|*.*") }) {
      if (d.ShowDialog(this) != DialogResult.OK) return;
      OpenDisc(d.FileName);
    }
  }

  // A disc image (or extracted folder) picks its game; with a profile it is installed, without one the generic path is offered.
  void OpenDisc(string path) {
    if (Busy) return;
    if (!Lib) { Install(path); return; }
    string console = Consoles.Identify(path, out _);
    if (console == null) { Dialog.Info(this, Text, L.T("Não reconheci este disco (PS2, Xbox, Xbox 360, PSP ou dump de PS5). Imagens .iso precisam ter setores de 2048 bytes.", "This disc was not recognised (PS2, Xbox, Xbox 360, PSP or a PS5 dump). .iso images need 2048-byte sectors.")); return; }
    recomp = registry.RecompForConsole(console);
    if (recomp == null) {
      var k = registry.ForConsole(console);
      if (k != null && Dialog.Ask(this, Text, L.T($"Este é um disco de {Consoles.Name(console)}. O recompilador dele ({k.Name}) ainda não está neste PC. Baixar agora?", $"This is a {Consoles.Name(console)} disc. Its recompiler ({k.Name}) is not on this PC yet. Download it now?"),
                                  L.T("Baixar", "Download"), L.T("Cancelar", "Cancel"))) DownloadRecomp(k);
      return;
    }
    var match = recomp.Detect(path, out string boot);
    if (match == null && boot != null && recomp.Generic != null) { AddGenericPort(path, boot); return; }
    if (match == null) {
      Dialog.Info(this, Text, boot == null
          ? L.T("Este disco não é de nenhum jogo de " + recomp.Name + ", ou a imagem não é uma .iso de setores de 2048 bytes.", "This disc is not one of the " + recomp.Name + " games, or the image is not an .iso with 2048-byte sectors.")
          : L.T($"Este disco ({boot}) não é de nenhum jogo de {recomp.Name}. Jogos suportados:\n\n", $"This disc ({boot}) is not one of the {recomp.Name} games. Supported games:\n\n") +
            string.Join("\n", recomp.Games.Select(g => "•  " + g.Name + "  (" + g.Disc + ")")));
      return;
    }
    SelectGame(match.Id);
    if (game != match) return;
    var e = Current;
    if (e != null && e.Disc == null) { e.Disc = path; library.Invalidate(); }
    Install(path);
  }

  void AddGenericPort(string path, string boot) {
    if (Current != null && Current.Recomp != null && Current.Disc == path) recomp = Current.Recomp;
    if (Busy || recomp == null || path == null) return;
    if (boot == null) recomp.Detect(path, out boot);
    if (boot == null) { Dialog.Info(this, Text, L.T("Não foi possível ler este disco.", "This disc could not be read.")); return; }
    bool ps5 = Recomp.Ps5Title(path, out _) != null;
    if (ps5 ? !Dialog.Ask(this, Text, L.T(
            $"Este jogo ({boot}) ainda não foi convertido.\n\nO {recomp.Name} converte o executável do seu dump para um programa do Windows " +
            "(sem emulação) e liga os arquivos do jogo no lugar, sem copiar. Leva poucos minutos. Jogos ainda podem não funcionar ou parar em algum ponto.\n\nConverter?",
            $"This game ({boot}) has not been converted yet.\n\n{recomp.Name} converts your dump's executable into a Windows program " +
            "(no emulation) and links the game files in place, without copying. It takes a few minutes. Games may still not work or may stop somewhere.\n\nConvert it?"),
            L.T("Converter", "Convert"), L.T("Cancelar", "Cancel"))
        : !Dialog.Ask(this, Text, L.T(
            $"Este disco ({boot}) não tem um perfil em {recomp.Name}.\n\nDá para tentar montar mesmo assim com o caminho genérico, que serve para qualquer jogo: " +
            "ele analisa o seu disco, traduz o jogo inteiro e compila. Jogos sem perfil podem não funcionar ou parar em algum ponto; " +
            "o que faltar é traduzido enquanto você joga e entra na próxima montagem.\n\nTentar?",
            $"This disc ({boot}) has no profile in {recomp.Name}.\n\nThe generic path, which works for any game, can still try: " +
            "it analyses your disc, translates the whole game and compiles it. Games without a profile may not work or may stop somewhere; " +
            "whatever is missing is translated while you play and goes into the next build.\n\nTry it?"),
            L.T("Tentar", "Try"), L.T("Cancelar", "Cancel"))) return;
    string id = recomp.CreateGenericPort(path, boot);
    if (id == null) { Dialog.Info(this, Text, L.T("Não foi possível ler este disco.", "This disc could not be read.")); return; }
    ReloadRecomp(id);
    if (game?.Id == id) Install(path);
  }

  // ------------------------------------------------------------------ status

  void RefreshStatus() {
    bool busy = Busy;
    var e = Current;
    bool current = IsCurrent(e);
    if (current && e.Installed != Installed) { e.Installed = Installed; library.Invalidate(); }
    library.Locked = busy;
    tbPlay.Enabled = current && Installed && !busy;
    tbStop.Enabled = busy;
    tbInstall.Enabled = e != null && !busy && (current || e.NoProfile || e.NeedsRecomp);
    tbInstall.Text = e != null && e.NeedsRecomp ? L.T("Baixar", "Get") : e != null && e.NoProfile ? L.T("Montar", "Build") : current && Installed ? L.T("Reparar", "Repair") : L.T("Instalar", "Install");
    tbSettings.Enabled = current && !e.NoProfile;
    tbDeps.Enabled = current && !e.NoProfile;
    if (tbRecomps != null) tbRecomps.Enabled = !busy;
    tbOpen.Enabled = !busy;
    cancelButton.Enabled = busy;
    cancelButton.Text = SetupRunning || !busy ? L.T("Parar", "Stop") : L.T("Fechar jogo", "Close game");
    if (SetupRunning) statusLeft.Text = L.T("Instalando ", "Setting up ") + cfg.Game + L.T("... acompanhe no log. Pode demorar; deixe esta janela aberta.", "... follow it in the log. It takes a while; keep this window open.");
    else if (busy) statusLeft.Text = L.T("Em jogo: ", "Playing: ") + cfg.Game + L.T(". O launcher volta quando o jogo fechar.", ". The launcher comes back when the game closes.");
    else if (e == null) statusLeft.Text = "";
    else if (e.NeedsRecomp || e.Unsupported) statusLeft.Text = e.Disc ?? "";
    else if (e.NoProfile) statusLeft.Text = L.T("Disco sem perfil: ", "Disc without a profile: ") + e.Disc;
    else if (current && Installed) statusLeft.Text = L.T("Pronto para jogar  ·  ", "Ready to play  ·  ") + root;
    else if (root == null) statusLeft.Text = L.T($"Não instalado. Precisa de cerca de {cfg.DiskGb} GB livres e {cfg.Minutes} minutos.", $"Not installed. About {cfg.DiskGb} GB free and {cfg.Minutes} minutes are needed.");
    else if (e.Disc != null) statusLeft.Text = L.T("Disco encontrado: ", "Disc found: ") + e.Disc;
    else statusLeft.Text = L.T("Sem disco. Escolha a ISO ou adicione a pasta dela.", "No disc. Choose the ISO or add its folder.");
    Text = Title() + (e != null ? "  ·  " + e.Name : "");
    RenderDetail();
    UpdateCounts();
    UpdateDepButtons();
    if (!busy && depsDialog != null) CheckDeps();
  }

  // ------------------------------------------------------------------ updates

  async void CheckUpdates(bool manual) {
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
    if (IsDisposed) return;
    bool launcherNewer = Updater.CanReplaceSelf && Updater.IsNewer(rel, channel);
    if (launcherNewer && !manual && Prefs.Bool("update_auto", true) && !Updater.IsLocalBuild && !Busy) { UpdateLauncher(rel); return; }
    if (recompNewer && !manual && Prefs.Bool("update_recomp_auto", false) && !Busy) UpdateRecomp(r, target, label);
    else if (launcherNewer || recompNewer) {
      var actions = new List<KeyValuePair<string, Action>>();
      string msg;
      if (launcherNewer) {
        msg = L.T($"Nova versão do launcher: {rel.Display} (você tem {Updater.Describe()}).", $"New launcher version: {rel.Display} (you have {Updater.Describe()}).");
        actions.Add(new KeyValuePair<string, Action>(L.T("Atualizar launcher", "Update launcher"), () => UpdateLauncher(rel)));
        if (recompNewer) {
          msg += L.T($"  O {r.Name} também tem novidades ({label}).", $"  {r.Name} has news too ({label}).");
          actions.Add(new KeyValuePair<string, Action>(L.T("Atualizar ", "Update ") + r.Name, () => UpdateRecomp(r, target, label)));
        }
      } else {
        msg = L.T($"Há uma versão nova do {r.Name} ({label}{(target != null ? " " + target.Substring(0, 7) : "")}): perfis de jogos e correções.",
                  $"A new version of {r.Name} is out ({label}{(target != null ? " " + target.Substring(0, 7) : "")}): game profiles and fixes.");
        actions.Add(new KeyValuePair<string, Action>(L.T("Atualizar agora", "Update now"), () => UpdateRecomp(r, target, label)));
      }
      actions.Add(new KeyValuePair<string, Action>(L.T("Depois", "Later"), () => updateBar.Visible = false));
      updateBar.Show(Glyph.Update, Theme.Blue, msg, actions.ToArray());
    } else if (manual) {
      Dialog.Info(this, L.T("Atualizações", "Updates"), error != null
          ? L.T("Não foi possível procurar atualizações: ", "Could not check for updates: ") + error
          : L.T("Tudo atualizado.", "Everything is up to date.") + "\n\nLauncher: " + Updater.Describe() +
            (rel == null && channel == UpdateChannel.Nightly ? L.T(" (o canal nightly ainda não tem compilação)", " (the nightly channel has no build yet)") : "") +
            (r != null && local != null ? "\n" + r.Name + ": " + local.Substring(0, Math.Min(7, local.Length)) : ""));
    }
  }

  async void UpdateLauncher(LauncherRelease rel) {
    if (Busy) return;
    updateBar.Visible = false;
    SetLogVisible(true);
    stepLabel.Text = L.T("Atualizando o launcher para ", "Updating the launcher to ") + rel.Display + "...";
    try {
      await Task.Run(() => Updater.ReplaceSelf(rel, AppendLog));
    } catch (Exception ex) {
      AppendLog(ex.Message);
      stepLabel.Text = L.T("A atualização do launcher falhou.", "The launcher update failed.");
      return;
    }
    if (dirty && root != null) SaveSettings();
    Updater.Relaunch();
    Restart = false;
    Close();
  }

  async void UpdateRecomp(Recomp target, string sha, string label) {
    if (Busy || target == null || sha == null) return;
    recomp = target;
    updateBar.Visible = false;
    SetLogVisible(true);
    log.Clear();
    stepLabel.Text = L.T("Atualizando ", "Updating ") + recomp.Name + " (" + label + ")...";
    tbInstall.Enabled = tbOpen.Enabled = false;
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

  void ShowLog() => SetLogVisible(true);

  void AppendLog(string line) {
    if (line == null) return;
    if (InvokeRequired) { try { BeginInvoke(new Action<string>(AppendLog), line); } catch (Exception) { } return; }
    log.AppendText(line + Environment.NewLine);
    var m = Regex.Match(line, @"^== (\d)/(\d) (.*)$");
    if (m.Success) {
      progress.Maximum = int.Parse(m.Groups[2].Value);
      progress.Value = int.Parse(m.Groups[1].Value) - 1;
      stepLabel.Text = m.Groups[1].Value + "/" + m.Groups[2].Value + "   " + m.Groups[3].Value;
    }
  }

  string DefaultInstallFolder() {
    var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
        .OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
    return Path.Combine(best?.RootDirectory.FullName ?? "C:\\", "Games", RepoName);
  }

  async void Install(string source = null) {
    if (Busy) return;
    if (Current != null && !IsCurrent(Current)) return;
    pendingSource = source;
    string target = root;
    if (target == null) {
      target = DefaultInstallFolder();
      using (var d = new FolderBrowserDialog { Description = L.T("Onde instalar o jogo (uma pasta " + RepoName + " é criada dentro)", "Where to install the game (a " + RepoName + " folder is created inside)"),
                                               SelectedPath = Path.GetDirectoryName(target) }) {
        if (d.ShowDialog(this) != DialogResult.OK) return;
        target = Path.Combine(d.SelectedPath, RepoName);
      }
    }
    string msg = L.T(
        "O instalador vai:\n\n" +
        (root == null ? "•  baixar o kit do port do GitHub (só código-fonte, cerca de 1 MB)\n" : "") +
        (cfg.BuildToolsGb > 0 ? "•  instalar o Python e o Microsoft C++ Build Tools se faltarem (o Build Tools tem cerca de 7 GB e pede permissão de administrador)\n" : "") +
        "•  baixar as ferramentas de compilação que ainda não estiverem na pasta\n" +
        (source != null ? $"•  copiar os arquivos do jogo de {source}\n" : $"•  pedir o seu disco ({cfg.Disc}) ou a imagem .iso\n") +
        $"•  montar a versão para PC (cerca de {cfg.Minutes} minutos)\n\n" +
        $"Pasta: {target}",
        "The installer will:\n\n" +
        (root == null ? "•  download the port kit from GitHub (source code only, about 1 MB)\n" : "") +
        (cfg.BuildToolsGb > 0 ? "•  install Python and the Microsoft C++ Build Tools if missing (the Build Tools are about 7 GB and ask for administrator permission)\n" : "") +
        "•  download the build tools not in the folder yet\n" +
        (source != null ? $"•  copy the game files from {source}\n" : $"•  ask for your disc ({cfg.Disc}) or its .iso image\n") +
        $"•  build the PC version (about {cfg.Minutes} minutes)\n\n" +
        $"Folder: {target}");
    if (!Dialog.Ask(this, cfg.Game, msg, L.T("Instalar", "Install"), L.T("Cancelar", "Cancel"))) return;

    ShowLog();
    log.Clear();
    progress.Value = 0;
    tbInstall.Enabled = false;
    try {
      if (root == null) {
        stepLabel.Text = L.T("Baixando o kit do port...", "Downloading the port kit...");
        root = await Task.Run(() => KitDownload.Fetch(cfg, target, AppendLog));
        if (Current != null) Current.Dir = root;
      }
    } catch (Exception ex) {
      AppendLog(ex.Message);
      stepLabel.Text = L.T("O download do kit falhou.", "Downloading the kit failed.");
      RefreshStatus();
      return;
    }
    RunSetup();
  }

  static readonly Encoding Oem = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

  void RunSetup(string only = null) {
    var psi = new ProcessStartInfo {
      FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
      Arguments = SetupArguments(only),
      WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
      RedirectStandardOutput = true, RedirectStandardError = true,
      StandardOutputEncoding = Oem, StandardErrorEncoding = Oem,
    };
    psi.EnvironmentVariables["PORT_LAUNCHER"] = "1";
    // PowerShell writes the console's OEM code page into the pipe; Python is told to do the same.
    psi.EnvironmentVariables["PYTHONIOENCODING"] = "cp" + Oem.CodePage + ":replace";
    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
    p.OutputDataReceived += (s, e) => AppendLog(e.Data);
    p.ErrorDataReceived += (s, e) => AppendLog(e.Data);
    p.Exited += (s, e) => BeginInvoke(new Action(() => SetupFinished(p.ExitCode, only)));
    running = p;
    stepLabel.Text = L.T("Preparando...", "Starting...");
    p.Start();
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    RefreshStatus();
  }

  string SetupArguments(string only) {
    string args;
    if (Lib && recomp != null) {
      recomp.EnsureInstallerFiles(AppendLog);
      args = $"-NoProfile -ExecutionPolicy Bypass -File \"{recomp.Setup}\" -Yes -NoShortcut -GameDir \"{root}\" -ToolsDir \"{recomp.ToolsDir}\"";
    } else {
      args = $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root, cfg.Setup)}\" -Yes -NoShortcut";
    }
    if (only != null) args += " -Only " + only;
    if (pendingSource != null && (only == null || only == "game")) args += $" -Source \"{pendingSource}\"";
    pendingSource = null;
    return args;
  }

  void SetupFinished(int code, string only) {
    running = null;
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

  void StopRunning() {
    if (!Busy) return;
    bool setup = SetupRunning;
    if (!Dialog.Ask(this, Text, setup ? L.T("Parar agora? Dá para continuar depois: as etapas prontas são puladas.", "Stop now? You can continue later: finished steps are skipped.")
                                      : L.T("Fechar o jogo agora? O que não foi salvo no jogo se perde.", "Close the game now? Anything not saved in the game is lost."),
                    setup ? L.T("Parar", "Stop") : L.T("Fechar o jogo", "Close the game"), L.T("Continuar", "Keep going"), true)) return;
    KillTree(running.Id);
  }

  static void KillTree(int pid) {
    try { Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {pid}") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(); }
    catch (Exception) { }
  }

  // The launcher itself is the shortcut target: it plays and keeps the settings at hand.
  void CreateShortcut() {
    try {
      // Recompiler mode: this launcher, opened on the game; a kit: a copy of the launcher inside it.
      string exe = Lib ? launcherPath : Path.Combine(root, Path.GetFileName(launcherPath));
      if (!File.Exists(exe)) File.Copy(launcherPath, exe);
      var shellType = Type.GetTypeFromProgID("WScript.Shell");
      dynamic shell = Activator.CreateInstance(shellType);
      dynamic link = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), cfg.Game + ".lnk"));
      link.TargetPath = exe;
      if (Lib && recomp != null && game != null) link.Arguments = $"--game \"{registry.KnownFor(recomp)?.Id}/{game.Id}\"";
      link.WorkingDirectory = root;
      link.IconLocation = exe + ",0";
      link.Save();
    } catch (Exception ex) {
      AppendLog("Shortcut: " + ex.Message);
    }
  }

  void OnClosing(object sender, FormClosingEventArgs e) {
    scanCancel?.Cancel();
    ++coverPass;
    if (Busy) {
      if (!Dialog.Ask(this, Text, SetupRunning ? L.T("A instalação ainda está rodando. Parar e fechar?", "Setup is still running. Stop it and close?")
                                               : L.T("O jogo ainda está aberto. Fechar o jogo e o launcher?", "The game is still open. Close the game and the launcher?"),
                      L.T("Parar e fechar", "Stop and close"), L.T("Voltar", "Back"), true)) { e.Cancel = true; Restart = false; return; }
      KillTree(running.Id);
    }
  }

  // ------------------------------------------------------------------ play

  void Play() {
    if (!Installed || Busy || !IsCurrent(Current)) return;
    var output = new StringBuilder();
    var psi = new ProcessStartInfo {
      FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
      Arguments = $"/c \"\"{Path.Combine(root, cfg.Play)}\" < nul\"",
      WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
      RedirectStandardOutput = true, RedirectStandardError = true,
    };
    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
    p.Exited += (s, e) => BeginInvoke(new Action(() => {
      running = null;
      WindowState = FormWindowState.Normal;
      Activate();
      if (p.ExitCode != 0) {
        string text;
        lock (output) text = output.ToString().Trim();
        if (text.Length > 2000) text = "..." + text.Substring(text.Length - 2000);
        Dialog.Info(this, Text, L.T("O jogo fechou com erro (código ", "The game closed with an error (code ") + p.ExitCode + ").\n\n" + text);
      }
      RefreshStatus();
    }));
    running = p;
    p.Start();
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    WindowState = FormWindowState.Minimized;
    RefreshStatus();
  }

  // ------------------------------------------------------------------ PC settings and dependencies windows

  Form OpenSettingsDialog() {
    editors.Clear(); rows.Clear(); monitorCombo = refreshCombo = null;
    pageSettings = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(26, 20, 26, 18) };
    BuildSettingsPage();
    LoadSettings();
    var dlg = PageDialog(L.T("Configurações do PC", "PC settings") + "  ·  " + cfg.Game, pageSettings, new Size(920, 680));
    dlg.FormClosing += (s, e) => { if (dirty && root != null) SaveSettings(); dirty = false; };
    dlg.FormClosed += (s, e) => { editors.Clear(); rows.Clear(); monitorCombo = refreshCombo = null; pageSettings = null; };
    return dlg;
  }

  void ShowSettings() {
    if (Current == null || !IsCurrent(Current) || Current.NoProfile) return;
    using (var dlg = OpenSettingsDialog()) dlg.ShowDialog(this);
  }

  Form OpenDepsDialog() {
    pageDeps = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(26, 20, 26, 18) };
    BuildDepsPage();
    var dlg = PageDialog(L.T("Dependências", "Dependencies") + "  ·  " + cfg.Game, pageDeps, new Size(940, 600));
    depsDialog = dlg;
    dlg.FormClosed += (s, e) => { depsDialog = null; depsList = null; depsAllButton = null; depsRecheckButton = null; depsSummary = null; depButtons.Clear(); pageDeps = null; };
    depsChecking = false;
    CheckDeps();
    return dlg;
  }

  void ShowDeps() {
    if (Current == null || !IsCurrent(Current) || Current.NoProfile) return;
    using (var dlg = OpenDepsDialog()) dlg.ShowDialog(this);
  }

  // A button in the dependencies window: close it, then act in the main window (the log shows there).
  void FromDeps(Action action) {
    depsDialog?.Close();
    BeginInvoke(action);
  }
}

// The state pill of the game panel.
sealed class PillLabel : Control {
  Color color = Theme.Text3;
  public PillLabel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); Size = new Size(200, 26); }
  public void Set(string text, Color c) { Text = text; color = c; Invalidate(); }
  protected override void OnPaint(PaintEventArgs e) {
    e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
    Theme.Smooth(e.Graphics);
    GameLibrary.Pill(e.Graphics, Text, color, new Point(0, Height / 2), false);
  }
}

// File > Game folders: the folders searched for discs, like an emulator's game directories list.
sealed class FoldersDialog : Form {
  readonly ListBox list = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, IntegralHeight = false };
  public FoldersDialog() {
    Text = L.T("Pastas de jogos", "Game folders");
    Font = Theme.UI(10f); AutoScaleMode = AutoScaleMode.Dpi; ClientSize = new Size(680, 440); MinimumSize = new Size(520, 340);
    StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Bg; ForeColor = Theme.Text; ShowInTaskbar = false; MinimizeBox = false;
    Padding = new Padding(24, 18, 24, 16);
    Theme.DarkTitle(this);
    var head = new Label {
      Dock = DockStyle.Top, Height = 54, ForeColor = Theme.Text2,
      Text = L.T("O launcher procura discos (.iso e pastas de disco extraído) nestas pastas e nas subpastas delas, e reconhece cada jogo pelo próprio disco.",
                 "The launcher looks for discs (.iso and extracted disc folders) in these folders and their subfolders, and recognises each game from the disc itself."),
    };
    var card = new Card { Dock = DockStyle.Fill, Padding = new Padding(6, 6, 6, 6) };
    list.BackColor = Theme.Surface; list.ForeColor = Theme.Text; list.ItemHeight = (int)(40 * DeviceDpi / 96f);
    list.DrawItem += DrawItem;
    Theme.DarkScroll(list);
    card.Controls.Add(list);
    var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 14, 0, 0), BackColor = Theme.Bg };
    var close = new FlatButton(L.T("Fechar", "Close"), ButtonKind.Primary, 120, 38) { Margin = Padding.Empty };
    close.Click += (s, e) => Close();
    var remove = new FlatButton(L.T("Remover", "Remove"), ButtonKind.Secondary, 120, 38) { Margin = new Padding(0, 0, 10, 0) };
    remove.Click += (s, e) => {
      if (list.SelectedItem == null) return;
      list.Items.Remove(list.SelectedItem);
      GameFolders.Save(list.Items.Cast<string>());
    };
    var add = new FlatButton(L.T("Adicionar pastas...", "Add folders..."), ButtonKind.Blue, 170, 38) { Margin = new Padding(0, 0, 10, 0) };
    add.Click += (s, e) => {
      GameFolders.Add(FolderPicker.Pick(this, L.T("Escolha uma ou mais pastas com jogos", "Choose one or more folders with games")));
      Fill();
    };
    bar.Controls.Add(close);
    bar.Controls.Add(remove);
    bar.Controls.Add(add);
    Controls.Add(card);
    Controls.Add(head);
    Controls.Add(bar);
    CancelButton = close;
    Fill();
  }

  void Fill() {
    list.Items.Clear();
    foreach (string f in GameFolders.Load()) list.Items.Add(f);
  }

  void DrawItem(object sender, DrawItemEventArgs e) {
    var g = e.Graphics;
    Theme.Smooth(g);
    bool sel = (e.State & DrawItemState.Selected) != 0;
    using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, e.Bounds);
    if (e.Index < 0) return;
    if (sel) using (var b = new SolidBrush(Theme.Selection)) using (var path = Theme.Round(new RectangleF(e.Bounds.X + 2, e.Bounds.Y + 2, e.Bounds.Width - 4, e.Bounds.Height - 4), 6)) g.FillPath(b, path);
    string path0 = (string)list.Items[e.Index];
    float k = DeviceDpi / 96f, s = 18 * k;
    Glyphs.Draw(g, Glyph.Folder, new RectangleF(e.Bounds.X + 12 * k, e.Bounds.Y + (e.Bounds.Height - s) / 2, s, s), Directory.Exists(path0) ? Theme.Accent : Theme.Danger);
    TextRenderer.DrawText(g, path0 + (Directory.Exists(path0) ? "" : L.T("   (não encontrada)", "   (not found)")), Font,
                          new Rectangle(e.Bounds.X + (int)(42 * k), e.Bounds.Y, e.Bounds.Width - (int)(50 * k), e.Bounds.Height), Theme.Text,
                          TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPadding);
  }
}

}
