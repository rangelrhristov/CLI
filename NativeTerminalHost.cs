using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal static class NativeTerminalHost
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        SetupResult setup;
        using (var dialog = new SetupDialog())
        {
            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            setup = dialog.Result;
        }

        Application.Run(new HostForm(setup));
    }
}

internal sealed class SetupResult
{
    public int Count;
    public string Workdir = "C:\\IDE";
}

internal sealed class SetupDialog : Form
{
    private readonly NumericUpDown countBox = new NumericUpDown();
    private readonly TextBox workdirBox = new TextBox();

    public SetupResult Result { get; private set; }

    public SetupDialog()
    {
        Text = "Codex Terminal Host";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Color.FromArgb(8, 9, 10);
        ForeColor = Color.White;
        Size = new Size(520, 235);

        var title = new Label
        {
            Text = "Actual Windows Terminal Host",
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(22, 20)
        };
        Controls.Add(title);

        Controls.Add(new Label { Text = "How many terminals?", AutoSize = true, Location = new Point(24, 76) });
        countBox.Minimum = 1;
        countBox.Maximum = 16;
        countBox.Value = 4;
        countBox.Location = new Point(210, 72);
        countBox.Width = 70;
        Controls.Add(countBox);

        Controls.Add(new Label { Text = "Workdir", AutoSize = true, Location = new Point(24, 114) });
        workdirBox.Text = "C:\\IDE";
        workdirBox.Location = new Point(94, 110);
        workdirBox.Width = 370;
        Controls.Add(workdirBox);

        var open = new Button { Text = "Open contained terminals", Width = 210, Height = 36, Location = new Point(24, 154) };
        open.Click += delegate
        {
            Result = new SetupResult
            {
                Count = (int)countBox.Value,
                Workdir = string.IsNullOrWhiteSpace(workdirBox.Text) ? "C:\\IDE" : workdirBox.Text.Trim()
            };
            DialogResult = DialogResult.OK;
        };
        Controls.Add(open);

        var cancel = new Button { Text = "Cancel", Width = 90, Height = 36, Location = new Point(386, 154) };
        cancel.Click += delegate { DialogResult = DialogResult.Cancel; };
        Controls.Add(cancel);

        AcceptButton = open;
        CancelButton = cancel;
    }
}

internal sealed class HostForm : Form
{
    private const int TopBarHeight = 34;
    private const int ResizeBorder = 7;
    private const int SnapDistance = 14;
    private readonly SetupResult setup;
    private readonly List<EmbeddedTerminal> terminals = new List<EmbeddedTerminal>();
    private readonly string wtPath;
    private readonly TableLayoutPanel grid = new TableLayoutPanel();
    private readonly TextBox dictationBox = new TextBox();
    private readonly Label inputStatus = new Label();
    private readonly System.Windows.Forms.Timer focusGuardTimer = new System.Windows.Forms.Timer();
    private bool forwardingDictationText;
    private KeyboardForwarder keyboardForwarder;
    private MousePaneActivator mousePaneActivator;
    private EmbeddedTerminal activeTerminal;
    private WindowMouseMode windowMouseMode = WindowMouseMode.None;
    private Point windowMouseStart;
    private Rectangle windowStartBounds;
    private int resizeEdge;
    private int layoutMode;

    public HostForm(SetupResult setup)
    {
        this.setup = setup;
        wtPath = WindowsTerminalLocator.Find();

        Text = "CLI";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        ForeColor = Color.White;
        MinimumSize = FitMinimumToWorkingArea(new Size(720, 420));
        Size = FitToWorkingArea(new Size(1450, 940));
        KeyPreview = true;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black,
            RowCount = 2,
            ColumnCount = 1
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, TopBarHeight));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var topBar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(7, 8, 9),
            Cursor = Cursors.SizeAll
        };
        topBar.MouseDown += delegate { BeginWindowMouseOperation(Cursor.Position); };
        topBar.MouseMove += delegate { ContinueWindowMouseOperation(Cursor.Position); };
        topBar.MouseUp += delegate { EndWindowMouseOperation(); };
        topBar.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleMaximized();
            }
        };
        root.Controls.Add(topBar, 0, 0);

        var brand = new Label
        {
            Text = "FD CLI",
            AutoSize = true,
            ForeColor = Color.FromArgb(18, 214, 231),
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Location = new Point(12, 8)
        };
        brand.MouseDown += delegate { BeginWindowMouseOperation(Cursor.Position); };
        brand.MouseMove += delegate { ContinueWindowMouseOperation(Cursor.Position); };
        brand.MouseUp += delegate { EndWindowMouseOperation(); };
        brand.MouseDoubleClick += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleMaximized();
            }
        };
        topBar.Controls.Add(brand);

        AddTopButton(topBar, "+", 74, delegate { AddTerminalFromPrompt(); });
        AddTopButton(topBar, "name", 110, delegate { RenameActiveTerminal(); });
        AddTopButton(topBar, "restart", 174, delegate { RestartActiveTerminal(); });
        AddTopButton(topBar, "stop", 240, delegate { StopActiveTerminal(); });
        AddTopButton(topBar, "layout", 292, delegate { CycleLayout(); });

        dictationBox.BorderStyle = BorderStyle.None;
        dictationBox.BackColor = Color.FromArgb(3, 4, 5);
        dictationBox.ForeColor = Color.FromArgb(3, 4, 5);
        dictationBox.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        dictationBox.Location = new Point(-2000, -2000);
        dictationBox.Size = new Size(4, 4);
        dictationBox.TabStop = true;
        dictationBox.TextChanged += delegate { ForwardDictationText(); };
        dictationBox.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (HandleDictationControlKey(e.KeyCode))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        topBar.Controls.Add(dictationBox);

        inputStatus.Text = "input -> Codex 1";
        inputStatus.AutoSize = false;
        inputStatus.TextAlign = ContentAlignment.MiddleRight;
        inputStatus.ForeColor = Color.FromArgb(96, 120, 124);
        inputStatus.BackColor = Color.Transparent;
        inputStatus.Font = new Font("Segoe UI", 8, FontStyle.Regular);
        inputStatus.Location = new Point(368, 7);
        inputStatus.Size = new Size(250, 18);
        inputStatus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        topBar.Resize += delegate
        {
            inputStatus.Left = Math.Max(368, topBar.ClientSize.Width - inputStatus.Width - 12);
        };
        topBar.Controls.Add(inputStatus);

        focusGuardTimer.Interval = 250;
        focusGuardTimer.Tick += delegate { MaintainDictationFocus(); };
        focusGuardTimer.Start();

        grid.Dock = DockStyle.Fill;
        grid.BackColor = Color.Black;
        grid.Padding = new Padding(6);
        root.Controls.Add(grid, 0, 1);

        for (var i = 0; i < setup.Count; i++)
        {
            terminals.Add(CreateTerminal("Codex " + (i + 1)));
        }
        RenderLayout();

        Shown += delegate
        {
            foreach (var terminal in terminals)
            {
                terminal.StartAsync();
            }
        };
        FormClosing += delegate
        {
            if (keyboardForwarder != null)
            {
                keyboardForwarder.Dispose();
                keyboardForwarder = null;
            }
            if (mousePaneActivator != null)
            {
                mousePaneActivator.Dispose();
                mousePaneActivator = null;
            }
            focusGuardTimer.Stop();
            foreach (var terminal in terminals)
            {
                terminal.Close();
            }
        };
        keyboardForwarder = new KeyboardForwarder(this, terminals);
        mousePaneActivator = new MousePaneActivator(this, terminals);
        MarkInitialTerminalActive();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        FocusDictationInput();
    }

    private void ToggleMaximized()
    {
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    private static Size FitToWorkingArea(Size preferred)
    {
        var area = Screen.PrimaryScreen.WorkingArea;
        var width = Math.Min(preferred.Width, Math.Max(320, area.Width - 24));
        var height = Math.Min(preferred.Height, Math.Max(240, area.Height - 24));
        return new Size(width, height);
    }

    private static Size FitMinimumToWorkingArea(Size preferred)
    {
        var area = Screen.PrimaryScreen.WorkingArea;
        var width = Math.Min(preferred.Width, Math.Max(320, area.Width - 24));
        var height = Math.Min(preferred.Height, Math.Max(240, area.Height - 24));
        return new Size(width, height);
    }

    private void SnapToScreenEdge()
    {
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        var cursor = Cursor.Position;

        if (cursor.Y <= screen.Top + SnapDistance)
        {
            WindowState = FormWindowState.Maximized;
            return;
        }

        if (cursor.X <= screen.Left + SnapDistance)
        {
            WindowState = FormWindowState.Normal;
            Bounds = new Rectangle(screen.Left, screen.Top, screen.Width / 2, screen.Height);
            return;
        }

        if (cursor.X >= screen.Right - SnapDistance)
        {
            WindowState = FormWindowState.Normal;
            Bounds = new Rectangle(screen.Left + (screen.Width / 2), screen.Top, screen.Width - (screen.Width / 2), screen.Height);
        }
    }

    public bool HandleWindowMouseHook(int message, Point screenPoint)
    {
        if (IsDisposed)
        {
            return false;
        }

        if (message == NativeMethods.WM_LBUTTONDOWN)
        {
            return BeginWindowMouseOperation(screenPoint);
        }

        if (message == NativeMethods.WM_MOUSEMOVE)
        {
            return ContinueWindowMouseOperation(screenPoint);
        }

        if (message == NativeMethods.WM_LBUTTONUP)
        {
            return EndWindowMouseOperation();
        }

        return false;
    }

    private bool BeginWindowMouseOperation(Point screenPoint)
    {
        if (!RectangleToScreen(ClientRectangle).Contains(screenPoint))
        {
            return false;
        }

        var client = PointToClient(screenPoint);
        var edge = GetResizeEdge(client);
        if (edge != NativeMethods.HTCLIENT)
        {
            windowMouseMode = WindowMouseMode.Resize;
            resizeEdge = edge;
            windowMouseStart = screenPoint;
            windowStartBounds = Bounds;
            return true;
        }

        if (client.Y < TopBarHeight && !IsTopButtonPoint(client))
        {
            if (WindowState == FormWindowState.Maximized)
            {
                WindowState = FormWindowState.Normal;
            }

            windowMouseMode = WindowMouseMode.Drag;
            windowMouseStart = screenPoint;
            windowStartBounds = Bounds;
            return true;
        }

        return false;
    }

    private bool ContinueWindowMouseOperation(Point screenPoint)
    {
        if (windowMouseMode == WindowMouseMode.None)
        {
            return false;
        }

        var dx = screenPoint.X - windowMouseStart.X;
        var dy = screenPoint.Y - windowMouseStart.Y;

        if (windowMouseMode == WindowMouseMode.Drag)
        {
            Bounds = new Rectangle(windowStartBounds.Left + dx, windowStartBounds.Top + dy, windowStartBounds.Width, windowStartBounds.Height);
            return true;
        }

        var bounds = windowStartBounds;
        if (resizeEdge == NativeMethods.HTLEFT || resizeEdge == NativeMethods.HTTOPLEFT || resizeEdge == NativeMethods.HTBOTTOMLEFT)
        {
            bounds.X += dx;
            bounds.Width -= dx;
        }
        if (resizeEdge == NativeMethods.HTRIGHT || resizeEdge == NativeMethods.HTTOPRIGHT || resizeEdge == NativeMethods.HTBOTTOMRIGHT)
        {
            bounds.Width += dx;
        }
        if (resizeEdge == NativeMethods.HTTOP || resizeEdge == NativeMethods.HTTOPLEFT || resizeEdge == NativeMethods.HTTOPRIGHT)
        {
            bounds.Y += dy;
            bounds.Height -= dy;
        }
        if (resizeEdge == NativeMethods.HTBOTTOM || resizeEdge == NativeMethods.HTBOTTOMLEFT || resizeEdge == NativeMethods.HTBOTTOMRIGHT)
        {
            bounds.Height += dy;
        }

        if (bounds.Width < MinimumSize.Width) bounds.Width = MinimumSize.Width;
        if (bounds.Height < MinimumSize.Height) bounds.Height = MinimumSize.Height;
        Bounds = bounds;
        return true;
    }

    private bool EndWindowMouseOperation()
    {
        if (windowMouseMode == WindowMouseMode.None)
        {
            return false;
        }

        var mode = windowMouseMode;
        windowMouseMode = WindowMouseMode.None;
        resizeEdge = NativeMethods.HTCLIENT;
        if (mode == WindowMouseMode.Drag)
        {
            SnapToScreenEdge();
        }
        return true;
    }

    private int GetResizeEdge(Point client)
    {
        var left = client.X <= ResizeBorder;
        var right = client.X >= ClientSize.Width - ResizeBorder;
        var top = client.Y <= ResizeBorder;
        var bottom = client.Y >= ClientSize.Height - ResizeBorder;

        if (top && left) return NativeMethods.HTTOPLEFT;
        if (top && right) return NativeMethods.HTTOPRIGHT;
        if (bottom && left) return NativeMethods.HTBOTTOMLEFT;
        if (bottom && right) return NativeMethods.HTBOTTOMRIGHT;
        if (left) return NativeMethods.HTLEFT;
        if (right) return NativeMethods.HTRIGHT;
        if (top) return NativeMethods.HTTOP;
        if (bottom) return NativeMethods.HTBOTTOM;
        return NativeMethods.HTCLIENT;
    }

    private static bool IsTopButtonPoint(Point client)
    {
        return client.Y >= 3 && client.Y <= 31 && client.X >= 68 && client.X <= 368;
    }

    private static Button AddTopButton(Control parent, string text, int x, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            Width = text.Length > 2 ? 58 : 28,
            Height = 24,
            Location = new Point(x, 5),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(17, 19, 21),
            ForeColor = Color.White,
            TabStop = false
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(55, 62, 68);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(24, 31, 34);
        button.Click += click;
        parent.Controls.Add(button);
        return button;
    }

    private EmbeddedTerminal CreateTerminal(string name)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Margin = new Padding(5) };
        return new EmbeddedTerminal(panel, name, setup.Workdir, wtPath, SetActiveTerminal);
    }

    private void RenderLayout()
    {
        grid.SuspendLayout();
        grid.Controls.Clear();
        grid.ColumnStyles.Clear();
        grid.RowStyles.Clear();

        var count = Math.Max(1, terminals.Count);
        if (layoutMode == 1)
        {
            ApplyHorizontalGrid(count);
        }
        else
        {
            ApplyBalancedGrid(count);
        }

        grid.ResumeLayout(true);
    }

    private void ApplySimpleGrid(int columns, int rows)
    {
        grid.ColumnCount = columns;
        grid.RowCount = rows;
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        }
        for (var r = 0; r < rows; r++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
        }
        for (var i = 0; i < terminals.Count; i++)
        {
            grid.Controls.Add(terminals[i].HostPanel, i % columns, i / columns);
        }
    }

    private void ApplyBalancedGrid(int count)
    {
        var bestColumns = 1;
        var bestRows = count;
        var bestScore = double.MaxValue;
        const double targetAspect = 1.6;

        for (var columns = 1; columns <= count; columns++)
        {
            var rows = (int)Math.Ceiling(count / (double)columns);
            var emptyCells = (columns * rows) - count;
            var aspect = columns / (double)rows;
            var score = Math.Abs(aspect - targetAspect) + (emptyCells * 0.3);
            if (score < bestScore)
            {
                bestScore = score;
                bestColumns = columns;
                bestRows = rows;
            }
        }

        ApplySimpleGrid(bestColumns, bestRows);
    }

    private void ApplyHorizontalGrid(int count)
    {
        var columns = Math.Min(count, 2);
        var rows = (int)Math.Ceiling(count / (double)columns);
        ApplySimpleGrid(columns, rows);
    }

    private void AddTerminalFromPrompt()
    {
        var defaultName = "Codex " + (terminals.Count + 1);
        var terminal = CreateTerminal(defaultName);
        terminals.Add(terminal);
        RenderLayout();
        SetActiveTerminal(terminal);
        terminal.StartAsync();
    }

    private void RenameActiveTerminal()
    {
        var terminal = ActiveTerminal;
        if (terminal == null)
        {
            return;
        }

        terminal.BeginRename();
    }

    private void RestartActiveTerminal()
    {
        var terminal = ActiveTerminal;
        if (terminal == null)
        {
            return;
        }

        terminal.RestartAsync();
    }

    private void StopActiveTerminal()
    {
        var terminal = ActiveTerminal;
        if (terminal == null)
        {
            return;
        }

        terminal.Close();
    }

    private void CycleLayout()
    {
        layoutMode = (layoutMode + 1) % 2;
        RenderLayout();
    }

    public void SetActiveTerminal(EmbeddedTerminal terminal)
    {
        if (terminal == null)
        {
            return;
        }

        activeTerminal = terminal;
        foreach (var item in terminals)
        {
            item.SetActive(item == activeTerminal);
        }
        UpdateInputStatus();
        FocusDictationInput();
    }

    private void MarkInitialTerminalActive()
    {
        activeTerminal = terminals.FirstOrDefault();
        foreach (var item in terminals)
        {
            item.SetActive(item == activeTerminal);
        }
        UpdateInputStatus();
        FocusDictationInput();
    }

    public EmbeddedTerminal ActiveTerminal
    {
        get { return activeTerminal ?? terminals.FirstOrDefault(); }
    }

    public EmbeddedTerminal TerminalAtScreenPoint(Point point)
    {
        foreach (var terminal in terminals)
        {
            if (terminal.ContainsScreenPoint(point))
            {
                return terminal;
            }
        }
        return null;
    }

    public bool IsPointActuallyOnHost(Point screenPoint)
    {
        var window = NativeMethods.WindowFromPoint(screenPoint);
        return window == Handle || NativeMethods.IsChild(Handle, window);
    }

    public bool IsForegroundActuallyHost()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        return foreground == Handle || NativeMethods.IsChild(Handle, foreground);
    }

    public void FocusDictationInput()
    {
        if (!dictationBox.IsDisposed && !dictationBox.Focused && !IsRenamingPane())
        {
            dictationBox.Focus();
        }
    }

    private void MaintainDictationFocus()
    {
        if (IsDisposed || !IsForegroundActuallyHost() || IsRenamingPane())
        {
            return;
        }

        if (!dictationBox.Focused)
        {
            dictationBox.Focus();
        }
    }

    private bool IsRenamingPane()
    {
        return terminals.Any(terminal => terminal.IsRenaming);
    }

    private void UpdateInputStatus()
    {
        var terminal = ActiveTerminal;
        inputStatus.Text = terminal == null ? "input ready" : "input -> " + terminal.DisplayName;
    }

    public bool IsDictationInputHandle(IntPtr hwnd)
    {
        return hwnd == dictationBox.Handle || NativeMethods.IsChild(dictationBox.Handle, hwnd);
    }

    public bool IsDictationInputControl(Control control)
    {
        return control == dictationBox;
    }

    private void ForwardDictationText()
    {
        if (forwardingDictationText || string.IsNullOrEmpty(dictationBox.Text))
        {
            return;
        }

        var text = dictationBox.Text;
        forwardingDictationText = true;
        try
        {
            dictationBox.Clear();
        }
        finally
        {
            forwardingDictationText = false;
        }

        var terminal = ActiveTerminal;
        if (terminal != null)
        {
            terminal.ForwardText(text);
        }
    }

    private bool HandleDictationControlKey(Keys key)
    {
        var terminal = ActiveTerminal;
        if (terminal == null)
        {
            return false;
        }

        switch (key)
        {
            case Keys.Enter:
                terminal.ForwardKey(NativeMethods.VK_RETURN);
                return true;
            case Keys.Back:
                terminal.ForwardKey(NativeMethods.VK_BACK);
                return true;
            case Keys.Escape:
                terminal.ForwardKey(NativeMethods.VK_ESCAPE);
                return true;
            case Keys.Left:
                terminal.ForwardKey(NativeMethods.VK_LEFT);
                return true;
            case Keys.Right:
                terminal.ForwardKey(NativeMethods.VK_RIGHT);
                return true;
            case Keys.Up:
                terminal.ForwardKey(NativeMethods.VK_UP);
                return true;
            case Keys.Down:
                terminal.ForwardKey(NativeMethods.VK_DOWN);
                return true;
            case Keys.Delete:
                terminal.ForwardKey(NativeMethods.VK_DELETE);
                return true;
            case Keys.Home:
                terminal.ForwardKey(NativeMethods.VK_HOME);
                return true;
            case Keys.End:
                terminal.ForwardKey(NativeMethods.VK_END);
                return true;
            default:
                return false;
        }
    }
}

internal enum WindowMouseMode
{
    None,
    Drag,
    Resize
}

internal sealed class EmbeddedTerminal
{
    private const int TerminalChromeCrop = 48;
    private const int BorderSize = 3;
    private const int HeaderHeight = 22;
    private static readonly Color ActiveBorder = Color.FromArgb(18, 214, 231);
    private static readonly Color IdleBorder = Color.FromArgb(36, 43, 46);
    private static readonly Color UserBorder = Color.FromArgb(70, 166, 255);
    private static readonly Color CodexBorder = Color.FromArgb(242, 201, 95);
    private static readonly Color AmberBorder = Color.FromArgb(242, 201, 95);
    private readonly Panel host;
    private readonly Panel headerStrip = new Panel();
    private readonly Panel terminalSurface = new Panel();
    private readonly Label nameLabel = new Label();
    private readonly TextBox nameEditor = new TextBox();
    private readonly Label statusLabel = new Label();
    private readonly Action<EmbeddedTerminal> activate;
    private string name;
    private readonly string workdir;
    private readonly string wtPath;
    private IntPtr hwnd = IntPtr.Zero;
    private readonly string uniqueTitle;
    private readonly System.Windows.Forms.Timer flashTimer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer completionTimer = new System.Windows.Forms.Timer();
    private readonly System.Windows.Forms.Timer typingTimer = new System.Windows.Forms.Timer();
    private Color statusBorder = IdleBorder;
    private Process process;
    private int rootProcessId;
    private bool isActive;
    private bool pendingCompletion;
    private DateTime taskStartedAt;
    private DateTime lastKeyboardAt;
    private DateTime lastCpuMovedAt;
    private TimeSpan lastCpu = TimeSpan.MinValue;
    private int flashTicks;

    public EmbeddedTerminal(Panel host, string name, string workdir, string wtPath, Action<EmbeddedTerminal> activate)
    {
        this.host = host;
        this.name = name;
        this.workdir = workdir;
        this.wtPath = wtPath;
        this.activate = activate;
        uniqueTitle = name + " - Codex - " + Guid.NewGuid().ToString("N").Substring(0, 8);
        host.Resize += delegate { ResizeWindow(); };
        host.MouseDown += delegate { Activate(); };
        host.Click += delegate { Activate(); };
        host.BackColor = IdleBorder;

        headerStrip.BackColor = Color.FromArgb(7, 8, 9);
        headerStrip.Location = new Point(BorderSize, BorderSize);
        headerStrip.Size = new Size(1, HeaderHeight);
        headerStrip.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        headerStrip.MouseDown += delegate { Activate(); };
        headerStrip.Click += delegate { Activate(); };
        host.Controls.Add(headerStrip);

        terminalSurface.BackColor = Color.Black;
        terminalSurface.Location = new Point(BorderSize, HeaderHeight + BorderSize);
        terminalSurface.Size = new Size(1, 1);
        terminalSurface.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        terminalSurface.MouseDown += delegate { Activate(); };
        terminalSurface.Click += delegate { Activate(); };
        host.Controls.Add(terminalSurface);

        nameLabel.Text = name;
        nameLabel.AutoEllipsis = true;
        nameLabel.ForeColor = Color.White;
        nameLabel.BackColor = Color.Transparent;
        nameLabel.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        nameLabel.Location = new Point(6, 3);
        nameLabel.Size = new Size(190, 16);
        nameLabel.MouseDown += delegate { Activate(); };
        nameLabel.DoubleClick += delegate { BeginInlineRename(); };
        headerStrip.Controls.Add(nameLabel);

        nameEditor.Visible = false;
        nameEditor.BorderStyle = BorderStyle.None;
        nameEditor.BackColor = Color.FromArgb(17, 19, 21);
        nameEditor.ForeColor = Color.White;
        nameEditor.Font = new Font("Segoe UI", 8, FontStyle.Bold);
        nameEditor.Location = new Point(5, 3);
        nameEditor.Size = new Size(190, 16);
        nameEditor.KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                CommitInlineRename();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                CancelInlineRename();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        nameEditor.Leave += delegate { CommitInlineRename(); };
        headerStrip.Controls.Add(nameEditor);

        statusLabel.Text = "starting";
        statusLabel.AutoEllipsis = true;
        statusLabel.TextAlign = ContentAlignment.TopRight;
        statusLabel.ForeColor = Color.FromArgb(145, 156, 160);
        statusLabel.BackColor = Color.Transparent;
        statusLabel.Font = new Font("Segoe UI", 8, FontStyle.Regular);
        statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        statusLabel.Location = new Point(Math.Max(8, headerStrip.Width - 118), 3);
        statusLabel.Size = new Size(110, 16);
        statusLabel.MouseDown += delegate { Activate(); };
        headerStrip.Controls.Add(statusLabel);

        flashTimer.Interval = 180;
        flashTimer.Tick += delegate { FlashStep(); };
        completionTimer.Interval = 750;
        completionTimer.Tick += delegate { CheckCompletion(); };
        typingTimer.Interval = 1500;
        typingTimer.Tick += delegate
        {
            typingTimer.Stop();
            if (!pendingCompletion)
            {
                SetStatus("ready");
            }
        };
    }

    public Panel HostPanel
    {
        get { return host; }
    }

    public string DisplayName
    {
        get { return name; }
    }

    public bool IsRenaming
    {
        get { return nameEditor.Visible || nameEditor.Focused; }
    }

    public void StartAsync()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            Start();
        });
    }

    private void Start()
    {
        BeginOnUi(delegate { SetStatus("starting"); });
        var codexPath = CodexLocator.Find();
        var command = "$Host.UI.RawUI.WindowTitle = " + PsQuote(uniqueTitle) + "; Set-Location -LiteralPath " + PsQuote(workdir) + "; & " + PsQuote(codexPath);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var args = BuildWindowsTerminalArgs(uniqueTitle, encodedCommand);
        process = Process.Start(new ProcessStartInfo
        {
            FileName = wtPath,
            Arguments = args,
            UseShellExecute = false
        });

        hwnd = WaitForProcessWindow(process, uniqueTitle, TimeSpan.FromSeconds(15));
        if (hwnd == IntPtr.Zero)
        {
            BeginOnUi(delegate
            {
                MessageBox.Show("Could not find the Windows Terminal window for " + name + ". It may have opened separately.", "Terminal embed failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            });
            return;
        }

        BeginOnUi(delegate
        {
            NativeMethods.SetParent(hwnd, terminalSurface.Handle);
            uint windowProcessId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out windowProcessId);
            rootProcessId = windowProcessId == 0 && process != null ? process.Id : (int)windowProcessId;
            var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
            style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_POPUP);
            style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_CLIPCHILDREN | NativeMethods.WS_CLIPSIBLINGS;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_STYLE, style);
            ResizeWindow();
            SetStatus("ready");
            headerStrip.BringToFront();
        });
    }

    public void Close()
    {
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            hwnd = IntPtr.Zero;
        }
        pendingCompletion = false;
        completionTimer.Stop();
        typingTimer.Stop();
        flashTimer.Stop();
        SetStatus("stopped");
    }

    public void RestartAsync()
    {
        Close();
        ThreadPool.QueueUserWorkItem(delegate
        {
            Thread.Sleep(650);
            Start();
        });
    }

    public void SetDisplayName(string value)
    {
        name = value;
        nameLabel.Text = name;
    }

    public void BeginRename()
    {
        BeginInlineRename();
    }

    private void BeginInlineRename()
    {
        Activate();
        nameEditor.Text = name;
        nameEditor.Bounds = nameLabel.Bounds;
        nameEditor.Visible = true;
        nameEditor.BringToFront();
        nameEditor.Focus();
        nameEditor.SelectAll();
    }

    private void CommitInlineRename()
    {
        if (!nameEditor.Visible)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(nameEditor.Text))
        {
            SetDisplayName(nameEditor.Text.Trim());
        }
        nameEditor.Visible = false;
    }

    private void CancelInlineRename()
    {
        nameEditor.Visible = false;
    }

    public void SetActive(bool value)
    {
        isActive = value;
        if (!flashTimer.Enabled)
        {
            UpdateBorder();
        }
    }

    private void Activate()
    {
        if (activate != null)
        {
            activate(this);
            return;
        }
        FocusTerminal();
    }

    private void ResizeWindow()
    {
        headerStrip.Bounds = new Rectangle(
            BorderSize,
            BorderSize,
            Math.Max(1, host.ClientSize.Width - (BorderSize * 2)),
            HeaderHeight);
        statusLabel.Location = new Point(Math.Max(8, headerStrip.ClientSize.Width - 118), 3);
        nameLabel.Width = Math.Max(60, headerStrip.ClientSize.Width - 128);
        nameEditor.Width = nameLabel.Width;
        terminalSurface.Bounds = new Rectangle(
            BorderSize,
            HeaderHeight + BorderSize,
            Math.Max(1, host.ClientSize.Width - (BorderSize * 2)),
            Math.Max(1, host.ClientSize.Height - HeaderHeight - (BorderSize * 2)));

        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.MoveWindow(
                hwnd,
                0,
                -TerminalChromeCrop,
                Math.Max(1, terminalSurface.ClientSize.Width),
                Math.Max(1, terminalSurface.ClientSize.Height + TerminalChromeCrop),
                true);
            headerStrip.BringToFront();
        }
    }

    public void FocusTerminal()
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var target = NativeMethods.FindFocusableDescendant(hwnd);
        if (target == IntPtr.Zero)
        {
            target = hwnd;
        }

        uint targetProcessId;
        var targetThreadId = NativeMethods.GetWindowThreadProcessId(target, out targetProcessId);
        var currentThreadId = NativeMethods.GetCurrentThreadId();

        NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, true);
        try
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            NativeMethods.SetFocus(target);
        }
        finally
        {
            NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, false);
        }
    }

    public bool ContainsScreenPoint(Point point)
    {
        return host.RectangleToScreen(host.ClientRectangle).Contains(point);
    }

    public bool HeaderContainsScreenPoint(Point point)
    {
        return headerStrip.RectangleToScreen(headerStrip.ClientRectangle).Contains(point);
    }

    public bool ForwardKey(int virtualKey)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var target = NativeMethods.FindFocusableDescendant(hwnd);
        if (target == IntPtr.Zero)
        {
            target = hwnd;
        }

        if (IsPasteShortcut(virtualKey))
        {
            TryPasteClipboardText(target);
            return true;
        }

        var character = NativeMethods.VirtualKeyToCharacter((uint)virtualKey);
        if (character.HasValue)
        {
            NoteForwardedKey(virtualKey);
            NativeMethods.PostMessage(target, NativeMethods.WM_CHAR, (IntPtr)character.Value, IntPtr.Zero);
            return true;
        }

        switch (virtualKey)
        {
            case NativeMethods.VK_RETURN:
                NoteForwardedKey(virtualKey);
                NativeMethods.PostMessage(target, NativeMethods.WM_CHAR, (IntPtr)'\r', IntPtr.Zero);
                return true;
            case NativeMethods.VK_BACK:
                NoteForwardedKey(virtualKey);
                NativeMethods.PostMessage(target, NativeMethods.WM_CHAR, (IntPtr)'\b', IntPtr.Zero);
                return true;
            case NativeMethods.VK_TAB:
                NoteForwardedKey(virtualKey);
                NativeMethods.PostMessage(target, NativeMethods.WM_CHAR, (IntPtr)'\t', IntPtr.Zero);
                return true;
            case NativeMethods.VK_ESCAPE:
                NoteForwardedKey(virtualKey);
                NativeMethods.PostMessage(target, NativeMethods.WM_CHAR, (IntPtr)27, IntPtr.Zero);
                return true;
            case NativeMethods.VK_LEFT:
            case NativeMethods.VK_RIGHT:
            case NativeMethods.VK_UP:
            case NativeMethods.VK_DOWN:
            case NativeMethods.VK_DELETE:
            case NativeMethods.VK_HOME:
            case NativeMethods.VK_END:
                NoteForwardedKey(virtualKey);
                NativeMethods.PostMessage(target, NativeMethods.WM_KEYDOWN, (IntPtr)virtualKey, IntPtr.Zero);
                NativeMethods.PostMessage(target, NativeMethods.WM_KEYUP, (IntPtr)virtualKey, IntPtr.Zero);
                return true;
            default:
                return false;
        }
    }

    public bool ForwardText(string text)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(text))
        {
            return false;
        }

        var target = NativeMethods.FindFocusableDescendant(hwnd);
        if (target == IntPtr.Zero)
        {
            target = hwnd;
        }

        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                continue;
            }

            NativeMethods.PostMessage(target, NativeMethods.WM_CHAR, (IntPtr)(ch == '\r' ? '\r' : ch), IntPtr.Zero);
        }
        NoteForwardedKey(text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0 ? NativeMethods.VK_RETURN : NativeMethods.VK_V);
        return true;
    }

    private static bool IsPasteShortcut(int virtualKey)
    {
        return (virtualKey == NativeMethods.VK_V && Control.ModifierKeys.HasFlag(Keys.Control)) ||
               (virtualKey == NativeMethods.VK_INSERT && Control.ModifierKeys.HasFlag(Keys.Shift));
    }

    private bool TryPasteClipboardText(IntPtr target)
    {
        try
        {
            var text = ReadClipboardTextWithRetry();
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            foreach (var ch in text)
            {
                if (ch == '\n')
                {
                    continue;
                }

                var normalized = ch == '\r' ? '\r' : ch;
                NativeMethods.PostMessage(target, NativeMethods.WM_CHAR, (IntPtr)normalized, IntPtr.Zero);
            }
            NoteForwardedKey(text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0 ? NativeMethods.VK_RETURN : NativeMethods.VK_V);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadClipboardTextWithRetry()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    return Clipboard.GetText();
                }
            }
            catch
            {
                // Dictation tools can briefly lock or replace the clipboard while pasting.
            }
            Thread.Sleep(50);
        }
        return "";
    }

    private void StartFlash()
    {
        flashTicks = 16;
        flashTimer.Start();
    }

    private void NoteForwardedKey(int virtualKey)
    {
        lastKeyboardAt = DateTime.UtcNow;
        if (virtualKey != NativeMethods.VK_RETURN)
        {
            SetStatus("YOU typing");
            typingTimer.Stop();
            typingTimer.Start();
            return;
        }

        typingTimer.Stop();
        pendingCompletion = true;
        taskStartedAt = lastKeyboardAt;
        lastCpuMovedAt = lastKeyboardAt;
        lastCpu = TimeSpan.MinValue;
        SetStatus("CODEX working");
        completionTimer.Start();
    }

    private void CheckCompletion()
    {
        if (!pendingCompletion || rootProcessId <= 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - taskStartedAt).TotalSeconds < 3)
        {
            return;
        }

        if ((now - taskStartedAt).TotalSeconds >= 8 && (now - lastKeyboardAt).TotalSeconds >= 3)
        {
            CompleteTaskNotification();
            return;
        }

        var totalCpu = ProcessTree.TryGetTotalCpu(rootProcessId);
        if (!totalCpu.HasValue)
        {
            if ((now - taskStartedAt).TotalSeconds > 10)
            {
                CompleteTaskNotification();
            }
            return;
        }

        if (lastCpu == TimeSpan.MinValue)
        {
            lastCpu = totalCpu.Value;
            lastCpuMovedAt = now;
            return;
        }

        if ((totalCpu.Value - lastCpu).TotalMilliseconds > 80)
        {
            lastCpuMovedAt = now;
            lastCpu = totalCpu.Value;
            return;
        }

        if ((now - lastCpuMovedAt).TotalSeconds >= 2.5 && (now - lastKeyboardAt).TotalSeconds >= 3)
        {
            CompleteTaskNotification();
        }
    }

    private void CompleteTaskNotification()
    {
        pendingCompletion = false;
        completionTimer.Stop();
        SetStatus("ready");
        StartFlash();
        ThreadPool.QueueUserWorkItem(delegate { QuietPing.Play(); });
    }

    private void SetStatus(string status)
    {
        if (host.IsDisposed)
        {
            return;
        }

        if (host.InvokeRequired)
        {
            BeginOnUi(delegate { SetStatus(status); });
            return;
        }

        statusLabel.Text = status;
        if (status.StartsWith("YOU", StringComparison.OrdinalIgnoreCase))
        {
            statusLabel.ForeColor = UserBorder;
            statusBorder = UserBorder;
        }
        else if (status.StartsWith("CODEX", StringComparison.OrdinalIgnoreCase))
        {
            statusLabel.ForeColor = CodexBorder;
            statusBorder = CodexBorder;
        }
        else if (status == "stopped")
        {
            statusLabel.ForeColor = Color.FromArgb(255, 117, 117);
            statusBorder = Color.FromArgb(92, 41, 41);
        }
        else
        {
            statusLabel.ForeColor = Color.FromArgb(145, 156, 160);
            statusBorder = isActive ? ActiveBorder : IdleBorder;
        }
        if (!flashTimer.Enabled)
        {
            UpdateBorder();
        }
    }

    private void UpdateBorder()
    {
        host.BackColor = CurrentBorder();
    }

    private Color CurrentBorder()
    {
        if (statusBorder != IdleBorder && statusBorder != ActiveBorder)
        {
            return statusBorder;
        }
        return isActive ? ActiveBorder : IdleBorder;
    }

    private string BuildWindowsTerminalArgs(string title, string encodedCommand)
    {
        var builder = new StringBuilder();
        builder.Append("new-tab ");
        builder.Append("--title ");
        builder.Append(WinQuote(title));
        builder.Append(" --suppressApplicationTitle powershell.exe -NoLogo -NoExit -ExecutionPolicy Bypass -EncodedCommand ");
        builder.Append(encodedCommand);
        return builder.ToString();
    }

    private void FlashStep()
    {
        if (flashTicks <= 0)
        {
            flashTimer.Stop();
            UpdateBorder();
            return;
        }

        host.BackColor = flashTicks % 2 == 0
            ? AmberBorder
            : CurrentBorder();
        flashTicks--;
    }

    private static IntPtr WaitForProcessWindow(Process process, string titlePart, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process != null)
            {
                try
                {
                    process.Refresh();
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        return process.MainWindowHandle;
                    }
                    var byPid = NativeMethods.FindTopLevelWindowByProcessId(process.Id);
                    if (byPid != IntPtr.Zero)
                    {
                        return byPid;
                    }
                }
                catch
                {
                    // Windows Terminal can hand off to an existing process. Fall back to title search below.
                }
            }

            var found = NativeMethods.FindTopLevelWindow(titlePart);
            if (found != IntPtr.Zero)
            {
                return found;
            }
            Thread.Sleep(150);
        }
        return IntPtr.Zero;
    }

    private static string PsQuote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string WinQuote(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private void BeginOnUi(MethodInvoker action)
    {
        if (host.IsDisposed)
        {
            return;
        }

        try
        {
            if (host.InvokeRequired)
            {
                host.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch
        {
            // The host may be closing while background terminal startup finishes.
        }
    }
}

internal sealed class KeyboardForwarder : IDisposable
{
    private readonly HostForm host;
    private readonly List<EmbeddedTerminal> terminals;
    private readonly NativeMethods.LowLevelKeyboardProc proc;
    private IntPtr hook = IntPtr.Zero;

    public KeyboardForwarder(HostForm host, List<EmbeddedTerminal> terminals)
    {
        this.host = host;
        this.terminals = terminals;
        proc = HookCallback;
        hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, proc, NativeMethods.GetModuleHandle(null), 0);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || host.IsDisposed || terminals.Count == 0)
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        if (message != NativeMethods.WM_KEYDOWN && message != NativeMethods.WM_SYSKEYDOWN)
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        var data = (NativeMethods.KbdLlHookStruct)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KbdLlHookStruct));
        if (IsWindowsSystemShortcut((int)data.vkCode))
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        var cursorOnHost = host.RectangleToScreen(host.ClientRectangle).Contains(Cursor.Position) && host.IsPointActuallyOnHost(Cursor.Position);
        var foregroundIsHost = host.IsForegroundActuallyHost();
        if (!cursorOnHost && !foregroundIsHost)
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        if (host.ActiveControl is TextBox && !host.IsDictationInputControl(host.ActiveControl))
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        if (Form.ActiveForm != host && !foregroundIsHost)
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        var terminal = host.ActiveTerminal;
        if (terminal != null && terminal.ForwardKey((int)data.vkCode))
        {
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    }

    private static bool IsWindowsSystemShortcut(int virtualKey)
    {
        var altDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_MENU) & 0x8000) != 0;
        var winDown =
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0 ||
            (NativeMethods.GetAsyncKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0;

        if (winDown || virtualKey == NativeMethods.VK_LWIN || virtualKey == NativeMethods.VK_RWIN)
        {
            return true;
        }

        if (!altDown)
        {
            return false;
        }

        return virtualKey == NativeMethods.VK_TAB ||
               virtualKey == NativeMethods.VK_ESCAPE ||
               virtualKey == NativeMethods.VK_F4 ||
               virtualKey == NativeMethods.VK_MENU;
    }
}

internal sealed class MousePaneActivator : IDisposable
{
    private readonly HostForm host;
    private readonly List<EmbeddedTerminal> terminals;
    private readonly NativeMethods.LowLevelMouseProc proc;
    private IntPtr hook = IntPtr.Zero;

    public MousePaneActivator(HostForm host, List<EmbeddedTerminal> terminals)
    {
        this.host = host;
        this.terminals = terminals;
        proc = HookCallback;
        hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, proc, NativeMethods.GetModuleHandle(null), 0);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0 || host.IsDisposed || terminals.Count == 0)
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        if (message != NativeMethods.WM_LBUTTONDOWN && message != NativeMethods.WM_MOUSEMOVE && message != NativeMethods.WM_LBUTTONUP)
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        var data = (NativeMethods.MouseLlHookStruct)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MouseLlHookStruct));
        var point = new Point(data.pt.x, data.pt.y);
        if (!host.IsPointActuallyOnHost(point))
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        host.HandleWindowMouseHook(message, point);

        if (message != NativeMethods.WM_LBUTTONDOWN)
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        if (!host.RectangleToScreen(host.ClientRectangle).Contains(point))
        {
            return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
        }

        var terminal = host.TerminalAtScreenPoint(point);
        if (terminal != null)
        {
            host.BeginInvoke((MethodInvoker)delegate
            {
                if (!host.IsDisposed)
                {
                    host.SetActiveTerminal(terminal);
                }
            });
            if (terminal.HeaderContainsScreenPoint(point))
            {
                return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
            }
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(hook, code, wParam, lParam);
    }
}

internal static class ProcessTree
{
    public static TimeSpan? TryGetTotalCpu(int rootProcessId)
    {
        try
        {
            var pids = NativeMethods.GetProcessTreeIds(rootProcessId);
            if (pids.Count == 0)
            {
                pids.Add(rootProcessId);
            }

            var total = TimeSpan.Zero;
            foreach (var pid in pids)
            {
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        total += process.TotalProcessorTime;
                    }
                }
                catch
                {
                    // Processes can exit while the snapshot is being read.
                }
            }
            return total;
        }
        catch
        {
            return null;
        }
    }
}

internal static class QuietPing
{
    public static void Play()
    {
        try
        {
            using (var stream = new MemoryStream())
            {
                WriteQuietSineWave(stream);
                stream.Position = 0;
                using (var player = new System.Media.SoundPlayer(stream))
                {
                    player.PlaySync();
                }
            }
        }
        catch
        {
            // Notification sound is best-effort.
        }
    }

    private static void WriteQuietSineWave(Stream stream)
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        const double frequency = 660.0;
        const double seconds = 0.16;
        const short amplitude = 2800;

        var sampleCount = (int)(sampleRate * seconds);
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var dataSize = sampleCount * channels * bitsPerSample / 8;
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);

            for (var i = 0; i < sampleCount; i++)
            {
                var fade = Math.Min(1.0, Math.Min(i / 1200.0, (sampleCount - i) / 2200.0));
                var value = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * fade);
                writer.Write(value);
            }
        }
    }
}

internal static class WindowsTerminalLocator
{
    public static string Find()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        if (Directory.Exists(root))
        {
            var match = Directory.GetDirectories(root, "Microsoft.WindowsTerminal_*_x64__8wekyb3d8bbwe")
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .Select(dir => Path.Combine(dir, "WindowsTerminal.exe"))
                .FirstOrDefault(File.Exists);
            if (match != null)
            {
                return match;
            }
        }

        return "wt.exe";
    }
}

internal static class CodexLocator
{
    public static string Find()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "codex.exe");
                if (File.Exists(exe))
                {
                    return exe;
                }
                var cmd = Path.Combine(dir.Trim(), "codex.cmd");
                if (File.Exists(cmd))
                {
                    return cmd;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return "codex";
    }
}

internal static class NativeMethods
{
    public const int GWL_STYLE = -16;
    public const int WS_CHILD = 0x40000000;
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_CAPTION = 0x00C00000;
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_CLIPCHILDREN = 0x02000000;
    public const int WS_CLIPSIBLINGS = 0x04000000;
    public const int WM_CLOSE = 0x0010;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_CHAR = 0x0102;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_LBUTTONUP = 0x0202;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCLIENT = 0x0001;
    public const int HTCAPTION = 0x0002;
    public const int HTLEFT = 0x000A;
    public const int HTRIGHT = 0x000B;
    public const int HTTOP = 0x000C;
    public const int HTTOPLEFT = 0x000D;
    public const int HTTOPRIGHT = 0x000E;
    public const int HTBOTTOM = 0x000F;
    public const int HTBOTTOMLEFT = 0x0010;
    public const int HTBOTTOMRIGHT = 0x0011;
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const int VK_BACK = 0x08;
    public const int VK_TAB = 0x09;
    public const int VK_RETURN = 0x0D;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_MENU = 0x12;
    public const int VK_LEFT = 0x25;
    public const int VK_UP = 0x26;
    public const int VK_RIGHT = 0x27;
    public const int VK_DOWN = 0x28;
    public const int VK_INSERT = 0x2D;
    public const int VK_F4 = 0x73;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_V = 0x56;
    public const int VK_DELETE = 0x2E;
    public const int VK_HOME = 0x24;
    public const int VK_END = 0x23;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    public delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PointStruct
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseLlHookStruct
    {
        public PointStruct pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int ToUnicode(uint virtualKey, uint scanCode, byte[] keyboardState, [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder receivingBuffer, int bufferSize, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] keyState);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsChild(IntPtr parent, IntPtr child);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public const int SW_SHOW = 5;

    public static IntPtr FindTopLevelWindow(string titlePart)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }
            var builder = new StringBuilder(512);
            GetWindowText(hWnd, builder, builder.Capacity);
            if (builder.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindTopLevelWindowByProcessId(int processId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }
            uint windowProcessId;
            GetWindowThreadProcessId(hWnd, out windowProcessId);
            if (windowProcessId == processId)
            {
                result = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindFocusableDescendant(IntPtr root)
    {
        IntPtr result = IntPtr.Zero;
        EnumChildWindows(root, delegate(IntPtr hWnd, IntPtr lParam)
        {
            if (IsWindowVisible(hWnd))
            {
                result = hWnd;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static char? VirtualKeyToCharacter(uint virtualKey)
    {
        if (Control.ModifierKeys.HasFlag(Keys.Control) || Control.ModifierKeys.HasFlag(Keys.Alt))
        {
            return null;
        }

        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState))
        {
            return null;
        }

        var scanCode = MapVirtualKey(virtualKey, 0);
        var builder = new StringBuilder(8);
        var result = ToUnicode(virtualKey, scanCode, keyboardState, builder, builder.Capacity, 0);
        if (result == 1 && builder.Length > 0 && !char.IsControl(builder[0]))
        {
            return builder[0];
        }

        return null;
    }

    public static int LowWord(IntPtr value)
    {
        return unchecked((short)((long)value & 0xFFFF));
    }

    public static int HighWord(IntPtr value)
    {
        return unchecked((short)(((long)value >> 16) & 0xFFFF));
    }

    public static List<int> GetProcessTreeIds(int rootProcessId)
    {
        var childrenByParent = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            return new List<int> { rootProcessId };
        }

        try
        {
            var entry = new ProcessEntry32();
            entry.dwSize = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
            if (!Process32First(snapshot, ref entry))
            {
                return new List<int> { rootProcessId };
            }

            do
            {
                var parent = (int)entry.th32ParentProcessID;
                var pid = (int)entry.th32ProcessID;
                List<int> children;
                if (!childrenByParent.TryGetValue(parent, out children))
                {
                    children = new List<int>();
                    childrenByParent[parent] = children;
                }
                children.Add(pid);
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var result = new List<int> { rootProcessId };
        var queue = new Queue<int>();
        queue.Enqueue(rootProcessId);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            List<int> children;
            if (!childrenByParent.TryGetValue(parent, out children))
            {
                continue;
            }

            foreach (var child in children)
            {
                result.Add(child);
                queue.Enqueue(child);
            }
        }
        return result;
    }
}
