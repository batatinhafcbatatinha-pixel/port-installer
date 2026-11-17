// The Linux/macOS launcher's start: the same command line as the Windows launcher (launcher/Program.cs):
// --root <recompiler>, --game <id>, --theme dark|light, --lang pt|en, and the test aids --shot <png> [--shot-tab N],
// --detect, --add-disc, --check-updates; plus --left (open the window at the left edge of the screen).
using System;
using System.IO;
using System.Linq;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace PortLauncher {

static class Program {
  public static string[] Args = new string[0];

  public static string Arg(string name) {
    int i = Array.IndexOf(Args, name);
    return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : null;
  }

  [STAThread]
  public static int Main(string[] args) {
    Args = args;
    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    Updater.CleanUp();
    string home = Home;

    string langArg = Arg("--lang");
    if (langArg == "pt" || langArg == "en") { L.Mode = langArg; L.Pt = langArg == "pt"; }

    // Test aids that need no window.
    string checkFile = Arg("--check-updates");
    if (checkFile != null) {
      var sb = new System.Text.StringBuilder("this: " + Updater.Describe() + " asset " + Updater.AssetName + "\n");
      foreach (var ch in new[] { UpdateChannel.Stable, UpdateChannel.Nightly }) {
        try { var r = Updater.Latest(ch); sb.Append(ch + ": " + (r == null ? "-" : r.Display + " " + r.Version + " " + r.Commit + " newer=" + Updater.IsNewer(r, ch) + " " + r.Url) + "\n"); }
        catch (Exception ex) { sb.Append(ch + ": " + ex.Message + "\n"); }
      }
      File.WriteAllText(checkFile, sb.ToString());
      return 0;
    }
    var recomp = Recomp.Find(Arg("--root") ?? home);
    string detect = Arg("--detect"), addDisc = Arg("--add-disc");
    if (addDisc != null && recomp != null) {
      var known = recomp.Detect(addDisc, out string bootName);
      string created = known == null ? recomp.CreateGenericPort(addDisc, bootName) : "profile:" + known.Id;
      File.WriteAllText(Args[Array.IndexOf(Args, "--add-disc") + 2], (created ?? "-") + "\n");
      return 0;
    }
    if (detect != null) {
      string outFile = Args[Array.IndexOf(Args, "--detect") + 2];
      if (recomp == null) { File.WriteAllText(outFile, "no recompiler\n"); return 0; }
      var detected = recomp.Detect(detect, out string boot);
      File.WriteAllText(outFile, (boot ?? "-") + " " + (detected?.Id ?? "-") + "\n");
      return 0;
    }
    // --list <out.txt>: the library as the window would show it (console, state, name), after one scan.
    string listFile = Arg("--list");
    if (listFile != null) {
      var reg = RecompRegistry.Load(Arg("--root") ?? home);
      var foundDiscs = DiscScanner.Scan(reg, GameFolders.Load(), null, default);
      var lines = Entries.Library(reg, foundDiscs, null, null).Select(e => (e.Console ?? "-") + "\t" + e.State + "\t" + e.Name + "\t" + (e.Disc ?? "-"));
      File.WriteAllLines(listFile, new[] { "system " + OS.Key + ", recompilers: " + string.Join(", ", reg.Installed.Select(kv => kv.Key + (kv.Value.Supported ? "" : " (no " + OS.Key + ")"))) }.Concat(lines));
      return 0;
    }

    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    return 0;
  }

  // Where the launcher sits, to find the recompiler it came with: the folder of the program, of the AppImage file
  // (Linux; the program itself runs from a temporary mount) or of the .app bundle (macOS: the program is in Contents/MacOS).
  public static string Home {
    get {
      string img = Environment.GetEnvironmentVariable("APPIMAGE");
      if (OS.Linux && !string.IsNullOrEmpty(img)) return Path.GetDirectoryName(img);
      return OS.AppBundle != null ? Path.GetDirectoryName(OS.AppBundle) : AppContext.BaseDirectory;
    }
  }

  public static AppBuilder BuildAvaloniaApp() =>
      AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
                .With(new X11PlatformOptions { WmClass = "port-launcher" })
                .With(new MacOSPlatformOptions { ShowInDock = true });
}

sealed class App : Application {
  public override void Initialize() {
    Styles.Add(new FluentTheme());
    Name = "Port launcher";
  }

  public override void OnFrameworkInitializationCompleted() {
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
      Look.Load();
      string themeArg = Program.Arg("--theme");
      if (themeArg != null) Look.Apply(themeArg);
      string home = Program.Home;
      // 1. A single-game kit (launcher.json next to the program), else 2. the game library of every recompiler.
      GameConfig kitConfig = File.Exists(Path.Combine(home, "launcher.json")) && Recomp.Find(home) == null ? GameConfig.Load(home) : null;
      var registry = kitConfig == null ? RecompRegistry.Load(Program.Arg("--root") ?? home) : null;
      string key = Program.Arg("--game");
      string kitRoot = kitConfig != null && File.Exists(Path.Combine(home, "setup.json")) ? home : null;
      void Open() {
        ApplyThemeVariant();
        MainWindow w;
        if (kitConfig != null) w = new MainWindow(kitConfig, kitRoot);
        else {
          var empty = GameConfig.Parse("{}");
          empty.Game = "Port launcher";
          w = new MainWindow(empty, null, registry, key);
        }
        string shot = Program.Arg("--shot");
        if (shot != null) w.ShotOnLoad(shot, int.TryParse(Program.Arg("--shot-tab"), out int t) ? t : 0, int.TryParse(Program.Arg("--shot-wait"), out int s) ? s : 8);
        string auto = Program.Arg("--install");
        if (auto != null) w.AutoInstall(auto, Program.Arg("--install-log") ?? "install-log.txt", Program.Arg("--only"));
        if (Program.Args.Contains("--left")) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Position = new PixelPoint(0, 0); }
        w.Closed += (s2, e2) => {
          if (w.Restart) { key = w.NextGame; kitRoot = w.RootForRestart ?? kitRoot; Open(); }
          else desktop.Shutdown();
        };
        desktop.MainWindow = w;
        w.Show();
      }
      Open();
    }
    base.OnFrameworkInitializationCompleted();
  }

  // Fluent's own parts (menus, text boxes, check boxes) follow the launcher's light or dark palette.
  void ApplyThemeVariant() {
    RequestedThemeVariant = Look.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
    Resources["SystemAccentColor"] = Look.Accent;
    Resources["MenuFlyoutPresenterBackground"] = Look.B(Look.Surface2);
    Resources["MenuFlyoutPresenterBorderBrush"] = Look.B(Look.Border);
    Resources["MenuFlyoutItemBackgroundPointerOver"] = Look.B(Look.Hover);
    Resources["MenuFlyoutItemForeground"] = Look.B(Look.Text);
    Resources["MenuFlyoutItemForegroundDisabled"] = Look.B(Look.DisabledText);
    Resources["MenuFlyoutSeparatorBackground"] = Look.B(Look.Border);
    Resources["MenuBarItemBackgroundPointerOver"] = Look.B(Look.Hover);
    Resources["MenuBarItemBackgroundSelected"] = Look.B(Look.Surface2);
    Resources["TextControlBackground"] = Look.B(Look.Surface2);
    Resources["TextControlBorderBrush"] = Look.B(Look.Border);
    Resources["TextControlForeground"] = Look.B(Look.Text);
    Resources["ScrollBarThumbFill"] = Look.B(Look.Border);
  }

  static WindowIcon icon;
  public static void SetIcon(Window w) {
    try {
      if (icon == null)
        using (var s = typeof(App).Assembly.GetManifestResourceStream("icon.png")) if (s != null) icon = new WindowIcon(s);
      if (icon != null) w.Icon = icon;
    } catch (Exception) { }
  }

  // The window's own content as a PNG (test aid; never the whole screen).
  public static void Snapshot(Window w, string file) {
    var size = w.ClientSize;
    double scale = w.RenderScaling;
    using (var rtb = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)(size.Width * scale)), Math.Max(1, (int)(size.Height * scale))), new Vector(96 * scale, 96 * scale))) {
      rtb.Render(w);
      rtb.Save(file);
    }
  }
}

}
