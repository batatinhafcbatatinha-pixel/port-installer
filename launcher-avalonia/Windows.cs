// The Linux/macOS launcher's other windows, like the Windows ones: message boxes, the one-line question, the game
// folders list, the recompilers, the PC settings, the dependencies and the patches/cheats; and the system's
// file and folder pickers.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PortLauncher {

// A window in the launcher's colours that closes with Escape.
class StyledWindow : Window {
  public StyledWindow(string title, double width, double height) {
    Title = title; Width = width; Height = height;
    Background = Look.B(Look.Bg); Foreground = Look.B(Look.Text);
    FontFamily = Look.UiFamily; FontSize = Look.Pt(10);
    WindowStartupLocation = WindowStartupLocation.CenterOwner;
    ShowInTaskbar = false;
    App.SetIcon(this);
    KeyDown += (s, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
  }

  public static TextBlock Words(string text, Typeface face, double points, Color color, Thickness margin, bool wrap = true) =>
      new TextBlock { Text = text, FontFamily = face.FontFamily, FontWeight = face.Weight, FontSize = Look.Pt(points), Foreground = Look.B(color), Margin = margin,
                      TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };

  public static ScrollViewer Scroller(Control content) => new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

  public static Border Line() => new Border { Height = 1, Background = Look.B(Look.Border) };
}

// Message boxes in the launcher's colours.
static class Dialog {
  static async Task<bool> Show(Window owner, string title, string text, string yes, string no, bool danger) {
    var w = new StyledWindow(title, 480, 200) { SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Background = Look.B(Look.Surface) };
    bool result = false;
    var body = StyledWindow.Words(text, Look.UI(), 10, Look.Text2, new Thickness(0));
    body.MaxWidth = 560;
    var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
    if (no != null) {
      var cancel = new FlatButton(no, ButtonKind.Secondary, 130, 38) { Margin = new Thickness(0, 0, 10, 0) };
      cancel.Click += (s, e) => w.Close();
      row.Children.Add(cancel);
    }
    var ok = new FlatButton(yes, danger ? ButtonKind.Danger : ButtonKind.Primary, 130, 38);
    ok.Click += (s, e) => { result = true; w.Close(); };
    row.Children.Add(ok);
    var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 18), MinWidth = 372 };
    stack.Children.Add(body);
    stack.Children.Add(row);
    w.Content = stack;
    w.KeyDown += (s, e) => { if (e.Key == Key.Enter) { result = true; w.Close(); } };
    w.Opened += (s, e) => ok.Focus();
    if (owner != null && owner.IsVisible) await w.ShowDialog(owner);
    else { var done = new TaskCompletionSource<bool>(); w.Closed += (s, e) => done.TrySetResult(true); w.Show(); await done.Task; }
    return result;
  }

  // Test aid (--install): every question is answered yes and every message only goes to the log.
  public static Action<string> Auto;

  public static Task<bool> Ask(Window owner, string title, string text, string yes, string no, bool danger = false) {
    if (Auto != null) { Auto("[" + title + "] " + text.Replace('\n', ' ') + " -> " + yes); return Task.FromResult(true); }
    return Show(owner, title, text, yes, no, danger);
  }
  public static Task Info(Window owner, string title, string text) {
    if (Auto != null) { Auto("[" + title + "] " + text.Replace('\n', ' ')); return Task.CompletedTask; }
    return Show(owner, title, text, "OK", null, false);
  }
}

// A one-line text question in the launcher's style.
static class Prompt {
  public static async Task<string> Ask(Window owner, string title, string text) {
    var w = new StyledWindow(title, 600, 200) { SizeToContent = SizeToContent.Height, CanResize = false };
    string result = null;
    var box = new TextBox { Width = 540, Margin = new Thickness(0, 0, 0, 14) };
    var ok = new FlatButton("OK", ButtonKind.Primary, 120, 36) { Margin = new Thickness(0, 0, 10, 0) };
    ok.Click += (s, e) => { result = box.Text; w.Close(); };
    var cancel = new FlatButton(L.T("Cancelar", "Cancel"), ButtonKind.Secondary, 120, 36);
    cancel.Click += (s, e) => w.Close();
    box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { result = box.Text; w.Close(); } };
    var row = new StackPanel { Orientation = Orientation.Horizontal };
    row.Children.Add(ok); row.Children.Add(cancel);
    var stack = new StackPanel { Margin = new Thickness(24, 18, 24, 12) };
    stack.Children.Add(StyledWindow.Words(text, Look.UI(), 10, Look.Text2, new Thickness(0, 0, 0, 10)));
    stack.Children.Add(box);
    stack.Children.Add(row);
    w.Content = stack;
    w.Opened += (s, e) => box.Focus();
    await w.ShowDialog(owner);
    return result;
  }
}

// The system's pickers (the desktop portal on Linux, the Finder panel on macOS).
static class Pickers {
  static async Task<IStorageFolder> Start(Window w, string path) {
    try { return string.IsNullOrEmpty(path) || !Directory.Exists(path) ? null : await w.StorageProvider.TryGetFolderFromPathAsync(path); } catch (Exception) { return null; }
  }

  public static async Task<List<string>> Folders(Window w, string title, string start = null) {
    var list = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = true, SuggestedStartLocation = await Start(w, start) });
    return list.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
  }

  public static async Task<string> Folder(Window w, string title, string start = null) {
    var list = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = await Start(w, start) });
    return list.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null);
  }

  public static async Task<string> File(Window w, string title, string kind, params string[] patterns) {
    var list = await w.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
      Title = title, AllowMultiple = false,
      FileTypeFilter = new[] { new FilePickerFileType(kind) { Patterns = patterns }, new FilePickerFileType(L.T("Todos os arquivos", "All files")) { Patterns = new[] { "*" } } },
    });
    return list.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null);
  }
}

// File > Game folders: the folders searched for discs.
sealed class FoldersWindow : StyledWindow {
  readonly StackPanel list = new StackPanel();
  string selected;
  public FoldersWindow() : base(L.T("Pastas de jogos", "Game folders"), 680, 440) {
    MinWidth = 520; MinHeight = 340;
    var head = Words(L.T("O launcher procura discos (.iso e pastas de disco extraído) nestas pastas e nas subpastas delas, e reconhece cada jogo pelo próprio disco.",
                         "The launcher looks for discs (.iso and extracted disc folders) in these folders and their subfolders, and recognises each game from the disc itself."),
                     Look.UI(), 10, Look.Text2, new Thickness(0, 0, 0, 14));
    var card = new Card { Padding = new Thickness(6), Child = Scroller(list) };
    var close = new FlatButton(L.T("Fechar", "Close"), ButtonKind.Primary, 120, 38);
    close.Click += (s, e) => Close();
    var remove = new FlatButton(L.T("Remover", "Remove"), ButtonKind.Secondary, 120, 38) { Margin = new Thickness(0, 0, 10, 0) };
    remove.Click += (s, e) => {
      if (selected == null) return;
      GameFolders.Save(GameFolders.Load().Where(f => f != selected));
      selected = null;
      Fill();
    };
    var add = new FlatButton(L.T("Adicionar pastas...", "Add folders..."), ButtonKind.Blue, 170, 38) { Margin = new Thickness(0, 0, 10, 0) };
    add.Click += async (s, e) => { GameFolders.Add(await Pickers.Folders(this, L.T("Escolha uma ou mais pastas com jogos", "Choose one or more folders with games"))); Fill(); };
    var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
    bar.Children.Add(add); bar.Children.Add(remove); bar.Children.Add(close);
    var dock = new DockPanel { Margin = new Thickness(24, 18, 24, 16) };
    DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(bar, Dock.Bottom);
    dock.Children.Add(head); dock.Children.Add(bar); dock.Children.Add(card);
    Content = dock;
    Fill();
  }

  void Fill() {
    list.Children.Clear();
    foreach (string f in GameFolders.Load()) {
      string path = f;
      bool exists = Directory.Exists(path);
      var row = new Border { Height = 40, CornerRadius = new CornerRadius(6), Background = Look.B(path == selected ? Look.Selection : Look.Surface), Cursor = new Cursor(StandardCursorType.Hand) };
      var line = new DockPanel();
      var icon = new GlyphView(Glyph.Folder, exists ? Look.Accent : Look.Danger) { Width = 18, Height = 18, Margin = new Thickness(12, 0, 12, 0) };
      DockPanel.SetDock(icon, Dock.Left);
      line.Children.Add(icon);
      var text = Words(path + (exists ? "" : L.T("   (não encontrada)", "   (not found)")), Look.UI(), 10, Look.Text, new Thickness(0), false);
      text.VerticalAlignment = VerticalAlignment.Center;
      line.Children.Add(text);
      row.Child = line;
      row.PointerPressed += (s, e) => { selected = path; Fill(); };
      list.Children.Add(row);
    }
  }
}

// A line icon as a control.
sealed class GlyphView : Control {
  readonly Glyph glyph;
  readonly Color color;
  public GlyphView(Glyph g, Color c) { glyph = g; color = c; }
  public override void Render(DrawingContext g) => Glyphs.Draw(g, glyph, new Rect(Bounds.Size), color);
}

// The Recompilers window: every recompiler the launcher knows, whether it is on this computer and whether it has a
// version for this system; buttons to download one, point at a folder that already holds it, or open it.
sealed class RecompsWindow : StyledWindow {
  readonly RecompRegistry reg;
  readonly StackPanel list = new StackPanel();
  public KnownRecomp Download;
  public bool Changed;

  public RecompsWindow(RecompRegistry registry, Window owner) : base(L.T("Recompiladores", "Recompilers"), 860, 560) {
    reg = registry;
    var head = Words(L.T("Cada console tem o seu recompilador. Baixe os que quiser: só o código-fonte vem do GitHub, nada de nenhum jogo. Os jogos de cada um aparecem na aba do console.",
                         "Each console has its own recompiler. Get the ones you want: only source code comes from GitHub, nothing from any game. Each one's games show up in its console tab."),
                     Look.UI(), 10, Look.Text2, new Thickness(0, 0, 0, 14));
    var dock = new DockPanel { Margin = new Thickness(26, 20, 26, 18) };
    DockPanel.SetDock(head, Dock.Top);
    dock.Children.Add(head);
    dock.Children.Add(Scroller(list));
    Content = dock;
    Fill();
  }

  void Fill() {
    list.Children.Clear();
    foreach (var k in reg.Known) {
      reg.Installed.TryGetValue(k.Id, out var r);
      bool here = r != null ? r.Supported : k.Supported;
      var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
      text.Children.Add(Words(k.Name, Look.Semi, 11.5, Look.Text, new Thickness(0)));
      string sub = r != null ? L.T("Instalado", "Installed") + "  ·  " + r.Games.Count + L.T(" perfis de jogos", " game profiles") + "\n" + Compact(r.Root) : (L.Pt ? k.Pt : k.En);
      text.Children.Add(Words(sub, Look.UI(), 10, r != null ? Look.Accent : Look.Text3, new Thickness(0, 4, 0, 0)));
      if (!here) text.Children.Add(Words(L.T($"Ainda sem versão para {OS.Name}: os jogos dele só montam no Windows por enquanto.", $"No {OS.Name} version yet: its games only build on Windows for now."),
                                         Look.UI(), 9.5, Look.Warn, new Thickness(0, 4, 0, 0)));
      var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
      if (r == null) {
        var get = new FlatButton(L.T("Baixar", "Download"), ButtonKind.Primary, 120, 38) { Margin = new Thickness(0, 0, 8, 0), IsEnabled = here };
        get.Click += (s, e) => { Download = k; Close(); };
        var locate = new FlatButton(L.T("Já tenho...", "Locate..."), ButtonKind.Secondary, 120, 38);
        locate.Click += (s, e) => Locate(k);
        buttons.Children.Add(get);
        buttons.Children.Add(locate);
      } else {
        var open = new FlatButton(L.T("Abrir pasta", "Open folder"), ButtonKind.Secondary, 130, 38) { Margin = new Thickness(0, 0, 8, 0) };
        var root = r.Root;
        open.Click += (s, e) => OS.OpenFolder(root);
        buttons.Children.Add(open);
        if (reg.Home != k.Id) {
          var forget = new FlatButton(L.T("Tirar da lista", "Remove"), ButtonKind.Secondary, 130, 38);
          forget.Click += async (s, e) => {
            if (!await Dialog.Ask(this, Title, L.T($"Tirar o {k.Name} do launcher? A pasta fica no disco ({root}).", $"Remove {k.Name} from the launcher? The folder stays on disk ({root})."),
                                  L.T("Tirar", "Remove"), L.T("Cancelar", "Cancel"))) return;
            reg.Forget(k.Id); Changed = true; Fill();
          };
          buttons.Children.Add(forget);
        }
      }
      var row = new DockPanel();
      DockPanel.SetDock(buttons, Dock.Right);
      row.Children.Add(buttons);
      row.Children.Add(text);
      list.Children.Add(new Card { Padding = new Thickness(18, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10), Child = row });
    }
  }

  static string Compact(string path) => path.Length <= 64 ? path : path.Substring(0, 18) + "..." + path.Substring(path.Length - 42);

  async void Locate(KnownRecomp k) {
    string dir = await Pickers.Folder(this, L.T("A pasta do ", "The folder of ") + k.Name);
    if (dir == null) return;
    Recomp r = null;
    try { r = Recomp.Find(dir); } catch (Exception) { }
    if (r == null || reg.Match(r)?.Id != k.Id) {
      await Dialog.Info(this, Title, L.T("Esta pasta não é do ", "This folder is not ") + k.Name + ".");
      return;
    }
    reg.Remember(k.Id, r.Root);
    reg.Installed[k.Id] = r;
    Changed = true;
    Fill();
  }
}

sealed partial class MainWindow {
  // ------------------------------------------------------------------ recompilers

  public async void ShowRecomps() {
    if (!Lib || Busy) return;
    var d = new RecompsWindow(registry, this);
    await d.ShowDialog(this);
    if (d.Download != null) DownloadRecomp(d.Download);
    else if (d.Changed) Rescan();
  }

  string DefaultRecompParent() {
    if (registry.Home != null && registry.Installed.TryGetValue(registry.Home, out var home)) return Path.GetDirectoryName(home.Root.TrimEnd(Path.DirectorySeparatorChar));
    return Texts.DefaultGamesFolder();
  }

  async void DownloadRecomp(KnownRecomp k) {
    if (Busy || k == null) return;
    if (!k.Supported) { await Dialog.Info(this, k.Name, L.T($"O {k.Name} ainda não tem versão para {OS.Name}.", $"{k.Name} has no {OS.Name} version yet.")); return; }
    string parent = DefaultRecompParent();
    try { Directory.CreateDirectory(parent); } catch (Exception) { }
    parent = await Pickers.Folder(this, L.T($"Onde guardar o {k.Name} (uma pasta {k.Folder} é criada dentro)", $"Where to keep {k.Name} (a {k.Folder} folder is created inside)"), parent);
    if (parent == null) return;
    string target = Path.Combine(parent, k.Folder);
    SetLogVisible(true);
    ClearLog();
    stepLabel.Text = L.T("Baixando ", "Downloading ") + k.Name + "...";
    tbRecomps.IsEnabled = tbInstall.IsEnabled = tbOpen.IsEnabled = false;
    try {
      string dir = File.Exists(Path.Combine(target, "launcher.json")) ? target : await Task.Run(() => Recomp.Download(k.Repo, k.Branch, target, AppendLog));
      var r = Recomp.Load(dir);
      if (r == null) throw new IOException(L.T("A pasta baixada não tem launcher.json.", "The downloaded folder has no launcher.json."));
      r.EnsureInstallerFiles(AppendLog);
      registry.Remember(k.Id, r.Root);
      registry.Installed[k.Id] = r;
      stepLabel.Text = k.Name + L.T(" pronto. Procurando os jogos dele nas suas pastas...", " ready. Looking for its games in your folders...");
    } catch (Exception ex) {
      AppendLog(ex.Message);
      stepLabel.Text = L.T("O download parou. Veja o log.", "The download stopped. See the log.");
    }
    RefreshStatus();
    Rescan();
  }

  // ------------------------------------------------------------------ PC settings

  readonly Dictionary<Setting, Control> editors = new Dictionary<Setting, Control>();
  readonly Dictionary<Setting, Control> rows = new Dictionary<Setting, Control>();
  ChoiceBox monitorCombo, refreshCombo;
  TextBlock settingsNote, saveState;
  FlatButton saveButton, defaultsButton;
  bool dirty, loading;

  public void ShowSettings() {
    if (Current == null || !IsCurrent(Current) || Current.NoProfile || Current.NoRuntime) return;
    OpenSettingsWindow().ShowDialog(this);
  }

  public Window OpenSettingsWindow() {
    editors.Clear(); rows.Clear(); monitorCombo = refreshCombo = null;
    var w = new StyledWindow(L.T("Configurações do PC", "PC settings") + "  ·  " + cfg.Game, 920, 680) { MinWidth = 760, MinHeight = 480 };
    var stack = new StackPanel();
    settingsNote = Words("", Look.UI(), 9.5, Look.Text3, new Thickness(4, 0, 4, 14));
    stack.Children.Add(settingsNote);
    var monitors = Displays.Monitors();
    var gpus = Gpus.List();
    foreach (var g in cfg.Groups) {
      var inner = new StackPanel();
      inner.Children.Add(Words(L.T(g).ToUpperInvariant(), Look.Semi, 9.5, Look.Accent, new Thickness(0, 0, 0, 4)));
      bool first = true;
      foreach (var s in g.Items) {
        if (s.Store == "gpu_pref" && !OS.Windows) continue;   // Windows' per-program graphics preference
        var left = new StackPanel { Margin = new Thickness(0, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Words(L.T(s), Look.Semi, 10.5, Look.Text, new Thickness(0, 0, 0, 2)));
        string help = (L.Pt ? s.HelpPt : s.HelpEn) + (s.Restart ? L.T(" Vale na próxima vez que o jogo abrir.", " Applies the next time the game starts.") : "");
        if (help.Trim().Length > 0) left.Children.Add(Words(help.Trim(), Look.UI(), 9, Look.Text3, new Thickness(0)));
        Control editor;
        if (s.Max > s.Min) {
          var slider = new SliderBox();
          slider.Range(s.Min, s.Max, s.Step);
          slider.ValueChanged += (o, e) => Changed();
          editor = slider;
        } else if (SettingsStore.IsToggle(s)) {
          var toggle = new ToggleSwitch { Width = 360, OnText = L.T(s.Choices.First(c => c.Value == 1)), OffText = L.T(s.Choices.First(c => c.Value == 0)) };
          toggle.CheckedChanged += (o, e) => Changed();
          editor = toggle;
        } else {
          var box = new ChoiceBox();
          FillChoices(s, box, monitors, gpus);
          box.SelectedIndexChanged += (o, e) => { if (box == monitorCombo) RefillRefresh(); Changed(); };
          editor = box;
        }
        editor.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,380"), Margin = new Thickness(0, 12) };
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(left); row.Children.Add(editor);
        var wrap = new StackPanel();
        if (!first) wrap.Children.Add(StyledWindow.Line());
        first = false;
        wrap.Children.Add(row);
        inner.Children.Add(wrap);
        editors[s] = editor;
        rows[s] = wrap;
      }
      stack.Children.Add(new Card { Margin = new Thickness(0, 0, 0, 16), Child = inner });
    }
    saveState = Words("", Look.UI(), 10, Look.Text3, new Thickness(4, 10, 0, 0), false);
    defaultsButton = new FlatButton(L.T("Restaurar padrões", "Restore defaults"), ButtonKind.Secondary, 190, 40) { Margin = new Thickness(0, 0, 10, 0) };
    defaultsButton.Click += (s, e) => { foreach (var kv in editors) SetEditor(kv.Key, kv.Key.Default); Changed(); };
    saveButton = new FlatButton(L.T("Salvar", "Save"), ButtonKind.Primary, 150, 40);
    saveButton.Click += (s, e) => SaveSettings();
    var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
    var buttons = new StackPanel { Orientation = Orientation.Horizontal };
    buttons.Children.Add(defaultsButton); buttons.Children.Add(saveButton);
    DockPanel.SetDock(buttons, Dock.Right);
    bottom.Children.Add(buttons); bottom.Children.Add(saveState);
    var dock = new DockPanel { Margin = new Thickness(26, 20, 26, 18) };
    DockPanel.SetDock(bottom, Dock.Bottom);
    dock.Children.Add(bottom);
    dock.Children.Add(StyledWindow.Scroller(stack));
    w.Content = dock;
    LoadSettings();
    w.Closing += (s, e) => { if (dirty && root != null) SaveSettings(); dirty = false; };
    w.Closed += (s, e) => { editors.Clear(); rows.Clear(); monitorCombo = refreshCombo = null; };
    return w;
  }

  void Changed() {
    if (loading) return;
    dirty = true;
    saveState.Text = L.T("Alterações não salvas", "Unsaved changes");
    saveState.Foreground = Look.B(Look.Text2);
  }

  void FillChoices(Setting s, ChoiceBox box, List<MonitorInfo> monitors, List<KeyValuePair<int, string>> gpus) {
    box.Items.Clear();
    switch (s.Source) {
      case "monitors":
        monitorCombo = box;
        for (int i = 0; i < Math.Max(1, monitors.Count); ++i)
          box.Items.Add(new ChoiceBox.Entry { Value = i, Text = (i + 1) + ": " + (i < monitors.Count ? monitors[i].Name : "?") });
        box.Tag2 = monitors;
        return;
      case "refresh":
        refreshCombo = box;
        box.Tag2 = s;
        RefillRefresh();
        return;
      case "gpus":
        box.Items.Add(new ChoiceBox.Entry { Value = -1, Text = L.T("Automática (padrão do sistema)", "Automatic (system default)") });
        foreach (var g in gpus) box.Items.Add(new ChoiceBox.Entry { Value = g.Key, Text = g.Value });
        return;
    }
    foreach (var c in s.Choices) box.Items.Add(new ChoiceBox.Entry { Value = c.Value, Text = L.T(c) });
  }

  void RefillRefresh() {
    if (refreshCombo == null) return;
    var monitors = monitorCombo?.Tag2 as List<MonitorInfo> ?? Displays.Monitors();
    int index = monitorCombo?.Selected?.Value ?? 0;
    string device = monitors != null && index < monitors.Count ? monitors[index].Device : null;
    int keep = refreshCombo.Selected?.Value ?? 0;
    var rates = Displays.RefreshRates(device, out int current);
    refreshCombo.Items.Clear();
    var zero = refreshCombo.Tag2 as Setting;
    var named = zero?.Choices.FirstOrDefault(c => c.Value == 0);
    refreshCombo.Items.Add(new ChoiceBox.Entry { Value = 0, Text = named != null ? L.T(named) : L.T($"A do monitor ({current} Hz)", $"Monitor refresh rate ({current} Hz)") });
    foreach (int hz in rates) refreshCombo.Items.Add(new ChoiceBox.Entry { Value = hz, Text = hz + " Hz" });
    refreshCombo.Select(keep, 0);
    refreshCombo.InvalidateVisual();
  }

  void SetEditor(Setting s, int value) {
    switch (editors[s]) {
      case ChoiceBox box: box.Select(value, s.Default); break;
      case ToggleSwitch t: t.Checked = value != 0; break;
      case SliderBox sl: sl.Value = value; break;
    }
  }

  int GetEditor(Setting s) {
    switch (editors[s]) {
      case ChoiceBox box: return box.Selected?.Value ?? s.Default;
      case ToggleSwitch t: return t.Checked ? 1 : 0;
      case SliderBox sl: return sl.Value;
    }
    return s.Default;
  }

  void LoadSettings() {
    string ini = IniPath;
    bool ready = ini != null && Directory.Exists(Path.GetDirectoryName(ini));
    settingsNote.Text = ready
        ? (cfg.PcMenu ? L.T("As mesmas opções do menu F1 do jogo, salvas em ", "The same options as the game's F1 menu, saved in ")
                      : L.T("Opções do PC, salvas em ", "PC options, saved in ")) + Path.GetFileName(ini) +
          L.T(". O jogo usa estas opções quando abrir.", ". The game uses them when it starts.")
        : L.T("Dá para conferir as opções agora; para salvar, instale o jogo primeiro (as configurações ficam ao lado dele).",
              "You can look at the options now; to save them, install the game first (the settings are kept next to it).");
    saveButton.IsEnabled = ready; defaultsButton.IsEnabled = ready;
    loading = true;
    foreach (var kv in rows) kv.Value.IsVisible = SettingsStore.Visible(kv.Key, cfg, root);
    var plain = ready && cfg.PlainIni ? PlainIni.Read(ini) : null;
    foreach (var s in editors.Keys.OrderBy(k => k.Source == "monitors" ? 0 : 1))
      SetEditor(s, ready ? SettingsStore.Read(s, cfg, root, GameExe, ini, plain) : s.Default);
    loading = false;
    dirty = false;
    saveState.Text = "";
  }

  void SaveSettings() {
    string ini = IniPath;
    if (ini == null || !Directory.Exists(Path.GetDirectoryName(ini))) return;
    var plain = cfg.PlainIni ? PlainIni.Read(ini) : null;
    foreach (var s in editors.Keys) if (SettingsStore.Visible(s, cfg, root)) SettingsStore.Write(s, GetEditor(s), cfg, root, GameExe, ini, plain);
    if (plain != null) PlainIni.Write(ini, plain);
    dirty = false;
    saveState.Text = L.T("Salvo. O jogo usa estas opções na próxima vez que abrir.", "Saved. The game uses these options the next time it starts.");
    saveState.Foreground = Look.B(Look.Accent);
  }

  // ------------------------------------------------------------------ dependencies

  public void ShowDeps() {
    if (Current == null || !IsCurrent(Current) || Current.NoProfile || Current.NoRuntime) return;
    OpenDepsWindow().ShowDialog(this);
  }

  public Window OpenDepsWindow() {
    var w = new StyledWindow(L.T("Dependências", "Dependencies") + "  ·  " + cfg.Game, 940, 600) { MinWidth = 760, MinHeight = 480 };
    var list = new StackPanel();
    var summary = Words("", Look.UI(), 10, Look.Text2, new Thickness(4, 0, 12, 0));
    summary.MaxWidth = 470;
    summary.VerticalAlignment = VerticalAlignment.Center;
    var recheck = new FlatButton(L.T("Verificar de novo", "Check again"), ButtonKind.Secondary, 170, 40) { Margin = new Thickness(0, 0, 10, 0) };
    var all = new FlatButton(L.T("Instalar tudo o que falta", "Install everything missing"), ButtonKind.Primary, 240, 40);
    var deps = DepsCheck.List(cfg);
    var buttons = new List<FlatButton>();
    bool checking = false;

    void Act(Action a) { w.Close(); Dispatcher.UIThread.Post(a); }

    void UpdateButtons() {
      bool busy = Busy;
      foreach (var b in buttons) b.IsEnabled = !busy && root != null;
      all.IsEnabled = !busy && deps.Any(d => d.Ok == false);
      recheck.IsEnabled = !busy && !checking;
    }

    void Render() {
      list.Children.Clear();
      buttons.Clear();
      bool first = true;
      foreach (var d in deps) {
        if (!first) list.Children.Add(StyledWindow.Line());
        first = false;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("44,*,210,96,120"), Margin = new Thickness(0, 12) };
        var dot = new StatusDot { Ok = d.Ok, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        var name = new StackPanel { Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Words(d.Name, Look.Semi, 10.5, Look.Text, new Thickness(0, 0, 0, 2)));
        name.Children.Add(Words(d.Detail, Look.UI(), 9, Look.Text3, new Thickness(0)));
        string stateText = d.Ok == null ? L.T("verificando...", "checking...") : d.Ok == true ? L.T("Instalado", "Installed") : L.T("Falta", "Missing");
        var state = new StackPanel { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        state.Children.Add(Words(stateText, Look.Semi, 10, d.Ok == null ? Look.Text3 : d.Ok == true ? Look.Accent : Look.Danger, new Thickness(0, 0, 0, 2)));
        if (!string.IsNullOrEmpty(d.Found)) state.Children.Add(Words(d.Found, Look.UI(), 9, Look.Text3, new Thickness(0)));
        string sizeText = d.Gb <= 0 ? "" : d.Gb < 1 ? $"~{Math.Max(1, (int)Math.Round(d.Gb * 1024))} MB" : $"~{d.Gb:0.#} GB";
        var size = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        size.Children.Add(Words(sizeText, Look.Semi, 10, Look.Text2, new Thickness(0, 0, 0, 2)));
        if (d.OnSystemDrive && sizeText.Length > 0) size.Children.Add(Words(L.T("no sistema", "on the system"), Look.UI(), 9, Look.Text3, new Thickness(0)));
        var button = new FlatButton(d.Only == null ? L.T("Montar", "Build") : L.T("Instalar", "Install"), ButtonKind.Blue, 112, 36) {
          HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsVisible = d.Ok == false && root != null,
        };
        var dep = d;
        button.Click += (s, e) => Act(() => InstallDep(dep));
        buttons.Add(button);
        Grid.SetColumn(name, 1); Grid.SetColumn(state, 2); Grid.SetColumn(size, 3); Grid.SetColumn(button, 4);
        foreach (var c in new Control[] { dot, name, state, size, button }) row.Children.Add(c);
        list.Children.Add(row);
      }
      // summary
      var missing = deps.Where(d => d.Ok == false).ToList();
      double onSys = missing.Where(d => d.OnSystemDrive).Sum(d => d.Gb), here = missing.Where(d => !d.OnSystemDrive).Sum(d => d.Gb);
      string folder = root ?? DefaultInstallFolder();
      string existing = folder;
      while (existing != null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
      double free = DepsCheck.FreeGb(existing ?? "/");
      bool shortOfSpace = false;
      string text;
      if (deps.Any(d => d.Ok == null)) text = L.T("Verificando...", "Checking...");
      else if (missing.Count == 0) text = L.T("Tudo instalado.", "Everything is installed.");
      else {
        text = L.T($"Faltam {missing.Count} itens: ~{here + onSys:0.#} GB (livre em {existing}: {free:0.#} GB).", $"{missing.Count} items missing: ~{here + onSys:0.#} GB (free in {existing}: {free:0.#} GB).");
        shortOfSpace = here + onSys > free;
        if (shortOfSpace) text += L.T(" Falta espaço em disco.", " Not enough disk space.");
      }
      summary.Text = text;
      summary.Foreground = Look.B(shortOfSpace ? Look.Danger : missing.Count == 0 && !deps.Any(d => d.Ok == null) ? Look.Accent : Look.Text2);
      UpdateButtons();
    }

    async void Check() {
      if (checking) return;
      checking = true;
      deps = DepsCheck.List(cfg);
      Render();
      string kit = root;
      bool installed = Installed;
      string exeName = installed ? Path.GetFileName(GameExe) : "";
      var cfg0 = cfg;
      var results = await Task.Run(() => DepsCheck.Run(cfg0, kit, installed, exeName));
      for (int i = 0; i < deps.Count && i < results.Count; ++i) { deps[i].Ok = results[i].Key; deps[i].Found = results[i].Value; }
      checking = false;
      Render();
    }

    recheck.Click += (s, e) => Check();
    all.Click += (s, e) => Act(() => Install(Current?.Disc));
    var bottom = new DockPanel { Margin = new Thickness(0, 14, 0, 0), Height = 70 };
    var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    right.Children.Add(recheck); right.Children.Add(all);
    DockPanel.SetDock(right, Dock.Right);
    bottom.Children.Add(right); bottom.Children.Add(summary);
    var stack = new StackPanel();
    stack.Children.Add(Words(L.T("O que a montagem do jogo precisa neste computador. O que faltar pode ser instalado item por item ou tudo de uma vez.",
                                 "What building the game needs on this computer. Anything missing can be installed one by one or all at once."),
                             Look.UI(), 9.5, Look.Text3, new Thickness(4, 0, 4, 14)));
    stack.Children.Add(new Card { Child = list });
    var dock = new DockPanel { Margin = new Thickness(26, 20, 26, 18) };
    DockPanel.SetDock(bottom, Dock.Bottom);
    dock.Children.Add(bottom);
    dock.Children.Add(StyledWindow.Scroller(stack));
    w.Content = dock;
    Action changed = () => { if (!Busy) Check(); else UpdateButtons(); };
    DepsChanged += changed;
    w.Closed += (s, e) => DepsChanged -= changed;
    Check();
    return w;
  }

  void InstallDep(Dep d) {
    if (Busy || root == null) return;
    if (d.Only == null) { Install(Current?.Disc); return; }
    SetLogVisible(true);
    ClearLog();
    progress.Value = 0;
    RunSetup(d.Only);
  }

  // ------------------------------------------------------------------ patches and cheats

  async void ShowPatches(PatchKind kind) {
    var e = Current;
    if (!Lib || e == null || e.Game == null || e.Recomp == null || Busy) return;
    var d = new PatchesWindow(kind, PatchChoices.GameOf(e), e.Game.Dir, e.Recomp.Name);
    await d.ShowDialog(this);
    if (d.Applied && e.Console == "ps2") RunHotpatch(e);
  }

  void RunHotpatch(GameEntry e) {
    var psi = PatchChoices.Hotpatch(e);
    if (psi == null) { AppendLog("hotpatch: " + Path.Combine(e.Recomp.Root, "tools", "ps2port", "ps2port.py") + " missing"); return; }
    SetLogVisible(true);
    stepLabel.Text = L.T("Aplicando patches e cheats (só as funções alteradas são recompiladas)...", "Applying patches and cheats (only the changed functions are recompiled)...");
    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
    p.OutputDataReceived += (s, a) => AppendLog(a.Data);
    p.ErrorDataReceived += (s, a) => AppendLog(a.Data);
    p.Exited += (s, a) => Dispatcher.UIThread.Post(() => {
      running = null;
      stepLabel.Text = p.ExitCode == 0 ? L.T("Patches e cheats prontos: valem na próxima vez que o jogo abrir.", "Patches and cheats ready: they apply the next time the game starts.")
                                       : L.T("Os patches não foram aplicados. Veja o log.", "The patches were not applied. See the log.");
      RefreshStatus();
    });
    running = p;
    runningSetup = false;
    try { p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine(); } catch (Exception ex) { running = null; AppendLog(ex.Message); }
    RefreshStatus();
  }
}

// The Patches and Cheats windows: a game's entries from the recommended repositories or any the player adds.
sealed class PatchesWindow : StyledWindow {
  readonly PatchKind kind;
  readonly PatchGame game;
  readonly string gameDir, recompName;
  readonly List<PatchSource> sources;
  readonly ChoiceBox sourceBox = new ChoiceBox { Width = 470, Height = 34 };
  readonly StackPanel list = new StackPanel();
  readonly TextBlock note, detail, status;
  readonly List<KeyValuePair<CheckBox, PatchEntry>> checks = new List<KeyValuePair<CheckBox, PatchEntry>>();
  Dictionary<string, HashSet<string>> chosen;
  List<PatchEntry> entries = new List<PatchEntry>();
  bool filling;
  int loadPass;
  public bool Applied;

  string What => kind == PatchKind.Patch ? L.T("Patches", "Patches") : L.T("Cheats", "Cheats");

  public PatchesWindow(PatchKind kind, PatchGame game, string gameDir, string recompName) : base("", 900, 640) {
    this.kind = kind; this.game = game; this.gameDir = gameDir; this.recompName = recompName;
    sources = PatchSource.For(game.Console, kind);
    Title = What + "  ·  " + game.Name;
    chosen = PatchChoices.Load(game, kind);
    string id = string.Join("  ·  ", new[] { game.Serial, game.Crc != null ? "CRC " + game.Crc : null, Consoles.Name(game.Console) }.Where(x => !string.IsNullOrEmpty(x)));
    var head = Words((kind == PatchKind.Patch
          ? L.T("Patches mudam o jogo (widescreen, 60 fps, câmera...). ", "Patches change the game (widescreen, 60 fps, camera...). ")
          : L.T("Cheats mudam valores do jogo enquanto ele roda (dinheiro, vida, munição...). ", "Cheats change values while the game runs (money, health, ammo...). ")) +
          L.T("Vêm de repositórios do GitHub mantidos pelas comunidades dos emuladores, ou de um que você adicionar.", "They come from GitHub repositories kept by the emulator communities, or one you add.") + "\n" + id,
        Look.UI(), 10, Look.Text2, new Thickness(0, 0, 0, 12));
    for (int i = 0; i < sources.Count; ++i) sourceBox.Items.Add(new ChoiceBox.Entry { Value = i, Text = sources[i].Name });
    sourceBox.Margin = new Thickness(0, 0, 10, 0);
    sourceBox.SelectedIndexChanged += (s, e) => LoadEntries();
    sourceBox.IsVisible = sources.Count > 0;
    var add = new FlatButton(L.T("Adicionar repositório...", "Add repository..."), ButtonKind.Secondary, 200, 34) { Margin = new Thickness(0, 0, 10, 0) };
    add.Click += (s, e) => AddSource();
    var open = new FlatButton(L.T("Abrir no GitHub", "Open on GitHub"), ButtonKind.Secondary, 150, 34);
    open.Click += (s, e) => { var src = Source; if (src != null) OS.OpenUrl("https://github.com/" + src.Repo + "/tree/" + src.Branch + "/" + src.Folder); };
    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    row.Children.Add(sourceBox); row.Children.Add(add); row.Children.Add(open);
    note = Words("", Look.UI(), 10, Look.Text3, new Thickness(0, 0, 0, 8));
    detail = Words("", Look.UI(), 10, Look.Text3, new Thickness(0, 8, 0, 10));
    status = Words("", Look.UI(), 10, Look.Text2, new Thickness(0), true);
    status.VerticalAlignment = VerticalAlignment.Center;
    var apply = new FlatButton(L.T("Aplicar", "Apply"), ButtonKind.Primary, 150, 40) { Margin = new Thickness(0, 0, 10, 0) };
    apply.Click += (s, e) => Apply();
    var close = new FlatButton(L.T("Fechar", "Close"), ButtonKind.Secondary, 120, 40);
    close.Click += (s, e) => Close();
    var bottom = new DockPanel();
    var bb = new StackPanel { Orientation = Orientation.Horizontal };
    bb.Children.Add(apply); bb.Children.Add(close);
    DockPanel.SetDock(bb, Dock.Right);
    bottom.Children.Add(bb); bottom.Children.Add(status);
    var top = new StackPanel();
    top.Children.Add(head); top.Children.Add(row); top.Children.Add(note);
    var under = new StackPanel();
    under.Children.Add(detail); under.Children.Add(bottom);
    var dock = new DockPanel { Margin = new Thickness(24, 18, 24, 16) };
    DockPanel.SetDock(top, Dock.Top); DockPanel.SetDock(under, Dock.Bottom);
    dock.Children.Add(top); dock.Children.Add(under);
    dock.Children.Add(new Card { Padding = new Thickness(10), Child = Scroller(list) });
    Content = dock;
    status.Text = Supported ? CountText() : L.T($"O {recompName} ainda não aplica {What.ToLowerInvariant()}: a escolha fica salva para quando aplicar.",
                                                $"{recompName} does not apply {What.ToLowerInvariant()} yet: the choice is saved for when it does.");
    if (sources.Count > 0) { sourceBox.Select(0, 0); LoadEntries(); }
    else note.Text = L.T("Nenhum repositório recomendado para este console. Adicione um.", "No recommended repository for this console. Add one.");
  }

  bool Supported => game.Console == "ps2" || (game.Console == "ps5" && kind == PatchKind.Patch);
  PatchSource Source => sources.Count == 0 ? null : sources[sourceBox.Selected?.Value ?? 0];

  string CountText() {
    int n = chosen.Values.Sum(x => x.Count);
    if (n == 0) return L.T("Nada escolhido.", "Nothing chosen.");
    if (game.Console == "ps5") return L.T($"{n} escolhido(s). Valem na próxima vez que o jogo abrir; arquivos que o recompilador não lê são ignorados.", $"{n} chosen. They apply the next time the game starts; files the recompiler does not read are ignored.");
    return L.T($"{n} escolhido(s). Aplicar recompila só as funções alteradas (segundos).", $"{n} chosen. Apply recompiles only the changed functions (seconds).");
  }

  async void LoadEntries() {
    var src = Source;
    if (src == null) return;
    int pass = ++loadPass;
    note.Text = L.T("Procurando em ", "Looking in ") + src.Repo + "...";
    list.Children.Clear(); checks.Clear();
    string text = null;
    List<PatchEntry> found;
    try { found = await Task.Run(() => PatchLibrary.Load(src, game, out text)); }
    catch (Exception ex) { if (pass == loadPass) note.Text = L.T("Não foi possível ler o repositório: ", "Could not read the repository: ") + ex.Message; return; }
    if (pass != loadPass) return;
    entries = found;
    note.Text = text;
    chosen.TryGetValue(src.Key, out var on);
    filling = true;
    foreach (var e in entries) {
      var entry = e;
      var cb = new CheckBox { Content = e.Title, IsChecked = on != null && on.Contains(e.Title), Foreground = Look.B(Look.Text), Margin = new Thickness(2, 0) };
      cb.IsCheckedChanged += (s, a) => { if (!filling) RememberChecks(); };
      cb.GotFocus += (s, a) => ShowDetail(entry);
      cb.PointerEntered += (s, a) => ShowDetail(entry);
      checks.Add(new KeyValuePair<CheckBox, PatchEntry>(cb, e));
      list.Children.Add(cb);
    }
    filling = false;
  }

  void ShowDetail(PatchEntry e) {
    var parts = new List<string>();
    if (!string.IsNullOrEmpty(e.Description)) parts.Add(e.Description);
    if (!string.IsNullOrEmpty(e.Author)) parts.Add(L.T("Autor: ", "Author: ") + e.Author);
    parts.Add(L.T($"{e.Lines.Count} linha(s) de código · ", $"{e.Lines.Count} code line(s) · ") + e.File);
    detail.Text = string.Join("\n", parts);
  }

  void RememberChecks() {
    var src = Source;
    if (src == null) return;
    chosen[src.Key] = new HashSet<string>(checks.Where(kv => kv.Key.IsChecked == true).Select(kv => kv.Value.Title));
    if (Supported) status.Text = CountText();
  }

  async void AddSource() {
    string text = await Prompt.Ask(this, L.T("Adicionar repositório", "Add repository"),
        L.T("Repositório do GitHub: dono/nome, dono/nome/pasta ou o link (github.com/...).", "GitHub repository: owner/name, owner/name/folder or the link (github.com/...)."));
    if (string.IsNullOrWhiteSpace(text)) return;
    var src = await Task.Run(() => PatchSource.Parse(text, game.Console, kind));
    if (src == null) { await Dialog.Info(this, Title, L.T("Não encontrei esse repositório.", "That repository was not found.")); return; }
    src.Remember();
    sources.Add(src);
    sourceBox.Items.Add(new ChoiceBox.Entry { Value = sources.Count - 1, Text = src.Name });
    sourceBox.IsVisible = true;
    sourceBox.Select(sources.Count - 1, 0);
  }

  async void Apply() {
    RememberChecks();
    try { PatchChoices.Apply(game, kind, gameDir, sources, chosen, Source, entries, What); }
    catch (Exception ex) { await Dialog.Info(this, Title, ex.Message); return; }
    Applied = true;
    Close();
  }
}

}
