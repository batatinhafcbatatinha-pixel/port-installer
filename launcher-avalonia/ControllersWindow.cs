// The Controllers window (as launcher/ControllersPage.cs): which controller is player 1, remaps, dead zones,
// sensitivities (stick, mouse, wheel) and vibration, for every game or for the selected one. It writes input.ini,
// which port_input (the input layer every recompiler's runtime shares) reads when the game starts.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace PortLauncher {

sealed partial class MainWindow {
  public void ShowControllers() => OpenControllersWindow().ShowDialog(this);

  public Window OpenControllersWindow() {
    string gameDir = root;
    string scopeDir = null;           // null: every game (the global file)
    bool loading = false;
    var w = new StyledWindow(L.T("Controles", "Controllers") + (gameDir != null ? "  ·  " + cfg.Game : ""), 940, 720) { MinWidth = 760, MinHeight = 480 };
    var stack = new StackPanel();
    var loaders = new List<Action>();

    stack.Children.Add(Words(L.T(
        "Valem para os jogos de todos os recompiladores (PS2, PSP, Xbox, Xbox 360) na próxima vez que abrirem. " +
        "Controles de Xbox, PlayStation, Nintendo, genéricos e volantes são reconhecidos pelo SDL3.",
        "They apply to the games of every recompiler (PS2, PSP, Xbox, Xbox 360) the next time they start. " +
        "Xbox, PlayStation, Nintendo, generic controllers and wheels are recognized through SDL3."),
        Look.UI(), 9.5, Look.Text3, new Thickness(4, 0, 4, 14)));

    StackPanel card = null;
    bool firstRow = true;
    void NewCard(string title) {
      card = new StackPanel();
      card.Children.Add(Words(title.ToUpperInvariant(), Look.Semi, 9.5, Look.Accent, new Thickness(0, 0, 0, 4)));
      stack.Children.Add(new Card { Margin = new Thickness(0, 0, 0, 16), Child = card });
      firstRow = true;
    }
    void AddRow(string title, string help, Control editor) {
      var left = new StackPanel { Margin = new Thickness(0, 0, 24, 0), VerticalAlignment = VerticalAlignment.Center };
      left.Children.Add(Words(title, Look.Semi, 10.5, Look.Text, new Thickness(0, 0, 0, 2)));
      if (!string.IsNullOrEmpty(help)) left.Children.Add(Words(help, Look.UI(), 9, Look.Text3, new Thickness(0)));
      editor.VerticalAlignment = VerticalAlignment.Center;
      editor.HorizontalAlignment = HorizontalAlignment.Right;
      var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,380"), Margin = new Thickness(0, 10) };
      Grid.SetColumn(editor, 1);
      row.Children.Add(left); row.Children.Add(editor);
      if (!firstRow) card.Children.Add(StyledWindow.Line());
      firstRow = false;
      card.Children.Add(row);
    }
    void Percent(string key, int def, int min, int max, int step, string title, string help) {
      var s = new SliderBox();
      s.Range(min, max, step);
      s.ValueChanged += (o, e) => { if (!loading) InputSettings.Put(key, s.Value, scopeDir); };
      loaders.Add(() => s.Value = InputSettings.GetInt(key, scopeDir, def));
      AddRow(title, help, s);
    }
    void Toggle(string key, bool def, string on, string off, string title, string help) {
      var t = new ToggleSwitch { Width = 360, OnText = on, OffText = off };
      t.CheckedChanged += (o, e) => { if (!loading) InputSettings.Put(key, t.Checked ? 1 : 0, scopeDir); };
      loaders.Add(() => t.Checked = InputSettings.GetInt(key, scopeDir, def ? 1 : 0) != 0);
      AddRow(title, help, t);
    }
    void Axis(string key, int def, string title) {
      var b = new ChoiceBox();
      for (int i = 0; i < 8; ++i) b.Items.Add(new ChoiceBox.Entry { Value = i, Text = L.T("Eixo ", "Axis ") + i });
      b.SelectedIndexChanged += (o, e) => { if (!loading && b.Selected != null) InputSettings.Put(key, b.Selected.Value, scopeDir); };
      loaders.Add(() => b.Select(InputSettings.GetInt(key, scopeDir, def), def));
      AddRow(title, null, b);
    }

    // Scope
    NewCard(L.T("Onde salvar", "Where to save"));
    var scope = new ChoiceBox();
    scope.Items.Add(new ChoiceBox.Entry { Value = 0, Text = L.T("Todos os jogos", "Every game") });
    if (gameDir != null) scope.Items.Add(new ChoiceBox.Entry { Value = 1, Text = L.T("Só este jogo: ", "This game only: ") + cfg.Game });
    AddRow(L.T("Configurações de", "Settings for"),
        L.T("As do jogo valem por cima das gerais, opção por opção.", "A game's own settings apply over the general ones, option by option."), scope);

    // Controllers
    NewCard(L.T("Controles", "Controllers"));
    var status = Words(L.T("Procurando controles...", "Looking for controllers..."), Look.UI(), 9.5, Look.Text2, new Thickness(0, 6));
    card.Children.Add(status);
    var player = new ChoiceBox();
    var connected = new List<Controller>();
    player.SelectedIndexChanged += (o, e) => {
      if (loading || player.Selected == null || player.Selected.Value == -2) return;   // -2: the saved choice, kept
      InputSettings.Put("controller", player.Selected.Value < 0 ? "auto" : connected[player.Selected.Value].Guid, scopeDir);
    };
    AddRow(L.T("Jogador 1", "Player 1"),
        L.T("Automático: o primeiro conectado. Os outros seguem como jogadores 2, 3...", "Automatic: the first one connected. The others follow as players 2, 3..."), player);
    var refresh = new FlatButton(L.T("Atualizar lista", "Refresh list"), ButtonKind.Secondary, 160, 34);
    AddRow(L.T("Controles conectados", "Connected controllers"), null, refresh);

    // Remap
    NewCard(L.T("Mapeamento de botões", "Button mapping"));
    var remapPad = new ChoiceBox();
    AddRow(L.T("Controle", "Controller"),
        L.T("Clique em Mudar e aperte o botão (ou mova o eixo) do controle. Fica salvo para esse modelo de controle.",
            "Click Change, then press the button (or move the axis) on the controller. It is saved for that controller model."), remapPad);
    var remapList = new StackPanel();
    card.Children.Add(remapList);
    var reset = new FlatButton(L.T("Restaurar padrão", "Restore default"), ButtonKind.Secondary, 160, 34);
    AddRow(L.T("Mapeamento deste controle", "This controller's mapping"), null, reset);

    Remap remap = null;
    Controller remapping = null;
    Sdl.Capture capture = null;
    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
    DateTime deadline = DateTime.MinValue;
    string waitingFor = null;
    Action fillRemap = null;
    string MappingKey(Controller c) => "mapping." + c.Guid;

    void StopCapture() { timer.Stop(); capture?.Dispose(); capture = null; }
    timer.Tick += (o, e) => {
      var got = capture?.Poll();
      if (got == null && DateTime.Now < deadline) return;
      string target = waitingFor;
      StopCapture();
      if (got != null && target != null && remap != null && remapping != null) {
        remap.Set(target, got);
        string m = remap.ToMapping(remapping);
        InputSettings.Put(MappingKey(remapping), m, scopeDir);
        Sdl.AddMapping(m);
      }
      fillRemap();
    };

    fillRemap = () => {
      remapList.Children.Clear();
      remapping = remapPad.Selected != null && remapPad.Selected.Value >= 0 ? connected[remapPad.Selected.Value] : null;
      reset.IsEnabled = remapping != null;
      if (remapping == null) {
        remapList.Children.Add(Words(L.T("Conecte um controle para mapear os botões.", "Connect a controller to map its buttons."), Look.UI(), 9.5, Look.Text3, new Thickness(0, 6)));
        return;
      }
      string saved = InputSettings.Get(MappingKey(remapping), scopeDir, "");
      remap = Remap.Parse(string.IsNullOrEmpty(saved) ? Sdl.MappingFor(remapping) : saved);
      foreach (string t in Remap.Targets) {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,220,110"), Margin = new Thickness(0, 3) };
        var label = Words(Remap.Label(t), Look.UI(), 10, Look.Text, new Thickness(0, 6, 0, 0));
        var now = Words(remap.Fields.TryGetValue(t, out var src) ? src : L.T("(nenhum)", "(none)"), Look.UI(), 10, Look.Text2, new Thickness(0, 6, 0, 0));
        var change = new FlatButton(L.T("Mudar", "Change"), ButtonKind.Secondary, 100, 30);
        string target = t;
        change.Click += (o, e) => {
          if (capture != null) return;
          capture = new Sdl.Capture(remapping);
          if (!capture.Ok) { StopCapture(); return; }
          waitingFor = target;
          now.Text = L.T("aperte...", "press...");
          now.Foreground = Look.B(Look.Accent);
          deadline = DateTime.Now.AddSeconds(6);
          timer.Start();
        };
        Grid.SetColumn(now, 1); Grid.SetColumn(change, 2);
        row.Children.Add(label); row.Children.Add(now); row.Children.Add(change);
        remapList.Children.Add(row);
      }
    };
    remapPad.SelectedIndexChanged += (o, e) => { if (!loading) { StopCapture(); fillRemap(); } };
    reset.Click += (o, e) => {
      if (remapping == null) return;
      InputSettings.Put(MappingKey(remapping), "", scopeDir);
      fillRemap();
    };

    // Sticks, mouse, wheel, vibration
    NewCard(L.T("Analógicos e gatilhos", "Sticks and triggers"));
    Percent("stick_sensitivity", 100, 10, 300, 5, L.T("Sensibilidade do analógico", "Stick sensitivity"),
        L.T("Acima de 100% chega ao máximo antes do fim do curso.", "Above 100% reaches full tilt before the end of travel."));
    Percent("stick_deadzone", 12, 0, 50, 1, L.T("Zona morta do analógico", "Stick dead zone"),
        L.T("Aumente se o personagem anda sozinho.", "Raise it if the character moves on its own."));
    Percent("trigger_deadzone", 5, 0, 50, 1, L.T("Zona morta dos gatilhos", "Trigger dead zone"), null);

    NewCard(L.T("Mouse", "Mouse"));
    Percent("mouse_sensitivity", 100, 10, 500, 5, L.T("Sensibilidade do mouse", "Mouse sensitivity"),
        L.T("Nos jogos que aceitam mouse para a câmera.", "In games that take the mouse for the camera."));

    NewCard(L.T("Volante", "Wheel"));
    Percent("wheel_sensitivity", 100, 10, 300, 5, L.T("Sensibilidade do volante", "Wheel sensitivity"),
        L.T("Acima de 100% esterça tudo com menos giro.", "Above 100% steers fully with less rotation."));
    Percent("wheel_deadzone", 2, 0, 30, 1, L.T("Zona morta do volante", "Wheel dead zone"), null);
    Axis("wheel_steering_axis", 0, L.T("Eixo da direção", "Steering axis"));
    Axis("wheel_throttle_axis", 2, L.T("Eixo do acelerador", "Throttle axis"));
    Axis("wheel_brake_axis", 3, L.T("Eixo do freio", "Brake axis"));
    Toggle("wheel_pedals_full_at_min", true, L.T("Mínimo = pisado", "Minimum = pressed"), L.T("Máximo = pisado", "Maximum = pressed"),
        L.T("Pedais", "Pedals"), L.T("A maioria dos volantes informa o pedal pisado no valor mínimo.", "Most wheels report a pressed pedal at the minimum value."));

    NewCard(L.T("Vibração", "Vibration"));
    Toggle("vibration", true, L.T("Ligada", "On"), L.T("Desligada", "Off"), L.T("Vibração", "Vibration"), null);
    Percent("vibration_strength", 100, 0, 200, 10, L.T("Força da vibração", "Vibration strength"), null);

    // Loading
    void FillPads() {
      loading = true;
      int keepRemap = remapPad.Selected?.Value ?? 0;
      player.Items.Clear(); remapPad.Items.Clear();
      player.Items.Add(new ChoiceBox.Entry { Value = -1, Text = L.T("Automático", "Automatic") });
      for (int i = 0; i < connected.Count; ++i) {
        player.Items.Add(new ChoiceBox.Entry { Value = i, Text = connected[i].ToString() });
        remapPad.Items.Add(new ChoiceBox.Entry { Value = i, Text = connected[i].ToString() });
      }
      string chosen = InputSettings.Get("controller", scopeDir, "auto");
      int pick = connected.FindIndex(c => string.Equals(c.Guid, chosen, StringComparison.OrdinalIgnoreCase));
      if (pick < 0 && chosen != "auto")
        player.Items.Add(new ChoiceBox.Entry { Value = -2, Text = L.T("Escolhido antes (desconectado)", "Chosen before (disconnected)") });
      player.Select(pick >= 0 ? pick : chosen == "auto" ? -1 : -2, -1);
      remapPad.Select(keepRemap, 0);
      player.InvalidateVisual(); remapPad.InvalidateVisual();
      loading = false;
      status.Text = connected.Count == 0
          ? L.T("Nenhum controle conectado.", "No controller connected.")
          : string.Join("\n", connected.Select((c, i) => (i + 1) + ". " + c));
      fillRemap();
    }
    void ReadPads() {
      foreach (var c in Sdl.List()) {   // the player's remaps, global then this game's
        Sdl.AddMapping(Ini.GetText(InputSettings.GlobalFile, InputSettings.Section, "mapping." + c.Guid));
        if (gameDir != null) Sdl.AddMapping(Ini.GetText(InputSettings.GameFile(gameDir), InputSettings.Section, "mapping." + c.Guid));
      }
      connected = Sdl.List();
      FillPads();
    }
    void LoadScope() {
      loading = true;
      foreach (var l in loaders) l();
      loading = false;
      FillPads();
    }
    scope.SelectedIndexChanged += (o, e) => { if (scope.Selected == null) return; StopCapture(); scopeDir = scope.Selected.Value == 1 ? gameDir : null; LoadScope(); };
    refresh.Click += (o, e) => { if (Sdl.Start()) ReadPads(); };

    var dock = new DockPanel { Margin = new Thickness(26, 20, 26, 18) };
    dock.Children.Add(StyledWindow.Scroller(stack));
    w.Content = dock;
    w.Closed += (o, e) => StopCapture();
    loading = true;
    scope.SelectedIndex = 0;
    loading = false;
    LoadScope();
    w.Opened += async (o, e) => {
      bool ok = await Task.Run(() => Sdl.Start());
      if (ok) ReadPads();
      else status.Text = L.T("Não foi possível listar os controles: ", "Could not list the controllers: ") + Sdl.Error;
    };
    return w;
  }
}

}
