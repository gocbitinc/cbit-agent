using System.Reflection;

namespace CbitAgent.Tray;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private readonly TrayApiClient _apiClient;

    public TrayApplicationContext()
    {
        _apiClient = new TrayApiClient();

        _notifyIcon = new NotifyIcon
        {
            Text = "CBIT Support — Right-click for options",
            Icon = LoadEmbeddedIcon(),
            BalloonTipTitle = "CBIT Support",
            BalloonTipText = "Right-click the tray icon to submit a support request.",
            BalloonTipIcon = ToolTipIcon.Info,
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _notifyIcon.MouseClick += OnTrayClick;

        // Show balloon so Windows surfaces the icon on first run
        _notifyIcon.ShowBalloonTip(3000);
    }

    private static Icon LoadEmbeddedIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream("CbitAgent.Tray.cbit.ico");
        if (stream != null)
            return new Icon(stream);

        // Fallback to system icon
        return SystemIcons.Information;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        var supportItem = new ToolStripMenuItem("Submit Support Request");
        supportItem.Click += OnSupportRequest;
        supportItem.Font = new Font(supportItem.Font, FontStyle.Bold);
        menu.Items.Add(supportItem);

        menu.Items.Add(new ToolStripSeparator());

        var aboutItem = new ToolStripMenuItem("About");
        aboutItem.Click += OnAbout;
        menu.Items.Add(aboutItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += OnExit;
        menu.Items.Add(exitItem);

        if (!_apiClient.IsConfigured)
        {
            supportItem.Enabled = false;
            supportItem.Text = "Agent not yet registered";
        }

        return menu;
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowComputerNamePopup();
        else if (e.Button == MouseButtons.Middle)
            OnSupportRequest(sender, e);
    }

    private void ShowComputerNamePopup()
    {
        var name = Environment.MachineName;
        var popup = new Form
        {
            Text = "",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            ClientSize = new Size(300, 130),
            ShowInTaskbar = false,
            TopMost = true
        };

        // Position bottom-right, just above the taskbar
        var workArea = Screen.PrimaryScreen!.WorkingArea;
        popup.Location = new Point(
            workArea.Right - popup.Width - 10,
            workArea.Bottom - popup.Height);

        var label = new Label
        {
            Text = name,
            Dock = DockStyle.None,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 14f, FontStyle.Bold),
            Location = new Point(0, 12),
            Size = new Size(300, 40)
        };
        popup.Controls.Add(label);

        var subLabel = new Label
        {
            Text = "Computer Name",
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 8.5f),
            ForeColor = Color.Gray,
            Location = new Point(0, 50),
            Size = new Size(300, 20)
        };
        popup.Controls.Add(subLabel);

        var copyBtn = new Button
        {
            Text = "Copy",
            Font = new Font("Segoe UI", 10f),
            Location = new Point(75, 80),
            Size = new Size(150, 36)
        };
        copyBtn.Click += (_, _) =>
        {
            Clipboard.SetText(name);
            copyBtn.Text = "Copied!";
        };
        popup.Controls.Add(copyBtn);

        // Auto-close after 5 seconds
        var timer = new System.Windows.Forms.Timer { Interval = 5000 };
        timer.Tick += (_, _) => { timer.Stop(); popup.Close(); };
        timer.Start();

        popup.Show();
    }

    private void OnSupportRequest(object? sender, EventArgs e)
    {
        if (!_apiClient.IsConfigured)
        {
            MessageBox.Show(
                "The CBIT Agent is not yet registered with the server.\n" +
                "Please wait for registration to complete and try again.",
                "CBIT Support", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // L12: Screenshot no longer pre-captured here — deferred until user opts in via checkbox in the form
        using var form = new SupportRequestForm(_apiClient);
        form.ShowDialog();
    }

    private void OnAbout(object? sender, EventArgs e)
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";

        MessageBox.Show(
            $"CBIT RMM Agent — Support Tray\nVersion {versionStr}\n\n" +
            "CBIT Inc.\n(509) 578-5424\nsupport@gocbit.com\nhttps://www.gocbit.com",
            "About CBIT Support", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
