// The menus of the Linux/macOS launcher: the same File, Game, Settings, View and Updates menus as the Windows
// launcher. One description of each menu becomes the window's menu bar on Linux and the system menu bar on macOS.
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace PortLauncher {

sealed class MenuSpec {
  public string Text, GestureText;
  public Action Action;
  public KeyGesture Gesture;
  public bool Enabled = true, Bold;
  public bool? Check;                 // a check item when set
  public Action<bool> Toggled;        // a check item that flips itself
  public Func<List<MenuSpec>> Items;  // a submenu, built when it opens
  public bool IsSeparator;
  public MenuSpec(string text, Action action) { Text = text; Action = action; }
  public static MenuSpec Separator => new MenuSpec("-", null) { IsSeparator = true };
}

sealed class Menus {
  readonly MainWindow w;
  readonly List<KeyValuePair<KeyGesture, Action>> keys = new List<KeyValuePair<KeyGesture, Action>>();
  Menu bar;
  public Menus(MainWindow window) { w = window; }

  static KeyModifiers Cmd => OS.Mac ? KeyModifiers.Meta : KeyModifiers.Control;
  static KeyGesture G(Key k, KeyModifiers extra = KeyModifiers.None) => new KeyGesture(k, Cmd | extra);

  List<KeyValuePair<string, Func<List<MenuSpec>>>> Top() => new List<KeyValuePair<string, Func<List<MenuSpec>>>> {
    new KeyValuePair<string, Func<List<MenuSpec>>>(L.T("Arquivo", "File"), FileMenu),
    new KeyValuePair<string, Func<List<MenuSpec>>>(L.T("Jogo", "Game"), w.GameMenuItems),
    new KeyValuePair<string, Func<List<MenuSpec>>>(L.T("Configurações", "Settings"), SettingsMenu),
    new KeyValuePair<string, Func<List<MenuSpec>>>(L.T("Exibir", "View"), ViewMenu),
    new KeyValuePair<string, Func<List<MenuSpec>>>(L.T("Atualizações", "Updates"), UpdatesMenu),
  };

  List<MenuSpec> FileMenu() => new List<MenuSpec> {
    new MenuSpec(L.T("Abrir disco (ISO)...", "Open disc (ISO)..."), () => w.ChooseDisc(null)) { Gesture = G(Key.O) },
    new MenuSpec(L.T("Adicionar pastas de jogos...", "Add game folders..."), w.AddFolders) { Gesture = G(Key.O, KeyModifiers.Shift) },
    new MenuSpec(L.T("Pastas de jogos...", "Game folders..."), w.ManageFolders),
    new MenuSpec(L.T("Procurar jogos de novo", "Rescan game folders"), w.Rescan) { Gesture = new KeyGesture(Key.F5) },
    MenuSpec.Separator,
    new MenuSpec(L.T("Recompiladores...", "Recompilers..."), w.ShowRecomps) { Gesture = G(Key.R) },
    new MenuSpec(L.T("Abrir a pasta do recompilador", "Open the recompiler folder"), w.OpenRecompFolder),
    MenuSpec.Separator,
    new MenuSpec(OS.Mac ? L.T("Fechar", "Close") : L.T("Sair", "Exit"), w.Close) { Gesture = OS.Mac ? G(Key.W) : G(Key.Q) },
  };

  List<MenuSpec> SettingsMenu() => new List<MenuSpec> {
    new MenuSpec(L.T("Configurações do PC...", "PC settings..."), w.ShowSettings) { Gesture = G(Key.P) },
    new MenuSpec(L.T("Dependências...", "Dependencies..."), w.ShowDeps) { Gesture = G(Key.D) },
    new MenuSpec(L.T("Controles...", "Controllers..."), w.ShowControllers) { Gesture = G(Key.J) },
    MenuSpec.Separator,
    new MenuSpec(L.T("Idioma (Language)", "Language (Idioma)"), null) { Items = () => new List<MenuSpec> {
      new MenuSpec(L.T("Igual ao sistema", "Same as the system"), () => w.SetLanguage("system")) { Check = L.Mode == "system" },
      new MenuSpec("Português", () => w.SetLanguage("pt")) { Check = L.Mode == "pt" },
      new MenuSpec("English", () => w.SetLanguage("en")) { Check = L.Mode == "en" },
    } },
  };

  List<MenuSpec> ViewMenu() => new List<MenuSpec> {
    new MenuSpec(L.T("Lista", "List"), () => w.SetGrid(false)) { Gesture = G(Key.D1) },
    new MenuSpec(L.T("Grade de capas", "Cover grid"), () => w.SetGrid(true)) { Gesture = G(Key.D2) },
    new MenuSpec(L.T("Tamanho das capas", "Cover size"), null) { Items = () => new[] { L.T("Pequenas", "Small"), L.T("Médias", "Medium"), L.T("Grandes", "Large") }
        .Select((t, i) => new MenuSpec(t, () => w.TileSize = i) { Check = w.TileSize == i }).ToList() },
    MenuSpec.Separator,
    new MenuSpec(L.T("Painel do jogo", "Game panel"), null) { Check = w.DetailVisible, Toggled = on => w.SetDetailVisible(on) },
    new MenuSpec("Log", null) { Check = w.LogVisible, Toggled = on => w.SetLogVisible(on) },
    MenuSpec.Separator,
    new MenuSpec(L.T("Baixar capas da internet", "Download covers from the internet"), null) { Check = Prefs.Bool("online_covers", true), Toggled = on => { Prefs.Set("online_covers", on); w.ReloadCovers(); } },
    MenuSpec.Separator,
    new MenuSpec(L.T("Tema", "Theme"), null) { Items = () => new List<MenuSpec> {
      new MenuSpec(L.T("Igual ao sistema", "Same as the system"), () => w.SetTheme("system")) { Check = Look.Mode == "system" },
      new MenuSpec(L.T("Escuro", "Dark"), () => w.SetTheme("dark")) { Check = Look.Mode == "dark" },
      new MenuSpec(L.T("Claro", "Light"), () => w.SetTheme("light")) { Check = Look.Mode == "light" },
    } },
  };

  List<MenuSpec> UpdatesMenu() => new List<MenuSpec> {
    new MenuSpec(L.T("Procurar atualizações agora", "Check for updates now"), () => w.CheckUpdates(true)),
    new MenuSpec(L.T("Canal de atualização", "Update channel"), null) { Items = () => new List<MenuSpec> {
      new MenuSpec(L.T("Estável (último release)", "Stable (latest release)"), () => { Updater.Channel = UpdateChannel.Stable; w.CheckUpdates(true); }) { Check = Updater.Channel == UpdateChannel.Stable },
      new MenuSpec(L.T("Nightly (última compilação)", "Nightly (latest build)"), () => { Updater.Channel = UpdateChannel.Nightly; w.CheckUpdates(true); }) { Check = Updater.Channel == UpdateChannel.Nightly },
    } },
    MenuSpec.Separator,
    new MenuSpec(L.T("Procurar ao abrir", "Check when the launcher opens"), null) { Check = Prefs.Bool("update_check", true), Toggled = on => Prefs.Set("update_check", on) },
    new MenuSpec(L.T("Atualizar o launcher sozinho", "Update the launcher automatically"), null) { Check = Prefs.Bool("update_auto", true), Toggled = on => Prefs.Set("update_auto", on) },
    new MenuSpec(L.T("Atualizar o recompilador sozinho", "Update the recompiler automatically"), null) { Check = Prefs.Bool("update_recomp_auto", false), Toggled = on => Prefs.Set("update_recomp_auto", on) },
    MenuSpec.Separator,
    new MenuSpec(L.T("Sobre o launcher", "About the launcher"), w.About),
  };

  // Linux: the menu bar inside the window. macOS: the system menu bar (returns null).
  public Control Bar() {
    foreach (var top in Top()) if (top.Value != w.GameMenuItems) Collect(top.Value());
    if (OS.Mac) {
      var native = new NativeMenu();
      foreach (var top in Top()) {
        var sub = new NativeMenu();
        var item = new NativeMenuItem(top.Key) { Menu = sub };
        var build = top.Value;
        sub.NeedsUpdate += (s, e) => FillNative(sub, build());
        FillNative(sub, build());
        native.Items.Add(item);
      }
      NativeMenu.SetMenu(w, native);
      return null;
    }
    bar = new Menu { Background = Look.B(Look.Surface), Foreground = Look.B(Look.Text), FontSize = Look.Pt(9.5), Padding = new Avalonia.Thickness(8, 3, 0, 3) };
    foreach (var top in Top()) {
      var item = new MenuItem { Header = top.Key, Padding = new Avalonia.Thickness(8, 2) };
      var build = top.Value;
      item.Items.Add(new MenuItem { Header = "-" });
      item.SubmenuOpened += (s, e) => { if (ReferenceEquals(e.Source, item)) Fill(item.Items, build()); };
      bar.Items.Add(item);
    }
    return new Border { Background = Look.B(Look.Surface), Child = bar };
  }

  void Collect(List<MenuSpec> items) {
    foreach (var m in items) {
      if (m.Gesture != null && m.Action != null) keys.Add(new KeyValuePair<KeyGesture, Action>(m.Gesture, m.Action));
      if (m.Items != null) Collect(m.Items());
    }
  }

  public bool HandleKey(KeyEventArgs e) {
    foreach (var kv in keys)
      if (kv.Key.Matches(e)) { kv.Value(); return true; }
    return false;
  }

  public void Sync() { }   // menus are rebuilt each time they open

  static void Fill(ItemCollection target, List<MenuSpec> items) {
    target.Clear();
    foreach (var m in items) target.Add(Item(m));
  }

  static Control Item(MenuSpec m) {
    if (m.IsSeparator) return new Separator();
    var item = new MenuItem { Header = m.Text, IsEnabled = m.Enabled, InputGesture = m.Gesture };
    if (m.Bold) item.FontWeight = FontWeight.SemiBold;
    if (m.Check != null) { item.ToggleType = MenuItemToggleType.CheckBox; item.IsChecked = m.Check.Value; }
    if (m.Items != null) {
      item.Items.Add(new MenuItem { Header = "-" });
      var build = m.Items;
      item.SubmenuOpened += (s, e) => { if (ReferenceEquals(e.Source, item)) Fill(item.Items, build()); };
    } else {
      item.Click += (s, e) => {
        if (m.Toggled != null) m.Toggled(!(m.Check ?? false));
        else m.Action?.Invoke();
      };
    }
    return item;
  }

  public static ContextMenu Context(List<MenuSpec> items) {
    var menu = new ContextMenu();
    foreach (var m in items) menu.Items.Add(Item(m));
    return menu;
  }

  static void FillNative(NativeMenu menu, List<MenuSpec> items) {
    menu.Items.Clear();
    foreach (var m in items) {
      if (m.IsSeparator) { menu.Items.Add(new NativeMenuItemSeparator()); continue; }
      var item = new NativeMenuItem(m.Text) { IsEnabled = m.Enabled, Gesture = m.Gesture };
      if (m.Check != null) { item.ToggleType = NativeMenuItemToggleType.CheckBox; item.IsChecked = m.Check.Value; }
      if (m.Items != null) {
        var sub = new NativeMenu();
        var build = m.Items;
        sub.NeedsUpdate += (s, e) => FillNative(sub, build());
        FillNative(sub, build());
        item.Menu = sub;
      } else {
        item.Click += (s, e) => {
          if (m.Toggled != null) m.Toggled(!(m.Check ?? false));
          else m.Action?.Invoke();
        };
      }
      menu.Items.Add(item);
    }
  }
}

}
