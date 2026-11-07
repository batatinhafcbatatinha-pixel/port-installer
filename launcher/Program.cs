using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortLauncher {

static class Program {
  static string Arg(string[] args, string name) {
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
  }

  [STAThread]
  static void Main(string[] args) {
    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    string dir = AppDomain.CurrentDomain.BaseDirectory;
    Theme.Load();
    // Test aid: --shot <file.png> [tab index] draws the window into a PNG and exits (no clicks needed);
    // --theme dark|light overrides the saved theme for that run.
    string themeArg = Arg(args, "--theme");
    if (themeArg != null) Theme.Apply(themeArg);
    string shotFile = Arg(args, "--shot");
    int shotTab = int.TryParse(Arg(args, "--shot-tab"), out int t) ? t : 0;

    // 1. A single-game kit: launcher.json next to the exe or built into it.
    var config = File.Exists(Path.Combine(dir, "launcher.json")) && Recomp.Find(dir) == null ? GameConfig.Load(dir) : null;
    if (config == null && Recomp.Find(Arg(args, "--root") ?? dir) == null) config = GameConfig.Load(null);
    if (config != null) { RunKit(config, dir, shotFile, shotTab); return; }

    // 2. A recompiler repository (--root, the exe's folder or a parent), else 3. pick and download one.
    var recomp = Recomp.Find(Arg(args, "--root") ?? dir);
    // Test aid: --detect <iso or folder> <out.txt> writes the boot executable and the game it selects.
    string detect = Arg(args, "--detect");
    if (detect != null && recomp != null) {
      string boot = Recomp.BootExecutable(detect);
      File.WriteAllText(args[Array.IndexOf(args, "--detect") + 2], (boot ?? "-") + " " + (recomp.GameForDisc(boot)?.Id ?? "-") + "\n");
      return;
    }
    if (recomp == null) {
      string root = WelcomeForm.Pick();
      if (root == null) return;
      recomp = Recomp.Load(root);
      if (recomp == null) return;
    }
    if (recomp.Games.Count == 0) {
      Dialog.Info(null, recomp.Name, L.T("Nenhum jogo encontrado em ", "No games found in ") + recomp.Root);
      return;
    }
    string gameId = Arg(args, "--game") ?? Settings.LastGame(recomp.Root);
    string source = null;
    int page = 0;
    while (true) {
      var game = recomp.Games.FirstOrDefault(g => g.Id == gameId) ?? recomp.Games[0];
      Settings.SaveLastGame(recomp.Root, game.Id);
      var form = new MainForm(recomp.ConfigFor(game), game.Dir, page, recomp, game, source);
      if (shotFile != null) form.ShotOnLoad(shotFile, shotTab);
      Application.Run(form);
      if (!form.Restart) break;
      gameId = form.NextGame ?? game.Id;
      source = form.NextSource;
      page = form.NextGame != null ? 0 : form.PageForRestart;
    }
  }

  static void RunKit(GameConfig config, string dir, string shotFile, int shotTab) {
    string kit = MainForm.IsKit(dir) ? dir : null;
    int page = 0;
    while (true) {
      var form = new MainForm(config, kit, page);
      if (shotFile != null) form.ShotOnLoad(shotFile, shotTab);
      Application.Run(form);
      if (!form.Restart) break;
      kit = form.RootForRestart;   // a kit downloaded in this session stays
      page = form.PageForRestart;
    }
  }
}

// The last game opened in each recompiler folder (%APPDATA%\port-launcher\launcher.ini).
static class Settings {
  static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "port-launcher", "launcher.ini");
  static string Key(string root) => "game_" + Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(root).TrimEnd('\\')));
  public static string LastGame(string root) {
    try { string v = Ini.GetText(File, "recomp", Key(root)); return v.Length > 0 ? v : null; } catch (Exception) { return null; }
  }
  public static void SaveLastGame(string root, string id) {
    try { Directory.CreateDirectory(Path.GetDirectoryName(File)); Ini.PutText(File, "recomp", Key(root), id); } catch (Exception) { }
  }
}

// The launcher on its own (downloaded from the port-installer releases): choose a recompiler and a folder;
// its source is downloaded there (code only) and the launcher opens on its games.
sealed class WelcomeForm : Form {
  string result;
  readonly List<KnownRecomp> known = KnownRecomp.List();
  readonly ChoiceBox recompBox = new ChoiceBox { Width = 520, Height = 34 };
  readonly TextBox folderBox = new TextBox { BorderStyle = BorderStyle.None, Width = 390 };
  readonly Label info = new Label { AutoSize = true, MaximumSize = new Size(640, 0) };
  readonly TextBox log = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, Height = 120, Width = 640, ScrollBars = ScrollBars.Vertical };
  FlatButton go;

  public static string Pick() {
    using (var f = new WelcomeForm()) {
      Application.Run(f);
      return f.result;
    }
  }

  WelcomeForm() {
    Text = "Port launcher";
    Font = Theme.UI(10f);
    AutoScaleMode = AutoScaleMode.Dpi;
    ClientSize = new Size(720, 520);
    StartPosition = FormStartPosition.CenterScreen;
    BackColor = Theme.Bg;
    ForeColor = Theme.Text;
    Theme.DarkTitle(this);
    var stack = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(36, 28, 36, 20), BackColor = Theme.Bg };
    stack.Controls.Add(new Label { Text = L.T("JOGAR COM UM RECOMPILADOR", "PLAY WITH A RECOMPILER"), Font = Theme.Semi(19f), AutoSize = true, ForeColor = Theme.Text });
    stack.Controls.Add(new Label {
      Text = L.T("Escolha o recompilador e a pasta. O código dele é baixado do GitHub (nada de nenhum jogo); depois você escolhe a ISO do seu jogo e o launcher monta e abre o jogo.",
                 "Choose the recompiler and a folder. Its source code is downloaded from GitHub (nothing from any game); then you choose your game's ISO and the launcher builds and starts it."),
      AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Theme.Text2, Margin = new Padding(0, 8, 0, 22) });
    stack.Controls.Add(new Label { Text = L.T("Recompilador", "Recompiler"), Font = Theme.Semi(9.5f), ForeColor = Theme.Text3, AutoSize = true });
    for (int i = 0; i < known.Count; ++i) recompBox.Items.Add(new ChoiceBox.Entry { Value = i, Text = known[i].Name });
    recompBox.Select(0, 0);
    recompBox.SelectedIndexChanged += (s, e) => Describe();
    recompBox.Margin = new Padding(0, 4, 0, 8);
    stack.Controls.Add(recompBox);
    info.ForeColor = Theme.Text3;
    info.Margin = new Padding(0, 0, 0, 18);
    stack.Controls.Add(info);
    stack.Controls.Add(new Label { Text = L.T("Pasta", "Folder"), Font = Theme.Semi(9.5f), ForeColor = Theme.Text3, AutoSize = true });
    var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0, 4, 0, 18) };
    var field = new Card { BackColor = Theme.Surface2, Height = 38, Width = 410, Padding = new Padding(12, 9, 12, 6), Margin = new Padding(0, 0, 10, 0) };
    folderBox.BackColor = Theme.Surface2; folderBox.ForeColor = Theme.Text; folderBox.Dock = DockStyle.Fill;
    field.Controls.Add(folderBox);
    var browse = new FlatButton(L.T("Escolher...", "Browse..."), ButtonKind.Secondary, 120, 38) { Margin = Padding.Empty };
    browse.Click += (s, e) => {
      using (var d = new FolderBrowserDialog()) if (d.ShowDialog(this) == DialogResult.OK) folderBox.Text = Path.Combine(d.SelectedPath, Current?.Folder ?? "recomp");
    };
    row.Controls.Add(field);
    row.Controls.Add(browse);
    stack.Controls.Add(row);
    go = new FlatButton(L.T("BAIXAR E ABRIR", "DOWNLOAD AND OPEN"), ButtonKind.Primary, 240, 46) { Font = Theme.Semi(11.5f), Margin = new Padding(0, 0, 0, 14) };
    go.Click += async (s, e) => await Download();
    stack.Controls.Add(go);
    log.BackColor = Theme.Bg; log.ForeColor = Theme.Text3; log.Font = new Font("Consolas", 9f);
    stack.Controls.Add(log);
    Controls.Add(stack);
    Describe();
  }

  KnownRecomp Current => known.Count == 0 ? null : known[recompBox.Selected?.Value ?? 0];

  void Describe() {
    var r = Current;
    if (r == null) { info.Text = "recomps.json missing"; go.Enabled = false; return; }
    info.Text = (L.Pt ? r.Pt : r.En) + "   (github.com/" + r.Repo + ")";
    var best = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).OrderByDescending(d => d.AvailableFreeSpace).FirstOrDefault();
    folderBox.Text = Path.Combine(best?.RootDirectory.FullName ?? "C:\\", "Games", r.Folder);
  }

  async Task Download() {
    var r = Current;
    string target = folderBox.Text.Trim();
    if (r == null || target.Length == 0 || !Path.IsPathRooted(target)) return;
    go.Enabled = false;
    try {
      string root = File.Exists(Path.Combine(target, "launcher.json"))
          ? target
          : await Task.Run(() => Recomp.Download(r.Repo, r.Branch, target, line => BeginInvoke(new Action(() => log.AppendText(line + Environment.NewLine)))));
      var recomp = Recomp.Load(root);
      if (recomp == null) throw new IOException(L.T("A pasta baixada não tem launcher.json.", "The downloaded folder has no launcher.json."));
      recomp.EnsureInstallerFiles();
      // The launcher keeps a copy of itself in the recompiler folder, where Launcher.cmd finds it.
      string self = Application.ExecutablePath, copy = Path.Combine(root, ".local", "launcher", "Launcher.exe");
      Directory.CreateDirectory(Path.GetDirectoryName(copy));
      if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(copy), StringComparison.OrdinalIgnoreCase)) File.Copy(self, copy, true);
      result = root;
      Close();
    } catch (Exception ex) {
      log.AppendText(ex.Message + Environment.NewLine);
      go.Enabled = true;
    }
  }
}

}
