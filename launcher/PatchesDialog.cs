// The Patches and Cheats windows: a game's entries from the recommended repositories or any the player adds,
// checked on or off. Applying writes them next to the game (PS2: patches.pnach / cheats.pnach) and runs the
// recompiler's patch step (PS2Recomp: tools/ps2port hotpatch, seconds, no rebuild).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortLauncher {

sealed partial class MainForm {
  void ShowPatches(PatchKind kind) {
    var e = Current;
    if (!Lib || e == null || e.Game == null || e.Recomp == null || Busy) return;
    var game = PatchGameOf(e);
    using (var d = new PatchesDialog(kind, game, e.Game.Dir, e.Recomp.Name)) {
      d.ShowDialog(this);
      if (d.Applied && e.Console == "ps2") RunHotpatch(e);
    }
  }

  PatchGame PatchGameOf(GameEntry e) {
    var g = new PatchGame { Console = e.Console, Serial = e.Serial, Name = e.Name };
    if (e.Console == "ps5") g.TitleId = e.Serial;
    if (e.Console == "ps2" && e.Game != null) {
      // PCSX2 names its files <serial>_<CRC of the boot executable>.
      string boot = e.Game.Executables.FirstOrDefault(x => Covers.Serial(x) != null);
      if (boot != null) {
        try {
          string installed = Path.Combine(e.Game.Dir, "game_files", boot);
          byte[] elf = File.Exists(installed) ? File.ReadAllBytes(installed) : e.Disc != null ? Recomp.ReadDiscFile(e.Disc, boot) : null;
          if (elf != null) g.Crc = PatchLibrary.Ps2Crc(elf);
          g.Serial = g.Serial ?? Covers.Serial(boot);
        } catch (Exception) { }
      }
    }
    return g;
  }

  // tools/ps2port hotpatch for the selected game, its output in the log.
  void RunHotpatch(GameEntry e) {
    var r = e.Recomp;
    string script = Path.Combine(r.Root, "tools", "ps2port", "ps2port.py");
    if (!File.Exists(script)) { AppendLog("hotpatch: " + script + " missing"); return; }
    string python = Path.Combine(r.Root, ".local", "python", "python.exe");
    if (!File.Exists(python)) python = "python";
    var psi = new ProcessStartInfo(python, $"\"{script}\" hotpatch --port \"{e.Game.Dir}\"") {
      WorkingDirectory = r.Root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    // llvm-mingw from the recompiler's tools (the ZIP's compiler); with none, ps2port finds Visual Studio itself.
    string mingw = Directory.Exists(r.ToolsDir) ? Directory.GetDirectories(r.ToolsDir, "llvm-mingw-*").FirstOrDefault() : null;
    if (mingw != null) {
      psi.EnvironmentVariables["PATH"] = Path.Combine(mingw, "bin") + ";" + psi.EnvironmentVariables["PATH"];
      psi.EnvironmentVariables["CC"] = "clang";
      psi.EnvironmentVariables["CXX"] = "clang++";
    }
    SetLogVisible(true);
    stepLabel.Text = L.T("Aplicando patches e cheats (só as funções alteradas são recompiladas)...", "Applying patches and cheats (only the changed functions are recompiled)...");
    var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
    p.OutputDataReceived += (s, a) => AppendLog(a.Data);
    p.ErrorDataReceived += (s, a) => AppendLog(a.Data);
    p.Exited += (s, a) => BeginInvoke(new Action(() => {
      running = null;
      stepLabel.Text = p.ExitCode == 0 ? L.T("Patches e cheats prontos: valem na próxima vez que o jogo abrir.", "Patches and cheats ready: they apply the next time the game starts.")
                                       : L.T("Os patches não foram aplicados. Veja o log.", "The patches were not applied. See the log.");
      RefreshStatus();
    }));
    running = p;
    p.Start();
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    RefreshStatus();
  }
}

sealed class PatchesDialog : Form {
  readonly PatchKind kind;
  readonly PatchGame game;
  readonly string gameDir, recompName;
  readonly List<PatchSource> sources;
  readonly ChoiceBox sourceBox = new ChoiceBox { Width = 470, Height = 34 };
  readonly CheckedListBox list = new CheckedListBox { BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false };
  readonly Label note = new Label { AutoSize = true, MaximumSize = new Size(820, 0) };
  readonly Label detail = new Label { AutoSize = true, MaximumSize = new Size(820, 0) };
  readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(520, 0), Anchor = AnchorStyles.Left };
  readonly Dictionary<string, HashSet<string>> chosen = new Dictionary<string, HashSet<string>>();   // source key -> titles
  List<PatchEntry> entries = new List<PatchEntry>();
  bool filling;
  int loadPass;
  public bool Applied;

  string What => kind == PatchKind.Patch ? L.T("Patches", "Patches") : L.T("Cheats", "Cheats");
  string ChoiceFile => Path.Combine(Prefs.Dir, "choices", Regex.Replace(game.Console + "-" + (game.Serial ?? game.Name ?? "game"), @"[^\w.-]", "_") + "-" + kind + ".txt");

  public PatchesDialog(PatchKind kind, PatchGame game, string gameDir, string recompName) {
    this.kind = kind; this.game = game; this.gameDir = gameDir; this.recompName = recompName;
    sources = PatchSource.For(game.Console, kind);
    Text = What + "  ·  " + game.Name;
    Font = Theme.UI(10f);
    AutoScaleMode = AutoScaleMode.Dpi;
    ClientSize = new Size(900, 640);
    StartPosition = FormStartPosition.CenterParent;
    MinimizeBox = false;
    BackColor = Theme.Bg; ForeColor = Theme.Text;
    Theme.DarkTitle(this);
    LoadChoices();

    var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Theme.Bg, Padding = new Padding(24, 18, 24, 16) };
    outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
    outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    string id = string.Join("  ·  ", new[] { game.Serial, game.Crc != null ? "CRC " + game.Crc : null, Consoles.Name(game.Console) }.Where(x => !string.IsNullOrEmpty(x)));
    outer.Controls.Add(new Label {
      Text = (kind == PatchKind.Patch
          ? L.T("Patches mudam o jogo (widescreen, 60 fps, câmera...). ", "Patches change the game (widescreen, 60 fps, camera...). ")
          : L.T("Cheats mudam valores do jogo enquanto ele roda (dinheiro, vida, munição...). ", "Cheats change values while the game runs (money, health, ammo...). ")) +
             L.T("Vêm de repositórios do GitHub mantidos pelas comunidades dos emuladores, ou de um que você adicionar.", "They come from GitHub repositories kept by the emulator communities, or one you add.") +
             "\n" + id,
      AutoSize = true, MaximumSize = new Size(840, 0), ForeColor = Theme.Text2, Margin = new Padding(0, 0, 0, 12) });

    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 8) };
    for (int i = 0; i < sources.Count; ++i) sourceBox.Items.Add(new ChoiceBox.Entry { Value = i, Text = sources[i].Name });
    sourceBox.Margin = new Padding(0, 0, 10, 0);
    sourceBox.SelectedIndexChanged += (s, e) => LoadEntries();
    var add = new FlatButton(L.T("Adicionar repositório...", "Add repository..."), ButtonKind.Secondary, 200, 34) { Margin = new Padding(0, 0, 10, 0) };
    add.Click += (s, e) => AddSource();
    var open = new FlatButton(L.T("Abrir no GitHub", "Open on GitHub"), ButtonKind.Secondary, 150, 34) { Margin = Padding.Empty };
    open.Click += (s, e) => { var src = Source; if (src != null) Process.Start("https://github.com/" + src.Repo + "/tree/" + src.Branch + "/" + src.Folder); };
    sourceBox.Visible = sources.Count > 0;
    row.Controls.Add(sourceBox);
    row.Controls.Add(add);
    row.Controls.Add(open);
    outer.Controls.Add(row);

    note.ForeColor = Theme.Text3; note.Margin = new Padding(0, 0, 0, 8);
    outer.Controls.Add(note);
    var listCard = new Card { BackColor = Theme.Surface, Dock = DockStyle.Fill, Padding = new Padding(10), Margin = new Padding(0, 0, 0, 8) };
    list.BackColor = Theme.Surface; list.ForeColor = Theme.Text; list.Dock = DockStyle.Fill; list.Font = Theme.UI(10f);
    list.ItemCheck += (s, e) => { if (!filling) BeginInvoke(new Action(RememberChecks)); };
    list.SelectedIndexChanged += (s, e) => ShowDetail();
    listCard.Controls.Add(list);
    outer.Controls.Add(listCard);
    detail.ForeColor = Theme.Text3; detail.Margin = new Padding(0, 0, 0, 10);
    outer.Controls.Add(detail);

    var bottom = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, BackColor = Theme.Bg };
    bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    status.ForeColor = Theme.Text2;
    var apply = new FlatButton(L.T("Aplicar", "Apply"), ButtonKind.Primary, 150, 40) { Margin = new Padding(0, 0, 10, 0) };
    apply.Click += (s, e) => Apply();
    var close = new FlatButton(L.T("Fechar", "Close"), ButtonKind.Secondary, 120, 40) { Margin = Padding.Empty };
    close.Click += (s, e) => Close();
    bottom.Controls.Add(status, 0, 0);
    bottom.Controls.Add(apply, 1, 0);
    bottom.Controls.Add(close, 2, 0);
    outer.Controls.Add(bottom);
    Controls.Add(outer);

    status.Text = Supported ? CountText() : L.T($"O {recompName} ainda não aplica {What.ToLowerInvariant()}: a escolha fica salva para quando aplicar.",
                                                $"{recompName} does not apply {What.ToLowerInvariant()} yet: the choice is saved for when it does.");
    if (sources.Count > 0) { sourceBox.Select(0, 0); LoadEntries(); }
    else note.Text = L.T("Nenhum repositório recomendado para este console. Adicione um.", "No recommended repository for this console. Add one.");
  }

  // PS2: pnach through ps2port hotpatch. PS5: the JSON patches of recomp-patches, which ps5port applies at play.
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
    filling = true; list.Items.Clear(); filling = false;
    string text = null;
    List<PatchEntry> found;
    try { found = await Task.Run(() => PatchLibrary.Load(src, game, out text)); }
    catch (Exception ex) { if (pass == loadPass) note.Text = L.T("Não foi possível ler o repositório: ", "Could not read the repository: ") + ex.Message; return; }
    if (pass != loadPass || IsDisposed) return;
    entries = found;
    note.Text = text;
    chosen.TryGetValue(src.Key, out var on);
    filling = true;
    foreach (var e in entries) list.Items.Add(e, on != null && on.Contains(e.Title));
    filling = false;
    ShowDetail();
  }

  void ShowDetail() {
    var e = list.SelectedItem as PatchEntry;
    if (e == null) { detail.Text = ""; return; }
    var parts = new List<string>();
    if (!string.IsNullOrEmpty(e.Description)) parts.Add(e.Description);
    if (!string.IsNullOrEmpty(e.Author)) parts.Add(L.T("Autor: ", "Author: ") + e.Author);
    parts.Add(L.T($"{e.Lines.Count} linha(s) de código · ", $"{e.Lines.Count} code line(s) · ") + e.File);
    detail.Text = string.Join("\n", parts);
  }

  void RememberChecks() {
    var src = Source;
    if (src == null) return;
    chosen[src.Key] = new HashSet<string>(list.CheckedItems.OfType<PatchEntry>().Select(e => e.Title));
    if (Supported) status.Text = CountText();
  }

  void AddSource() {
    string text = Prompt.Ask(this, L.T("Adicionar repositório", "Add repository"),
        L.T("Repositório do GitHub: dono/nome, dono/nome/pasta ou o link (github.com/...).", "GitHub repository: owner/name, owner/name/folder or the link (github.com/...)."));
    if (string.IsNullOrWhiteSpace(text)) return;
    var src = PatchSource.Parse(text, game.Console, kind);
    if (src == null) { Dialog.Info(this, Text, L.T("Não encontrei esse repositório.", "That repository was not found.")); return; }
    src.Remember();
    sources.Add(src);
    sourceBox.Items.Add(new ChoiceBox.Entry { Value = sources.Count - 1, Text = src.Name });
    sourceBox.Visible = true;
    sourceBox.Select(sources.Count - 1, 0);
  }

  // ------------------------------------------------------------------ choices and the files the recompiler reads

  void LoadChoices() {
    try {
      if (!File.Exists(ChoiceFile)) return;
      foreach (string line in File.ReadAllLines(ChoiceFile)) {
        var p = line.Split('\t');
        if (p.Length != 2) continue;
        if (!chosen.TryGetValue(p[0], out var set)) chosen[p[0]] = set = new HashSet<string>();
        set.Add(p[1]);
      }
    } catch (Exception) { }
  }

  void Apply() {
    RememberChecks();
    Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile));
    File.WriteAllLines(ChoiceFile, chosen.SelectMany(kv => kv.Value.Select(t => kv.Key + "\t" + t)));
    // The chosen entries of every source, with their code lines (the current source's are loaded; others reload).
    var sb = new StringBuilder();
    sb.AppendLine("// " + What + L.T(" escolhidos no launcher para ", " chosen in the launcher for ") + game.Name + " (" + game.Serial + (game.Crc != null ? " " + game.Crc : "") + "). " +
                  L.T("O launcher reescreve este arquivo.", "The launcher rewrites this file."));
    int count = 0;
    var json = new List<KeyValuePair<string, string>>();   // PS5: file name -> the patch's JSON text
    foreach (var src in sources) {
      if (!chosen.TryGetValue(src.Key, out var on) || on.Count == 0) continue;
      List<PatchEntry> all;
      try { all = src == Source ? entries : PatchLibrary.Load(src, game, out _); } catch (Exception) { continue; }
      foreach (var e in all.Where(x => on.Contains(x.Title))) {
        sb.AppendLine();
        sb.AppendLine("[" + e.Title + "]");
        sb.AppendLine("// " + src.Repo + "/" + e.File);
        foreach (string l in e.Lines) sb.AppendLine(l);
        ++count;
        if (game.Console == "ps5" && e.Supported && e.File.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
          json.Add(new KeyValuePair<string, string>(Regex.Replace(src.Repo, @"[^\w.-]", "_") + "-" + Path.GetFileName(e.File), string.Join("\n", e.Lines)));
      }
    }
    string file = Path.Combine(gameDir, game.Console == "ps2" ? (kind == PatchKind.Patch ? "patches.pnach" : "cheats.pnach")
                                                             : (kind == PatchKind.Patch ? "patches-chosen.txt" : "cheats-chosen.txt"));
    try {
      if (count == 0) { if (File.Exists(file)) File.Delete(file); }
      else File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
      if (game.Console == "ps5" && kind == PatchKind.Patch) {
        // <game>/patches holds exactly the chosen patches: tools/ps5port applies them at play and undoes the rest.
        string dir = Path.Combine(gameDir, "patches");
        if (Directory.Exists(dir)) foreach (string old in Directory.GetFiles(dir, "*.json")) File.Delete(old);
        if (json.Count > 0) Directory.CreateDirectory(dir);
        foreach (var kv in json) File.WriteAllText(Path.Combine(dir, kv.Key), kv.Value, new UTF8Encoding(false));
      }
    } catch (Exception ex) { Dialog.Info(this, Text, ex.Message); return; }
    Applied = true;
    Close();
  }
}

// A one-line text question in the launcher's style.
sealed class Prompt : Form {
  readonly TextBox box = new TextBox { BorderStyle = BorderStyle.None, Width = 520 };
  string result;

  public static string Ask(IWin32Window owner, string title, string text) {
    using (var p = new Prompt(title, text)) { p.ShowDialog(owner); return p.result; }
  }

  Prompt(string title, string text) {
    Text = title;
    Font = Theme.UI(10f);
    AutoScaleMode = AutoScaleMode.Dpi;
    ClientSize = new Size(600, 190);
    StartPosition = FormStartPosition.CenterParent;
    MinimizeBox = MaximizeBox = false;
    FormBorderStyle = FormBorderStyle.FixedDialog;
    BackColor = Theme.Bg; ForeColor = Theme.Text;
    Theme.DarkTitle(this);
    var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(24, 18, 24, 12), BackColor = Theme.Bg };
    stack.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(540, 0), ForeColor = Theme.Text2, Margin = new Padding(0, 0, 0, 10) });
    var field = new Card { BackColor = Theme.Surface2, Height = 36, Width = 540, Padding = new Padding(10, 8, 10, 6), Margin = new Padding(0, 0, 0, 14) };
    box.BackColor = Theme.Surface2; box.ForeColor = Theme.Text; box.Dock = DockStyle.Fill;
    field.Controls.Add(box);
    stack.Controls.Add(field);
    var row = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Bg, Margin = Padding.Empty };
    var ok = new FlatButton("OK", ButtonKind.Primary, 120, 36) { Margin = new Padding(0, 0, 10, 0) };
    ok.Click += (s, e) => { result = box.Text; Close(); };
    var cancel = new FlatButton(L.T("Cancelar", "Cancel"), ButtonKind.Secondary, 120, 36) { Margin = Padding.Empty };
    cancel.Click += (s, e) => Close();
    row.Controls.Add(ok);
    row.Controls.Add(cancel);
    stack.Controls.Add(row);
    Controls.Add(stack);
    AcceptButton = null;
    box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { result = box.Text; Close(); } };
  }
}

}
