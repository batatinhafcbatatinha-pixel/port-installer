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

  // An Xbox One / Series dump is a folder with MicrosoftGame.config: recognised, and made into a linked port.
  static void TestXboxSeriesDump() {
    string dump = Temp();
    File.WriteAllText(Path.Combine(dump, "MicrosoftGame.config"),
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Game configVersion=\"1\">\n  <Identity Name=\"TestGame\" Publisher=\"CN=Test\" Version=\"1.0.0.0\"/>\n" +
        "  <ExecutableList><Executable Name=\"game.exe\" TargetDeviceFamily=\"Scarlett\" Id=\"Game\"/></ExecutableList>\n" +
        "  <TitleId>1a2B3c4D</TitleId>\n  <ShellVisuals DefaultDisplayName=\"Test &amp; Game\" PublisherDisplayName=\"Test\"/>\n</Game>\n");
    Equal("xseries", Consoles.Identify(dump, out string label), "console of a dump with MicrosoftGame.config");
    Equal("1A2B3C4D", label, "title id");
    Equal("1A2B3C4D", Recomp.XsTitle(dump, out string name), "title id again");
    Equal("Test & Game", name, "display name");
    Check(DiscScanner.IsExtractedDisc(dump), "the scan sees the dump folder");

    string bare = Temp();
    File.WriteAllText(Path.Combine(bare, "MicrosoftGame.config"), "<Game configVersion=\"1\"><ShellVisuals DefaultDisplayName=\"ms-resource:Name\"/></Game>");
    string id = Recomp.XsTitle(bare, out name);
    Check(id != null && id.Length == 8 && id == Recomp.XsTitle(bare, out _), "a config without a title id still gets a stable one");
    Check(name == null, "a resource reference is not a name");
    Check(Recomp.XsTitle(Temp(), out _) == null, "an empty folder is not a dump");

    string root = Temp();
    File.WriteAllText(Path.Combine(root, "launcher.json"),
        "{\"recomp\": \"xs\", \"repo\": \"a/xseriesrecomp\", \"games_dir\": \"games\", \"exe\": \"build/run/plan.json\", \"generic_label\": \"^[0-9A-Fa-f]{8}$\"," +
        " \"generic\": {\"play\": \"Play.cmd\", \"exe\": \"build/run/plan.json\", \"link_game\": true, \"play_cmd\": \"@echo off\r\n\"," +
        " \"steps\": [{\"run\": [\"{python}\", \"x.py\"]}]}}");
    var r = Recomp.Load(root);
    Equal("1A2B3C4D", r.CreateGenericPort(dump, label), "port id");
    string port = Path.Combine(r.PortsDir, "1A2B3C4D");
    var setup = Json.Object(File.ReadAllText(Path.Combine(port, "setup.json")));
    Equal("Test & Game", Convert.ToString(setup["game"]), "setup.json game");
    Equal("Xbox One / Series (1A2B3C4D)", Convert.ToString(setup["disc"]), "setup.json disc");
    var inst = Json.Object(File.ReadAllText(Path.Combine(port, "installer.json")));
    Equal("link", Convert.ToString(inst["mode"]), "the dump is linked, not copied");
    Check(((IDictionary<string, object>)inst["required_files"]).ContainsKey("MicrosoftGame.config"), "recognised by its config");
    Check(File.Exists(Path.Combine(port, OS.ScriptName("Play.cmd"))), "play script");
    r = Recomp.Load(root);
    var again = r.Detect(dump, out string l2);
    Check(again != null && again.Id == "1A2B3C4D", "the same dump is the same game the next time");
    Equal("1A2B3C4D", l2, "label on detection");
    var known = KnownRecomp.List().First(k => k.Id == "xseriesrecomp");
    Equal("xseries", known.Console, "recomps.json console");
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

  // A read-only STFS package (one hash table per level): the file table in block 0, default.xex contiguous in blocks
  // 1-2, media/a.bin chained through the hash table (block 4, then block 3). Block b is at 0xA000 + (b + 1) * 0x1000
  // (the level-0 table sits at 0xA000).
  static byte[] Stfs(out byte[] xex, out byte[] bin) {
    var p = new byte[0x10000];
    void Be(int o, uint v) { p[o] = (byte)(v >> 24); p[o + 1] = (byte)(v >> 16); p[o + 2] = (byte)(v >> 8); p[o + 3] = (byte)v; }
    System.Text.Encoding.ASCII.GetBytes("LIVE").CopyTo(p, 0);
    Be(0x340, 0xA000);
    p[0x379] = 0x24; p[0x37B] = 1; p[0x37C] = 1;   // descriptor size, read-only, 1 file table block (at block 0)
    Be(0x395, 5);
    System.Text.Encoding.BigEndianUnicode.GetBytes("Test Arcade").CopyTo(p, 0x411);
    int Block(int b) => 0xA000 + (b + 1) * 0x1000;
    void Entry(int i, string name, byte flags, int start, int parent, uint size) {
      int o = Block(0) + i * 0x40;
      System.Text.Encoding.ASCII.GetBytes(name).CopyTo(p, o);
      p[o + 0x28] = (byte)(name.Length | flags);
      p[o + 0x2F] = (byte)start; p[o + 0x32] = (byte)(parent >> 8); p[o + 0x33] = (byte)parent;
      Be(o + 0x34, size);
    }
    Entry(0, "media", 0x80, 0, 0xFFFF, 0);
    Entry(1, "default.xex", 0x40, 1, 0xFFFF, 0x1800);
    Entry(2, "a.bin", 0, 4, 0, 0x1100);
    xex = new byte[0x1800]; bin = new byte[0x1100];
    new Random(2).NextBytes(xex); new Random(3).NextBytes(bin);
    System.Text.Encoding.ASCII.GetBytes("XEX2").CopyTo(xex, 0);
    Array.Copy(xex, 0, p, Block(1), 0x1800);
    Array.Copy(bin, 0, p, Block(4), 0x1000);
    Array.Copy(bin, 0x1000, p, Block(3), 0x100);
    p[0xA000 + 4 * 0x18 + 0x17] = 3;   // block 4 -> block 3
    return p;
  }

  static void TestStfsPackage() {
    string src = Temp(), root = Temp();
    string dir = Path.Combine(src, "584100FF", "000D0000");
    Directory.CreateDirectory(dir);
    byte[] package = Stfs(out byte[] xex, out byte[] bin);
    File.WriteAllBytes(Path.Combine(dir, "ABCDEF0123"), package);
    string found = GameInstall.StfsPackage(src);
    Equal(Path.Combine(dir, "ABCDEF0123"), found, "the package in the console's folder layout");
    Equal("Test Arcade", GameInstall.StfsTitle(found), "the package's display name");
    using (var s = File.OpenRead(found)) {
      var entries = GameInstall.Inventory(s, s.Length, "stfs");
      Equal(2, entries.Count, "files (the directory is not one)");
      var x = entries.Single(e => e.Path == "default.xex");
      Equal(1, x.Extents.Count, "a contiguous file is one extent");
      var a = entries.Single(e => e.Path == "media/a.bin");
      Equal(2, a.Extents.Count, "a chained file follows the hash table");
    }
    Equal(Sha(xex).ToLowerInvariant(), Recomp.DiscFileSha256(src, "default.xex", out long size).ToLowerInvariant(), "the identity file's hash");
    Equal((long)xex.Length, size, "its size");
    string json = Path.Combine(root, "installer.json");
    File.WriteAllText(json, "{\"schema\": 1, \"name\": \"T\", \"filesystem\": \"stfs\", \"destination\": \"game_files\", " +
                            "\"required_files\": {\"default.xex\": \"" + Sha(xex) + "\"}, \"required_paths\": []}");
    GameInstall.Install(src, root, json, _ => { }, (d, t) => { }, System.Threading.CancellationToken.None);
    Check(File.ReadAllBytes(Path.Combine(root, "game_files", "media", "a.bin")).SequenceEqual(bin), "chained file extracted");
    Check(GameInstall.GameOk(root, json), "game files in place");
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

  // A generic port takes the generic block's settings (not the repository's games'), and a choice with a word
  // stores that word in the ini key.
  // ------------------------------------------------------------------ controllers (input.ini, port_input)

  static void TestInputSettingsScope() {
    string game = Temp();
    InputSettings.Put("stick_deadzone", 20, null);
    Equal(20, InputSettings.GetInt("stick_deadzone", game, 12), "global seen by a game");
    InputSettings.Put("stick_deadzone", 7, game);
    Equal(7, InputSettings.GetInt("stick_deadzone", game, 12), "game over global");
    Equal(20, InputSettings.GetInt("stick_deadzone", null, 12), "global untouched");
    Equal(100, InputSettings.GetInt("mouse_sensitivity", game, 100), "default");
  }

  static void TestRemapRoundTrip() {
    var c = new Controller { Guid = "03000000de280000ff11000000000000", Name = "Pad" };
    var r = Remap.Parse("03000000de280000ff11000000000000,Pad,a:b0,b:b1,leftx:a0,dpup:h0.1,platform:Windows,");
    Equal("b0", r.Fields["a"], "parsed a");
    Check(!r.Fields.ContainsKey("platform"), "platform dropped");
    r.Set("a", new RawInput { Source = "b5" });
    r.Set("leftx", new RawInput { Source = "-a3" });
    r.Set("righttrigger", new RawInput { Source = "a5" });
    Equal("03000000de280000ff11000000000000,Pad,a:b5,b:b1,righttrigger:a5,dpup:h0.1,leftx:a3,platform:Windows,", r.ToMapping(c), "mapping");
  }

  static void TestSdlLists() {
    if (!OS.Windows) return;
    Check(Sdl.Start(), "SDL3 starts: " + Sdl.Error);
    var list = Sdl.List();   // usually empty on a test machine; must not throw
    foreach (var c in list) Check(c.Guid.Length == 32, "GUID text");
  }

  static void TestGenericPortSettings() {
    string repo = Temp();
    Directory.CreateDirectory(Path.Combine(repo, "games"));
    string port = Path.Combine(repo, ".local", "ports", "584108B7");
    Directory.CreateDirectory(port);
    File.WriteAllText(Path.Combine(port, "setup.json"), "{\"game\": \"T\", \"play\": \"Play.cmd\"}");
    File.WriteAllText(Path.Combine(port, "installer.json"), "{\"required_files\": {\"default.xex\": \"00\"}}");
    File.WriteAllText(Path.Combine(repo, "launcher.json"),
        "{\"recomp\": \"rexglue-ports\", \"games_dir\": \"games\", \"ini\": \"own.ini\", " +
        "\"groups\": [{\"pt\": \"Seus\", \"en\": \"Own\", \"items\": [{\"key\": \"x\", \"pt\": \"X\", \"en\": \"X\"}]}], " +
        "\"generic\": {\"play\": \"Play.cmd\", \"steps\": [], \"settings\": {\"ini\": \"pc-settings.ini\", \"section\": \"video\", " +
        "\"groups\": [{\"pt\": \"Vídeo\", \"en\": \"Video\", \"items\": [" +
        "{\"key\": \"post_effect\", \"pt\": \"AA\", \"en\": \"AA\", \"default\": 0, \"choices\": [" +
        "{\"v\": 0, \"pt\": \"Desligado\", \"en\": \"Off\", \"word\": \"none\"}, {\"v\": 1, \"pt\": \"FXAA\", \"en\": \"FXAA\", \"word\": \"fxaa\"}]}," +
        "{\"key\": \"resolution_scale\", \"pt\": \"R\", \"en\": \"R\", \"default\": 1, \"choices\": [{\"v\": 1, \"pt\": \"1\"}, {\"v\": 3, \"pt\": \"3\"}]}" +
        "]}]}}}");
    var r = Recomp.Load(repo);
    var g = r.Games.Single(x => x.Id == "584108B7");
    var cfg = r.ConfigFor(g);
    Equal("pc-settings.ini", cfg.Ini, "the generic block's ini");
    Equal("video", cfg.Section, "its section");
    Equal("post_effect", cfg.Groups.Single().Items[0].Key, "its settings, not the repository's games'");
    string ini = Path.Combine(port, cfg.Ini);
    var aa = cfg.Groups[0].Items[0];
    var scale = cfg.Groups[0].Items[1];
    Equal(0, SettingsStore.Read(aa, cfg, port, null, ini, null), "default while the ini has nothing");
    SettingsStore.Write(aa, 1, cfg, port, null, ini, null);
    SettingsStore.Write(scale, 3, cfg, port, null, ini, null);
    Equal("fxaa", Ini.GetText(ini, "video", "post_effect"), "the word goes into the ini");
    Equal("3", Ini.GetText(ini, "video", "resolution_scale"), "a number stays a number");
    Equal(1, SettingsStore.Read(aa, cfg, port, null, ini, null), "and reads back as its choice");
  }

  static void TestGameInstallSafePaths() {
    foreach (string bad in new[] { "../x", "a/../b", "CON", "a/NUL.txt", "a\\b", "/abs", "x:y", "trail." })
      try { GameInstall.SafePath(bad); throw new Exception("accepted " + bad); } catch (InvalidDataException) { }
    GameInstall.SafePath("PSP_GAME/SYSDIR/EBOOT.BIN");
  }
}

}
