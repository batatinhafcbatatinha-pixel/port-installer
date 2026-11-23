// The Controllers window: which controller is player 1, remaps, dead zones, sensitivities (stick, mouse, wheel) and
// vibration, for every game or for the selected one. It writes input.ini, which port_input (the input layer every
// recompiler's runtime shares) reads when the game starts.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PortLauncher {

sealed partial class MainForm {
  void ShowControllers() {
    using (var dlg = OpenControllersDialog()) dlg.ShowDialog(this);
  }

  Form OpenControllersDialog() {
    string gameDir = root;
    string scopeDir = null;           // null: every game (the global file)
    bool loading = false;
    var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(26, 20, 26, 18) };
    var stack = Stack(Theme.Bg);
    var loaders = new List<Action>();  // re-read every editor for the current scope

    stack.Controls.Add(Words(L.T(
        "Valem para os jogos de todos os recompiladores (PS2, PSP, Xbox, Xbox 360) na próxima vez que abrirem. " +
        "Controles de Xbox, PlayStation, Nintendo, genéricos e volantes são reconhecidos pelo SDL3.",
        "They apply to the games of every recompiler (PS2, PSP, Xbox, Xbox 360) the next time they start. " +
        "Xbox, PlayStation, Nintendo, generic controllers and wheels are recognized through SDL3."),
        Theme.UI(9.5f), Theme.Text3, new Padding(4, 0, 4, 14)));

    // Rows ------------------------------------------------------------------------------------------------------
    TableLayoutPanel card = null;
    bool firstRow = true;
    void NewCard(string title) {
      card = AddCard(stack, out _);
      card.Controls.Add(Words(title.ToUpperInvariant(), Theme.Semi(9.5f), Theme.Accent, new Padding(0, 0, 0, 4)));
      firstRow = true;
    }
    void AddRow(string title, string help, Control editor) {
      var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = new Padding(0, 10, 0, 10) };
      row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
      var left = Stack(Theme.Surface);
      left.Dock = DockStyle.None; left.Anchor = AnchorStyles.Left | AnchorStyles.Right; left.Margin = new Padding(0, 0, 24, 0);
      left.Controls.Add(Words(title, Theme.Semi(10.5f), Theme.Text, new Padding(0, 0, 0, 2)));
      if (!string.IsNullOrEmpty(help)) left.Controls.Add(Words(help, Theme.UI(9f), Theme.Text3, Padding.Empty));
      row.Controls.Add(left, 0, 0);
      editor.Margin = Padding.Empty;
      editor.Anchor = AnchorStyles.Right;
      row.Controls.Add(editor, 1, 0);
      if (!firstRow) card.Controls.Add(new Panel { Height = 1, BackColor = Theme.Border, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = Padding.Empty });
      firstRow = false;
      card.Controls.Add(row);
    }
    void Percent(string key, int def, int min, int max, int step, string title, string help) {
      var s = new Slider { Width = 360 };
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
      var b = new ChoiceBox { Width = 360 };
      for (int i = 0; i < 8; ++i) b.Items.Add(new ChoiceBox.Entry { Value = i, Text = L.T("Eixo ", "Axis ") + i });
      b.SelectedIndexChanged += (o, e) => { if (!loading && b.Selected != null) InputSettings.Put(key, b.Selected.Value, scopeDir); };
      loaders.Add(() => b.Select(InputSettings.GetInt(key, scopeDir, def), def));
      AddRow(title, null, b);
    }

    // Scope -----------------------------------------------------------------------------------------------------
    NewCard(L.T("Onde salvar", "Where to save"));
    var scope = new ChoiceBox { Width = 360 };
    scope.Items.Add(new ChoiceBox.Entry { Value = 0, Text = L.T("Todos os jogos", "Every game") });
    if (gameDir != null) scope.Items.Add(new ChoiceBox.Entry { Value = 1, Text = L.T("Só este jogo: ", "This game only: ") + cfg.Game });
    AddRow(L.T("Configurações de", "Settings for"),
        L.T("As do jogo valem por cima das gerais, opção por opção.", "A game's own settings apply over the general ones, option by option."), scope);

    // Controllers -----------------------------------------------------------------------------------------------
    NewCard(L.T("Controles", "Controllers"));
    var status = Words(L.T("Procurando controles...", "Looking for controllers..."), Theme.UI(9.5f), Theme.Text2, new Padding(0, 6, 0, 6));
    card.Controls.Add(status);
    var player = new ChoiceBox { Width = 360 };
    var connected = new List<Controller>();
    string chosenGuid = "auto";
    player.SelectedIndexChanged += (o, e) => {
      if (loading || player.Selected == null || player.Selected.Value == -2) return;   // -2: the saved choice, kept
      chosenGuid = player.Selected.Value < 0 ? "auto" : connected[player.Selected.Value].Guid;
      InputSettings.Put("controller", chosenGuid, scopeDir);
    };
    AddRow(L.T("Jogador 1", "Player 1"),
        L.T("Automático: o primeiro conectado. Os outros seguem como jogadores 2, 3...", "Automatic: the first one connected. The others follow as players 2, 3..."), player);
    var refresh = new FlatButton(L.T("Atualizar lista", "Refresh list"), ButtonKind.Secondary, 160, 34);
    AddRow(L.T("Controles conectados", "Connected controllers"), null, refresh);

    // Remap -----------------------------------------------------------------------------------------------------
    NewCard(L.T("Mapeamento de botões", "Button mapping"));
    var remapPad = new ChoiceBox { Width = 360 };
    AddRow(L.T("Controle", "Controller"),
        L.T("Clique em Mudar e aperte o botão (ou mova o eixo) do controle. Fica salvo para esse modelo de controle.",
            "Click Change, then press the button (or move the axis) on the controller. It is saved for that controller model."), remapPad);
    var remapList = Stack(Theme.Surface);
    card.Controls.Add(remapList);
    var reset = new FlatButton(L.T("Restaurar padrão", "Restore default"), ButtonKind.Secondary, 160, 34);
    AddRow(L.T("Mapeamento deste controle", "This controller's mapping"), null, reset);

    Remap remap = null;
    Controller remapping = null;
    Sdl.Capture capture = null;
    var timer = new Timer { Interval = 30 };
    DateTime deadline = DateTime.MinValue;
    Label waiting = null;
    Action fillRemap = null;
    string MappingKey(Controller c) => "mapping." + c.Guid;

    void StopCapture() { timer.Stop(); capture?.Dispose(); capture = null; }
    timer.Tick += (o, e) => {
      var got = capture?.Poll();
      if (got == null && DateTime.Now < deadline) return;
      string target = waiting?.Tag as string;
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
      remapList.SuspendLayout();
      remapList.Controls.Clear();
      remapping = remapPad.Selected != null && remapPad.Selected.Value >= 0 ? connected[remapPad.Selected.Value] : null;
      reset.Enabled = remapping != null;
      if (remapping == null) {
        remapList.Controls.Add(Words(L.T("Conecte um controle para mapear os botões.", "Connect a controller to map its buttons."), Theme.UI(9.5f), Theme.Text3, new Padding(0, 6, 0, 6)));
        remapList.ResumeLayout();
        return;
      }
      string saved = InputSettings.Get(MappingKey(remapping), scopeDir, "");
      remap = Remap.Parse(string.IsNullOrEmpty(saved) ? Sdl.MappingFor(remapping) : saved);
      foreach (string t in Remap.Targets) {
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = new Padding(0, 3, 0, 3) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        row.Controls.Add(Words(Remap.Label(t), Theme.UI(10f), Theme.Text, new Padding(0, 6, 0, 0)), 0, 0);
        var now = Words(remap.Fields.TryGetValue(t, out var src) ? src : L.T("(nenhum)", "(none)"), Theme.UI(10f), Theme.Text2, new Padding(0, 6, 0, 0));
        now.Tag = t;
        row.Controls.Add(now, 1, 0);
        var change = new FlatButton(L.T("Mudar", "Change"), ButtonKind.Secondary, 100, 30) { Margin = Padding.Empty };
        change.Click += (o, e) => {
          if (capture != null) return;
          capture = new Sdl.Capture(remapping);
          if (!capture.Ok) { StopCapture(); return; }
          waiting = now;
          now.Text = L.T("aperte...", "press...");
          now.ForeColor = Theme.Accent;
          deadline = DateTime.Now.AddSeconds(6);
          timer.Start();
        };
        row.Controls.Add(change, 2, 0);
        remapList.Controls.Add(row);
      }
      remapList.ResumeLayout();
    };
    remapPad.SelectedIndexChanged += (o, e) => { if (!loading) { StopCapture(); fillRemap(); } };
    reset.Click += (o, e) => {
      if (remapping == null) return;
      InputSettings.Put(MappingKey(remapping), "", scopeDir);
      fillRemap();
    };

    // Sticks, mouse, wheel, vibration -----------------------------------------------------------------------------
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

    // Loading ---------------------------------------------------------------------------------------------------
    void FillPads() {
      loading = true;
      int keepRemap = remapPad.Selected?.Value ?? 0;
      player.Items.Clear(); remapPad.Items.Clear();
      player.Items.Add(new ChoiceBox.Entry { Value = -1, Text = L.T("Automático", "Automatic") });
      for (int i = 0; i < connected.Count; ++i) {
        player.Items.Add(new ChoiceBox.Entry { Value = i, Text = connected[i].ToString() });
        remapPad.Items.Add(new ChoiceBox.Entry { Value = i, Text = connected[i].ToString() });
      }
      chosenGuid = InputSettings.Get("controller", scopeDir, "auto");
      int pick = connected.FindIndex(c => string.Equals(c.Guid, chosenGuid, StringComparison.OrdinalIgnoreCase));
      if (pick < 0 && chosenGuid != "auto")   // chosen before, not connected now: keep it listed
        player.Items.Add(new ChoiceBox.Entry { Value = -2, Text = L.T("Escolhido antes (desconectado)", "Chosen before (disconnected)") });
      player.Select(pick >= 0 ? pick : chosenGuid == "auto" ? -1 : -2, -1);
      remapPad.Select(keepRemap, 0);
      loading = false;
      status.Text = connected.Count == 0
          ? L.T("Nenhum controle conectado.", "No controller connected.")
          : string.Join("\n", connected.Select((c, i) => (i + 1) + ". " + c));
      fillRemap();
    }
    void ReadPads() {
      // The player's remaps, global then this game's, so the list shows what the games will see.
      foreach (var c in Sdl.List()) {
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

    page.Controls.Add(Scroller(stack));
    var dlg = PageDialog(L.T("Controles", "Controllers") + (gameDir != null ? "  ·  " + cfg.Game : ""), page, new Size(940, 720));
    dlg.FormClosed += (o, e) => { StopCapture(); timer.Dispose(); };
    loading = true;
    scope.SelectedIndex = 0;
    loading = false;
    LoadScope();
    dlg.Shown += (o, e) => Task.Run(() => Sdl.Start()).ContinueWith(t => {
      if (dlg.IsDisposed) return;
      dlg.BeginInvoke(new Action(() => {
        if (t.Result) ReadPads();
        else status.Text = L.T("Não foi possível listar os controles: ", "Could not list the controllers: ") + Sdl.Error;
      }));
    });
    return dlg;
  }
}

}
