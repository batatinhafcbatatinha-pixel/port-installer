// Unit tests of the launcher's shared logic, run on Windows, Linux and macOS (CI runs them on all three).
// Each test is a method; a failed check throws. Exit code = number of failed tests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PortLauncher {

static class Tests {
  static int failed, passed;

  static void Check(bool ok, string what) { if (!ok) throw new Exception("check failed: " + what); }
  static void Equal<T>(T want, T got, string what) { if (!Equals(want, got)) throw new Exception($"{what}: want <{want}> got <{got}>"); }

  static string Temp() {
    string d = Path.Combine(Path.GetTempPath(), "pl-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    Directory.CreateDirectory(d);
    return d;
  }

  public static int Main() {
    // Preferences and caches of the tests stay in a temporary folder.
    Environment.SetEnvironmentVariable("PORT_LAUNCHER_HOME", Temp());
    foreach (var m in typeof(Tests).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test")).OrderBy(m => m.Name)) {
      try { m.Invoke(null, null); ++passed; Console.WriteLine("ok   " + m.Name); }
      catch (TargetInvocationException e) { ++failed; Console.WriteLine("FAIL " + m.Name + ": " + e.InnerException?.Message); }
    }
    Console.WriteLine($"{passed} passed, {failed} failed ({OS.Name})");
    return failed;
  }

  // ------------------------------------------------------------------ JSON

  static void TestJsonShapes() {
    var d = Json.Object("﻿{\"a\": 1, \"b\": [true, null, \"x\\u00e9\\n\"], \"c\": {\"d\": 12345678901, \"e\": 1.25, \"f\": -2e3}}");
    Equal(1, d["a"], "int");
    var b = (object[])d["b"];
    Equal(3, b.Length, "array length");
    Equal(true, b[0], "bool");
    Check(b[1] == null, "null");
    Equal("xé\n", b[2], "escapes");
    var c = (IDictionary<string, object>)d["c"];
    Equal(12345678901L, c["d"], "long");
    Equal(1.25m, c["e"], "decimal");
    Equal(-2000.0, c["f"], "exponent");
  }

  static void TestJsonErrors() {
    foreach (string bad in new[] { "{", "[1,]x", "{\"a\" 1}", "\"open", "tru" }) {
      bool threw = false;
      try { Json.Parse(bad); } catch (FormatException) { threw = true; }
      Check(threw, "rejects " + bad);
    }
  }

  static void TestJsonWriteRoundTrip() {
    var src = new Dictionary<string, object> { ["name"] = "A <b> & 'c'", ["n"] = 3, ["list"] = new object[] { 1, "x", false }, ["inner"] = new Dictionary<string, object> { ["k"] = 2.5m } };
    string text = Json.Write(src);
    Check(!text.Contains("<") && !text.Contains("&"), "html characters escaped like JavaScriptSerializer");
    var back = Json.Object(text);
    Equal("A <b> & 'c'", back["name"], "string");
    Equal(3, back["n"], "number");
    Equal(2.5m, ((IDictionary<string, object>)back["inner"])["k"], "nested");
  }

  // ------------------------------------------------------------------ per-system configuration

  static void TestForThisSystemMerges() {
    var d = Json.Object("{\"exe\": \"a.exe\", \"play\": \"Play.cmd\", \"" + OS.Key + "\": {\"exe\": \"a-native\"}, \"other\": {\"exe\": \"no\"}}");
    var m = OS.ForThisSystem(d);
    Equal("a-native", m["exe"], "this system's block wins");
    Equal("Play.cmd", m["play"], "other keys stay");
    Equal("a.exe", d["exe"], "the original is not changed");
  }

  static void TestSupports() {
    var plain = Json.Object("{\"games_dir\": \"games\"}");
    Equal(OS.Windows, OS.Supports(plain), "a recompiler without a block runs on Windows only");
    Equal(true, OS.Supports(Json.Object("{\"" + OS.Key + "\": {}}")), "a block says it runs here");
    Equal(false, OS.Supports(Json.Object("{\"" + OS.Key + "\": false}")), "false says it does not");
    var cmdGame = Json.Object("{\"steps\": [{\"run\": [\"cmd\", \"/c\", \"build.cmd\"]}]}");
    var pyGame = Json.Object("{\"steps\": [{\"run\": [\"{python}\", \"../../tools/ps2port/ps2port.py\", \"prepare\"]}]}");
    Equal(OS.Windows, OS.Supports(cmdGame, false), "a game built by Windows scripts");
    Equal(true, OS.Supports(pyGame, false), "a game built by Python");
    Check(OS.WindowsOnlySteps(cmdGame) && !OS.WindowsOnlySteps(pyGame), "WindowsOnlySteps");
    Check(OS.WindowsOnlySteps(Json.Object("{\"steps\": [{\"run\": [\"tools/build.ps1\"]}]}")), "a .ps1 step");
  }

  static void TestScriptNames() {
    if (OS.Windows) {
      Equal("Play.cmd", OS.ScriptName("Play.cmd"), "Windows keeps .cmd");
      Equal("tools/setup.ps1", OS.ScriptName("tools/setup.ps1"), "Windows keeps .ps1");
    } else {
      Equal("Play-Game.sh", OS.ScriptName("Play-Game.cmd"), ".cmd -> .sh");
      Equal("tools/port-installer/setup/setup.sh", OS.ScriptName("tools/port-installer/setup/setup.ps1"), ".ps1 -> .sh");
      Equal("run.bin", OS.ScriptName("run.bin"), "other names stay");
    }
    Check(OS.ScriptName(null) == null, "null");
  }

  static void TestScriptStart() {
    var psi = OS.ScriptStart(Path.Combine("dir", OS.Windows ? "Play.cmd" : "Play.sh"), "", "dir");
    Check(psi.RedirectStandardOutput && psi.RedirectStandardError && !psi.UseShellExecute, "output is read by the launcher");
    if (OS.Windows) Check(psi.FileName.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase), "cmd runs .cmd");
    else { Equal("/bin/bash", psi.FileName, "bash runs .sh"); Check(psi.RedirectStandardInput, "no keyboard for questions"); }
  }

  // ------------------------------------------------------------------ ini files

  static void TestIniFile() {
    string f = Path.Combine(Temp(), "x.ini");
    IniFile.Write(f, "opcoes", "modo", "1");
    IniFile.Write(f, "opcoes", "hz", "60");
    IniFile.Write(f, "outra", "a", "b");
    IniFile.Write(f, "opcoes", "modo", "2");
    Equal("2", IniFile.Read(f, "opcoes", "modo"), "replaced");
    Equal("60", IniFile.Read(f, "OPCOES", "HZ"), "case-insensitive like Windows");
    Equal("b", IniFile.Read(f, "outra", "a"), "second section");
    Check(IniFile.Read(f, "opcoes", "none") == null, "missing key");
    var lines = File.ReadAllLines(f).Where(l => l.Length > 0).ToArray();
    Equal("[opcoes]|modo=2|hz=60|[outra]|a=b", string.Join("|", lines), "layout kept");
    IniFile.Write(f, "opcoes", "hz", null);
    Check(IniFile.Read(f, "opcoes", "hz") == null, "removed");
    if (!OS.Windows) {
      Ini.Put(f, "n", "v", -7);
      Equal(-7, Ini.Get(f, "n", "v", 0), "Ini on Linux/macOS");
      Equal(5, Ini.Get(f, "n", "missing", 5), "default");
      Ini.PutText(f, "n", "t", "word");
      Equal("word", Ini.GetText(f, "n", "t"), "text");
    }
  }

  static void TestPlainIniKeepsUnknownLines() {
    string f = Path.Combine(Temp(), "pc-settings.ini");
    File.WriteAllText(f, "# comment\nfps=60\nother=1\n");
    var d = PlainIni.Read(f);
    d["fps"] = "120"; d["new"] = "x";
    PlainIni.Write(f, d);
    Equal("# comment|fps=120|other=1|new=x", string.Join("|", File.ReadAllLines(f)), "plain ini");
  }

  // ------------------------------------------------------------------ displays (Linux)

  static void TestXrandr() {
#if !NETFRAMEWORK
    string text = "Screen 0: minimum 320 x 200, current 2560 x 1080\n" +
                  "XWAYLAND0 connected primary 2560x1080+0+0 (normal left inverted right x axis y axis) 0mm x 0mm\n" +
                  "   2560x1080     74.99*+  59.98    50.00  \n   1920x1080     60.00  \n" +
                  "HDMI-1 disconnected (normal left inverted right x axis y axis)\n" +
                  "DP-1 connected 1920x1080+2560+0\n   1920x1080    144.00*  60.00\n";
    var rates = Displays.ParseXrandr(text, null, out int cur);
    Equal("75,60,50", string.Join(",", rates), "first connected output, its current resolution");
    Equal(75, cur, "current rate");
    rates = Displays.ParseXrandr(text, "DP-1", out cur);
    Equal("144,60", string.Join(",", rates), "named output");
    Equal(144, cur, "its current rate");
#endif
  }

  // ------------------------------------------------------------------ recompilers and the library

  static string FakeRecomp() {
    string root = Temp();
    File.WriteAllText(Path.Combine(root, "launcher.json"),
        "{\"recomp\": \"PS2Recomp\", \"repo\": \"batatinhafcbatatinha-pixel/PS2Recomp\", \"games_dir\": \"games\", \"exe\": \"build/r.exe\"," +
        " \"linux\": {\"exe\": \"build/r\"}, \"macos\": {\"exe\": \"build/r\"}}");
    foreach (var g in new[] { new[] { "py", "{\"game\": \"Py Game\", \"disc\": \"PS2 (SLUS-00001)\", \"play\": \"Play.cmd\", \"steps\": [{\"run\": [\"{python}\", \"x.py\"]}]}" },
                              new[] { "cmd", "{\"game\": \"Cmd Game\", \"disc\": \"PS2 (SLUS-00002)\", \"play\": \"Play-Cmd.cmd\", \"steps\": [{\"run\": [\"cmd\", \"/c\", \"b.cmd\"]}]}" } }) {
      string dir = Path.Combine(root, "games", g[0]);
      Directory.CreateDirectory(dir);
      File.WriteAllText(Path.Combine(dir, "setup.json"), g[1]);
      File.WriteAllText(Path.Combine(dir, "installer.json"), "{\"required_files\": {\"SLUS_000." + (g[0] == "py" ? "01" : "02") + "\": \"00\"}}");
    }
    return root;
  }

  static void TestRecompPerSystem() {
    var r = Recomp.Load(FakeRecomp());
    Check(r != null && r.Supported, "a recompiler with this system's block");
    var py = r.Games.First(g => g.Id == "py");
    var cmd = r.Games.First(g => g.Id == "cmd");
    Check(py.Supported, "a Python-built game runs everywhere its recompiler does");
    Equal(OS.Windows, cmd.Supported, "a cmd-built game only on Windows");
    var c = r.ConfigFor(py);
    Equal(OS.Windows ? "build/r.exe" : "build/r", c.Exe, "the executable of this system");
    Equal(OS.Windows ? "Play.cmd" : "Play.sh", c.Play, "the start script of this system");
    Check(r.Setup.EndsWith(OS.Windows ? "setup.ps1" : "setup.sh"), "the setup script of this system");
  }

  static void TestLibraryStates() {
    string home = Environment.GetEnvironmentVariable("PORT_LAUNCHER_HOME");
    string root = FakeRecomp();
    File.WriteAllText(Path.Combine(home, "recomps.txt"), "ps2recomp\t" + root + "\n");
    var reg = RecompRegistry.Load(null);
    Check(reg.Installed.ContainsKey("ps2recomp"), "registry loads the saved recompiler");
    var rc = reg.Installed["ps2recomp"];
    var found = rc.Games.Select(g => new FoundDisc { Path = "/discs/" + g.Id + ".iso", Console = "ps2", Recomp = rc, Game = g, Label = g.Executables.First() }).ToList();
    var entries = Entries.Library(reg, found, null, null);
    Equal(EntryState.HasDisc, entries.First(e => e.Game.Id == "py").State, "a game that builds here");
    Equal(OS.Windows ? EntryState.HasDisc : EntryState.NoRuntime, entries.First(e => e.Game.Id == "cmd").State, "a Windows-only game");
    var known = reg.Known.First(k => k.Id == "anyps5");
    Equal(OS.Windows, known.Supported, "recomps.json platforms");
  }

  static void TestSetupCommandLine() {
    var r = Recomp.Load(FakeRecomp());
    var c = r.ConfigFor(r.Games.First(g => g.Id == "py"));
    string game = r.Games.First(g => g.Id == "py").Dir;
    var psi = Runner.Setup(r, c, game, "game", "/discs/a b.iso", null);
    if (OS.Windows) {
      Check(psi.FileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase), "powershell");
      Check(psi.Arguments.Contains("-Only game") && psi.Arguments.Contains("-GameDir"), "ps1 options");
    } else {
      Equal("/bin/bash", psi.FileName, "bash");
      Check(psi.Arguments.Contains("setup.sh") && psi.Arguments.Contains("--only game") && psi.Arguments.Contains("--game-dir") &&
            psi.Arguments.Contains("--source \"/discs/a b.iso\""), "sh options: " + psi.Arguments);
      Equal("1", psi.Environment["PORT_LAUNCHER"], "the script knows the launcher runs it");
    }
  }

  static void TestPreferencePaths() {
    string saved = Environment.GetEnvironmentVariable("PORT_LAUNCHER_HOME");
    try {
      Environment.SetEnvironmentVariable("PORT_LAUNCHER_HOME", null);
      if (OS.Linux) {
        string cfg = Temp(), cache = Temp();
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", cfg);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cache);
        Equal(Path.Combine(cfg, "port-launcher"), Prefs.Dir, "XDG_CONFIG_HOME");
        Equal(Path.Combine(cache, "port-launcher"), Prefs.Cache, "XDG_CACHE_HOME");
        File.WriteAllText(Path.Combine(cfg, "user-dirs.dirs"), "XDG_DESKTOP_DIR=\"$HOME/Área de Trabalho\"\n");
        Check(OS.DesktopDir.EndsWith("Área de Trabalho"), "localised desktop folder");
      } else if (OS.Mac) {
        Check(Prefs.Dir.EndsWith(Path.Combine("Library", "Application Support", "port-launcher")), "macOS preferences");
        Check(Prefs.Cache.EndsWith(Path.Combine("Library", "Caches", "port-launcher")), "macOS caches");
      } else {
        Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "port-launcher"), Prefs.Dir, "Windows %APPDATA%");
      }
    } finally {
      Environment.SetEnvironmentVariable("PORT_LAUNCHER_HOME", saved);
      Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", null);
      Environment.SetEnvironmentVariable("XDG_CACHE_HOME", null);
    }
  }

  static void TestUpdaterAsset() {
    string a = Updater.AssetName;
    if (OS.Windows) Equal("Launcher.exe", a, "Windows asset");
    else if (OS.Mac) Equal("Launcher-macos.zip", a, "macOS asset");
    else Check(a.StartsWith("Launcher-linux-") && (a.EndsWith(".tar.gz") || a.EndsWith(".AppImage")), "Linux asset " + a);
  }

  static void TestGameFoldersAdd() {
    string a = Temp();
    int n = GameFolders.Add(new[] { a, a + Path.DirectorySeparatorChar });
    Equal(1, n, "the same folder once");
    Check(GameFolders.Load().Contains(a), "saved without the trailing separator");
  }

  // ------------------------------------------------------------------ game files (GameInstall)

  static string Sha(byte[] b) { using (var h = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(h.ComputeHash(b)).Replace("-", "").ToLowerInvariant(); }

  static void TestGameInstallFromFolder() {
    string src = Temp(), root = Temp();
    byte[] boot = System.Text.Encoding.ASCII.GetBytes("BOOT2 = cdrom0:\\SLUS_000.01;1\n"), elf = new byte[5 << 20];
    new Random(1).NextBytes(elf);
    File.WriteAllBytes(Path.Combine(src, "SYSTEM.CNF"), boot);
    File.WriteAllBytes(Path.Combine(src, "SLUS_000.01"), elf);
    Directory.CreateDirectory(Path.Combine(src, "DATA"));
    File.WriteAllText(Path.Combine(src, "DATA", "a.txt"), "hello");
    string json = Path.Combine(root, "installer.json");
    File.WriteAllText(json, "{\"schema\": 1, \"name\": \"T\", \"filesystem\": \"iso9660\", \"destination\": \"game_files\", " +
                            "\"required_files\": {\"SLUS_000.01\": \"" + Sha(elf) + "\"}, \"expected_files\": {\"SYSTEM.CNF\": " + boot.Length + "}, \"required_paths\": [\"DATA\"]}");
    Check(Runner.NeedsGameFiles(root), "needs files before the copy");
    long last = 0, total = 0;
    var r = GameInstall.Install(src, root, json, _ => { }, (d, t) => { last = d; total = t; }, System.Threading.CancellationToken.None);
    Equal(total, last, "progress reaches the total");
    Check(!r.CanDeleteSource, "a folder is never offered for deletion");
    Check(GameInstall.GameOk(root, json) && !Runner.NeedsGameFiles(root), "files in place");
    Equal("hello", File.ReadAllText(Path.Combine(root, "game_files", "DATA", "a.txt")), "copied file");
    string tsv = File.ReadAllText(Path.Combine(root, "game_files", ".disc", "layout.tsv"));
    Check(tsv.StartsWith("EXTRACTED_DISC_V1\t") && tsv.Contains("\tSLUS_000.01\n"), "disc index written");
    // A second run compares instead of copying; a different executable is refused and the copy is kept.
    GameInstall.Install(src, root, json, _ => { }, (d, t) => { }, System.Threading.CancellationToken.None);
    elf[0] ^= 1;
    File.WriteAllBytes(Path.Combine(src, "SLUS_000.01"), elf);
    bool refused = false;
    try { GameInstall.Install(src, root, json, _ => { }, (d, t) => { }, System.Threading.CancellationToken.None); } catch (InvalidDataException) { refused = true; }
    Check(refused && GameInstall.GameOk(root, json), "another revision is refused, the good copy stays");
    // A cancelled copy leaves no partial file behind.
    string root2 = Temp();
    File.Copy(json, Path.Combine(root2, "installer.json"));
    elf[0] ^= 1;
    File.WriteAllBytes(Path.Combine(src, "SLUS_000.01"), elf);
    var cts = new System.Threading.CancellationTokenSource();
    bool cancelled = false;
    try { GameInstall.Install(src, root2, Path.Combine(root2, "installer.json"), _ => { }, (d, t) => cts.Cancel(), cts.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "cancel stops the copy");
    Check(!Directory.EnumerateFiles(root2, "*.part", SearchOption.AllDirectories).Any() && !File.Exists(Path.Combine(root2, ".game-install.lock")), "no partial files or lock left");
  }

  // A PARAM.SFO with one TITLE entry.
  static byte[] Sfo(string title) {
    byte[] key = System.Text.Encoding.ASCII.GetBytes("TITLE\0\0\0"), value = new byte[128];
    byte[] t = System.Text.Encoding.UTF8.GetBytes(title);
    Array.Copy(t, value, t.Length);
    var d = new List<byte>();
    d.AddRange(new byte[] { 0, (byte)'P', (byte)'S', (byte)'F', 1, 1, 0, 0 });
    d.AddRange(BitConverter.GetBytes(20 + 16));            // key table
    d.AddRange(BitConverter.GetBytes(20 + 16 + key.Length)); // data table
    d.AddRange(BitConverter.GetBytes(1));
    d.AddRange(BitConverter.GetBytes((ushort)0)); d.AddRange(BitConverter.GetBytes((ushort)0x0204));
    d.AddRange(BitConverter.GetBytes(t.Length + 1)); d.AddRange(BitConverter.GetBytes(128)); d.AddRange(BitConverter.GetBytes(0));
    d.AddRange(key); d.AddRange(value);
    return d.ToArray();
  }

  static void TestGenericPspPort() {
    string disc = Temp(), repo = Temp();
    Directory.CreateDirectory(Path.Combine(disc, "PSP_GAME", "SYSDIR"));
    File.WriteAllText(Path.Combine(disc, "UMD_DATA.BIN"), "ULUS-12345|0123456789ABCDEF|0001|G");
    File.WriteAllBytes(Path.Combine(disc, "PSP_GAME", "PARAM.SFO"), Sfo("Test Game"));
    File.WriteAllBytes(Path.Combine(disc, "PSP_GAME", "SYSDIR", "EBOOT.BIN"), new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F' });
    Directory.CreateDirectory(Path.Combine(repo, "profiles"));
    File.WriteAllText(Path.Combine(repo, "launcher.json"),
        "{\"recomp\": \"PSPRecomp\", \"games_dir\": \"profiles\", \"generic\": {\"play\": \"Play.cmd\", \"play_cmd\": \"@echo off\\r\\n\", " +
        "\"exe\": \"build/native/bin/PSPNative.exe\", \"steps\": []}, \"generic_label\": \"^[A-Z]{4}-\\\\d{5}$\"}");
    Equal("psp", Consoles.Identify(disc, out string label), "console");
    Equal("ULUS-12345", label, "label");
    var r = Recomp.Load(repo);
    Check(r.Generic != null && System.Text.RegularExpressions.Regex.IsMatch(label, r.LabelRegex), "generic label matches");
    Check(r.Detect(disc, out _) == null, "no profile before");
    string id = r.CreateGenericPort(disc, label);
    Equal("ULUS-12345", id, "port id");
    r = Recomp.Load(repo);
    var g = r.Detect(disc, out _);
    Check(g != null && g.Id == id, "the disc is recognised as the new port");
    Equal("Test Game", g.Name, "name from PARAM.SFO");
    Check(File.Exists(Path.Combine(g.Dir, OS.ScriptName("Play.cmd"))), "start script");
    Equal("build/native/bin/PSPNative.exe", r.ConfigFor(g).Exe, "exe from the generic block");
  }

  static void TestGameInstallSafePaths() {
    foreach (string bad in new[] { "../x", "a/../b", "CON", "a/NUL.txt", "a\\b", "/abs", "x:y", "trail." })
      try { GameInstall.SafePath(bad); throw new Exception("accepted " + bad); } catch (InvalidDataException) { }
    GameInstall.SafePath("PSP_GAME/SYSDIR/EBOOT.BIN");
  }
}

}
