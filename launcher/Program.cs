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
    Updater.CleanUp();
    // Test aid: --shot <file.png> [tab index] draws the window into a PNG and exits (no clicks needed);
    // --theme dark|light overrides the saved theme for that run; --lang pt|en the language.
    string themeArg = Arg(args, "--theme");
    if (themeArg != null) Theme.Apply(themeArg);
    // --lang pt|en shows that language for this run only (the saved choice stays).
    string langArg = Arg(args, "--lang");
    if (langArg == "pt" || langArg == "en") { L.Mode = langArg; L.Pt = langArg == "pt"; }
    string shotFile = Arg(args, "--shot");
    int shotTab = int.TryParse(Arg(args, "--shot-tab"), out int t) ? t : 0;

    // Test aid: --check-updates <out.txt> writes what each update channel offers.
    string checkFile = Arg(args, "--check-updates");
    if (checkFile != null) {
      var sb = new System.Text.StringBuilder("this: " + Updater.Describe() + "\n");
      foreach (var ch in new[] { UpdateChannel.Stable, UpdateChannel.Nightly }) {
        try { var r = Updater.Latest(ch); sb.Append(ch + ": " + (r == null ? "-" : r.Display + " " + r.Version + " " + r.Commit + " newer=" + Updater.IsNewer(r, ch) + " " + r.Url) + "\n"); }
        catch (Exception ex) { sb.Append(ch + ": " + ex.Message + "\n"); }
      }
      File.WriteAllText(checkFile, sb.ToString());
      return;
    }

    // 1. A single-game kit: launcher.json next to the exe or built into it.
    var config = File.Exists(Path.Combine(dir, "launcher.json")) && Recomp.Find(dir) == null ? GameConfig.Load(dir) : null;
    if (config == null && Recomp.Find(Arg(args, "--root") ?? dir) == null) config = GameConfig.Load(null);
    if (config != null) { RunKit(config, dir, shotFile, shotTab); return; }

    // 2. A recompiler repository (--root, the exe's folder or a parent), else 3. pick and download one.
    var recomp = Recomp.Find(Arg(args, "--root") ?? dir);
    // Test aid: --detect <iso or folder> <out.txt> writes the boot executable and the game it selects.
    string detect = Arg(args, "--detect");
    // Test aid: --add-disc <iso or folder> <out.txt> creates the generic port of a disc with no profile.
    string addDisc = Arg(args, "--add-disc");
    if (addDisc != null && recomp != null) {
      var known = recomp.Detect(addDisc, out string bootName);
      string created = known == null ? recomp.CreateGenericPort(addDisc, bootName) : "profile:" + known.Id;
      File.WriteAllText(args[Array.IndexOf(args, "--add-disc") + 2], (created ?? "-") + "\n");
      return;
    }
    if (detect != null && recomp == null) { File.WriteAllText(args[Array.IndexOf(args, "--detect") + 2], "no recompiler\n"); return; }
    if (detect != null) {
      var detected = recomp.Detect(detect, out string boot);
      File.WriteAllText(args[Array.IndexOf(args, "--detect") + 2], (boot ?? "-") + " " + (detected?.Id ?? "-") + "\n");
      return;
    }
    // The game library: every recompiler on this PC (this one, when the launcher sits in one, plus the ones
    // downloaded or located before). It starts empty until the player adds the folders with their discs.
    var registry = RecompRegistry.Load(Arg(args, "--root") ?? dir);
    string key = Arg(args, "--game");
    while (true) {
      var empty = GameConfig.Parse("{}");
      empty.Game = "Port launcher";
      var form = new MainForm(empty, null, 0, registry, key);
      if (shotFile != null) form.ShotOnLoad(shotFile, shotTab);
      Application.Run(form);
      if (!form.Restart) break;
      key = form.NextGame;
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

}
