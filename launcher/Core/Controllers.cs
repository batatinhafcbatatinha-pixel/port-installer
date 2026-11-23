// Controllers: the launcher's view of input.ini (port_input, the recompilers' shared input layer) and of the
// controllers connected now, through SDL3 (Windows: SDL3.dll fetched once into the launcher's folder, the same
// pinned build the games use).
//
// input.ini, section [input]: %APPDATA%\port-launcher\input.ini for every game, input.ini in a game's folder for
// that game only (key by key over the first). Remaps are SDL mapping strings per controller GUID (mapping.<GUID>).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PortLauncher {

// What a connected controller is.
sealed class Controller {
  public int Id;           // SDL joystick instance id (valid while connected)
  public string Name, Guid;
  public bool Gamepad;     // SDL knows its buttons (or the player remapped it)
  public bool Wheel;
  public override string ToString() => Name + (Wheel ? L.T("  (volante)", "  (wheel)") : Gamepad ? "" : L.T("  (genérico)", "  (generic)"));
}

// One input the player pressed: its SDL mapping source ("b3", "h0.4", "a2", "+a5", "-a1") and a description.
sealed class RawInput {
  public string Source, Text;
}

static class InputSettings {
  public static string GlobalFile => Path.Combine(Prefs.Dir, "input.ini");
  public static string GameFile(string gameDir) => Path.Combine(gameDir, "input.ini");
  public const string Section = "input";

  // The value a game sees: its own file over the global one; def when neither has it.
  public static string Get(string key, string gameDir, string def) {
    string v = gameDir != null ? Ini.GetText(GameFile(gameDir), Section, key) : "";
    if (string.IsNullOrEmpty(v)) v = Ini.GetText(GlobalFile, Section, key);
    return string.IsNullOrEmpty(v) ? def : v;
  }
  public static int GetInt(string key, string gameDir, int def) =>
      int.TryParse(Get(key, gameDir, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : def;

  public static void Put(string key, string value, string gameDir) {
    string file = gameDir != null ? GameFile(gameDir) : GlobalFile;
    Directory.CreateDirectory(Path.GetDirectoryName(file));
    Ini.PutText(file, Section, key, value);
  }
  public static void Put(string key, int value, string gameDir) => Put(key, value.ToString(CultureInfo.InvariantCulture), gameDir);
}

// SDL3 for the Controllers page. Windows only (the games read controllers through it on every system, but the
// launcher lists them only on Windows for now).
static class Sdl {
  [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
  [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

  [StructLayout(LayoutKind.Sequential)] struct Guid16 { public long A, B; }
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte InitFn(uint flags);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte SetHintFn(string name, string value);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddMappingsFn(string path);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int AddMappingFn(string mapping);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetJoysticksFn(out int count);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NameForIdFn(uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate Guid16 GuidForIdFn(uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void GuidToStringFn(Guid16 guid, StringBuilder text, int size);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TypeForIdFn(uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte IsGamepadFn(uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr OpenFn(uint id);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CloseFn(IntPtr stick);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int CountFn(IntPtr stick);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate short AxisFn(IntPtr stick, int axis);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte ButtonFn(IntPtr stick, int button);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte HatFn(IntPtr stick, int hat);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void UpdateFn();
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeFn(IntPtr p);
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr MappingForGuidFn(Guid16 guid);

  static InitFn init; static SetHintFn setHint; static AddMappingsFn addMappings; static AddMappingFn addMapping;
  static GetJoysticksFn getJoysticks; static NameForIdFn nameForId; static GuidForIdFn guidForId;
  static GuidToStringFn guidToString; static TypeForIdFn typeForId; static IsGamepadFn isGamepad;
  static OpenFn open; static CloseFn close; static CountFn axes, buttons, hats;
  static AxisFn axis; static ButtonFn button; static HatFn hat; static UpdateFn update; static FreeFn free;
  static MappingForGuidFn mappingForGuid;
  static bool ready;
  public static string Error;

  // The same SDL3 build and controller database the games stage next to themselves (port_input.cmake).
  const string SdlZip = "https://github.com/libsdl-org/SDL/releases/download/release-3.2.26/SDL3-3.2.26-win32-x64.zip";
  const string SdlZipSha = "866bd702f16cc767420ed401e8c5663679833a0de56892f03e4ef3e9c0a5a333";
  const string Db = "https://raw.githubusercontent.com/mdqinc/SDL_GameControllerDB/22a98f6339abf4b6ca6cb061443a70e4f7ab252d/gamecontrollerdb.txt";
  const string DbSha = "0f17831b6a9e0e9fc2f52bf8ab1bef1d30ffd307d966c2118e556db345ea7e96";
  static string Dir => Path.Combine(Prefs.Dir, "sdl3");

  static byte[] Download(string url, string sha) {
    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    using (var web = new WebClient()) {
      web.Headers.Add("User-Agent", "port-launcher");
      byte[] data = web.DownloadData(url);
      using (var h = SHA256.Create())
        if (BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant() != sha)
          throw new InvalidDataException(L.T("Arquivo baixado diferente do esperado: ", "Downloaded file differs from the expected one: ") + url);
      return data;
    }
  }

  // Fetches SDL3.dll and the database once (a few MB). False with Error set when it cannot.
  public static bool Start() {
    if (ready) return true;
    if (!OS.Windows) { Error = L.T("A lista de controles está disponível no Windows.", "The controller list is available on Windows."); return false; }
    try {
      Directory.CreateDirectory(Dir);
      string dll = Path.Combine(Dir, "SDL3.dll"), db = Path.Combine(Dir, "gamecontrollerdb.txt");
      if (!File.Exists(dll)) {
        using (var zip = new ZipArchive(new MemoryStream(Download(SdlZip, SdlZipSha))))
          zip.Entries.First(e => e.Name == "SDL3.dll").ExtractToFile(dll, true);
      }
      if (!File.Exists(db)) File.WriteAllBytes(db, Download(Db, DbSha));
      IntPtr lib = LoadLibraryW(dll);
      if (lib == IntPtr.Zero) { Error = "SDL3.dll"; return false; }
      T Bind<T>(string name) where T : class {
        IntPtr p = GetProcAddress(lib, name);
        if (p == IntPtr.Zero) throw new MissingMethodException(name);
        return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
      }
      init = Bind<InitFn>("SDL_Init"); setHint = Bind<SetHintFn>("SDL_SetHint");
      addMappings = Bind<AddMappingsFn>("SDL_AddGamepadMappingsFromFile"); addMapping = Bind<AddMappingFn>("SDL_AddGamepadMapping");
      getJoysticks = Bind<GetJoysticksFn>("SDL_GetJoysticks"); nameForId = Bind<NameForIdFn>("SDL_GetJoystickNameForID");
      guidForId = Bind<GuidForIdFn>("SDL_GetJoystickGUIDForID"); guidToString = Bind<GuidToStringFn>("SDL_GUIDToString");
      typeForId = Bind<TypeForIdFn>("SDL_GetJoystickTypeForID"); isGamepad = Bind<IsGamepadFn>("SDL_IsGamepad");
      open = Bind<OpenFn>("SDL_OpenJoystick"); close = Bind<CloseFn>("SDL_CloseJoystick");
      axes = Bind<CountFn>("SDL_GetNumJoystickAxes"); buttons = Bind<CountFn>("SDL_GetNumJoystickButtons");
      hats = Bind<CountFn>("SDL_GetNumJoystickHats"); axis = Bind<AxisFn>("SDL_GetJoystickAxis");
      button = Bind<ButtonFn>("SDL_GetJoystickButton"); hat = Bind<HatFn>("SDL_GetJoystickHat");
      update = Bind<UpdateFn>("SDL_UpdateJoysticks"); free = Bind<FreeFn>("SDL_free");
      mappingForGuid = Bind<MappingForGuidFn>("SDL_GetGamepadMappingForGUID");
      setHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
      if (init(0x2000u | 0x200u) == 0) { Error = "SDL_Init"; return false; }   // gamepad + joystick
      addMappings(db);
      ready = true;
      return true;
    } catch (Exception e) {
      Error = e.Message;
      return false;
    }
  }

  // The player's remaps, so the list shows what the games will see.
  public static void AddMapping(string mapping) { if (ready && !string.IsNullOrEmpty(mapping)) addMapping(mapping); }

  // SDL's current mapping of a controller (its database entry or the player's remap); "" when SDL has none.
  public static string MappingFor(Controller c) {
    if (!ready) return "";
    IntPtr p = mappingForGuid(guidForId((uint)c.Id));
    if (p == IntPtr.Zero) return "";
    string m = Marshal.PtrToStringAnsi(p);
    free(p);
    return m ?? "";
  }

  public static List<Controller> List() {
    var list = new List<Controller>();
    if (!ready) return list;
    update();
    IntPtr ids = getJoysticks(out int count);
    for (int i = 0; i < count; ++i) {
      uint id = (uint)Marshal.ReadInt32(ids, i * 4);
      var guid = new StringBuilder(40);
      guidToString(guidForId(id), guid, guid.Capacity);
      list.Add(new Controller {
        Id = (int)id, Name = (Marshal.PtrToStringAnsi(nameForId(id)) ?? "Controller").Replace(",", " "),
        Guid = guid.ToString(), Gamepad = isGamepad(id) != 0, Wheel = typeForId(id) == 2,
      });
    }
    if (ids != IntPtr.Zero) free(ids);
    return list;
  }

  // Watches one controller for the next input pressed (Poll repeatedly; null until one comes).
  public sealed class Capture : IDisposable {
    readonly IntPtr stick;
    readonly short[] rest;
    public Capture(Controller c) {
      stick = ready ? open((uint)c.Id) : IntPtr.Zero;
      rest = new short[stick == IntPtr.Zero ? 0 : axes(stick)];
      if (stick != IntPtr.Zero) { update(); for (int a = 0; a < rest.Length; ++a) rest[a] = axis(stick, a); }
    }
    public bool Ok => stick != IntPtr.Zero;
    public RawInput Poll() {
      if (stick == IntPtr.Zero) return null;
      update();
      for (int b = 0; b < buttons(stick); ++b)
        if (button(stick, b) != 0) return new RawInput { Source = "b" + b, Text = L.T("botão ", "button ") + b };
      for (int h = 0; h < hats(stick); ++h) {
        int v = hat(stick, h);
        foreach (int bit in new[] { 1, 2, 4, 8 })
          if ((v & bit) != 0) return new RawInput { Source = "h" + h + "." + bit, Text = L.T("direcional ", "hat ") + h + "." + bit };
      }
      for (int a = 0; a < rest.Length; ++a) {
        int now = axis(stick, a), delta = now - rest[a];
        if (Math.Abs(delta) < 16000) continue;
        // An axis resting at an end (a trigger, a pedal) moves the whole range one way: the full axis. One resting in
        // the middle (a stick) is taken by the half that moved.
        bool atEnd = Math.Abs((int)rest[a]) > 24000;
        string src = atEnd ? "a" + a : (delta > 0 ? "+a" : "-a") + a;
        return new RawInput { Source = src, Text = L.T("eixo ", "axis ") + (atEnd ? "" : delta > 0 ? "+" : "-") + a };
      }
      return null;
    }
    public void Dispose() { if (stick != IntPtr.Zero) close(stick); }
  }
}

// A remap of one controller: SDL mapping fields (target:source), read from and written to its mapping string.
sealed class Remap {
  // SDL's names for the pad's inputs, in the order the page lists them.
  public static readonly string[] Targets = {
    "a", "b", "x", "y", "leftshoulder", "rightshoulder", "lefttrigger", "righttrigger", "back", "start", "guide",
    "leftstick", "rightstick", "dpup", "dpdown", "dpleft", "dpright", "leftx", "lefty", "rightx", "righty" };
  public static string Label(string target) {
    switch (target) {
      case "a": return L.T("A (Cruz)", "A (Cross)");
      case "b": return L.T("B (Círculo)", "B (Circle)");
      case "x": return L.T("X (Quadrado)", "X (Square)");
      case "y": return L.T("Y (Triângulo)", "Y (Triangle)");
      case "leftshoulder": return "LB / L1";
      case "rightshoulder": return "RB / R1";
      case "lefttrigger": return "LT / L2";
      case "righttrigger": return "RT / R2";
      case "back": return "Back / Select";
      case "start": return "Start";
      case "guide": return "Guide / Home";
      case "leftstick": return L.T("Clique analógico esq. (L3)", "Left stick click (L3)");
      case "rightstick": return L.T("Clique analógico dir. (R3)", "Right stick click (R3)");
      case "dpup": return L.T("Direcional cima", "D-pad up");
      case "dpdown": return L.T("Direcional baixo", "D-pad down");
      case "dpleft": return L.T("Direcional esquerda", "D-pad left");
      case "dpright": return L.T("Direcional direita", "D-pad right");
      case "leftx": return L.T("Analógico esq. horizontal", "Left stick horizontal");
      case "lefty": return L.T("Analógico esq. vertical", "Left stick vertical");
      case "rightx": return L.T("Analógico dir. horizontal", "Right stick horizontal");
      case "righty": return L.T("Analógico dir. vertical", "Right stick vertical");
    }
    return target;
  }

  public readonly Dictionary<string, string> Fields = new Dictionary<string, string>();

  public static Remap Parse(string mapping) {
    var r = new Remap();
    if (string.IsNullOrEmpty(mapping)) return r;
    var parts = mapping.Split(',');
    for (int i = 2; i < parts.Length; ++i) {
      int colon = parts[i].IndexOf(':');
      if (colon > 0) r.Fields[parts[i].Substring(0, colon)] = parts[i].Substring(colon + 1);
    }
    r.Fields.Remove("platform");
    return r;
  }

  // An axis target taken from a half axis keeps the whole axis (a stick moved one way is that axis).
  public void Set(string target, RawInput input) {
    string source = input.Source;
    if ((target.EndsWith("x") || target.EndsWith("y")) && target.Length > 1 && (source.StartsWith("+") || source.StartsWith("-")))
      source = source.Substring(1);
    Fields[target] = source;
  }

  public string ToMapping(Controller c) =>
      c.Guid + "," + c.Name + "," + string.Join(",", Targets.Where(t => Fields.ContainsKey(t)).Select(t => t + ":" + Fields[t])) +
      ",platform:Windows,";
}

}
