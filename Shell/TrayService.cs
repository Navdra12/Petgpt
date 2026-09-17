using System.Reflection;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace PetGPT.Shell;

public sealed class TrayService : IDisposable
{
    private const string EmbeddedFallbackIcon = "PetGPT.Assets.petgpt-fallback.ico";

    private readonly Action _toggleChat;
    private readonly Action _exit;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showHideChat;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DrawingIcon _icon;
    private bool _disposed;

    public TrayService(Action toggleChat, Action exit)
    {
        _toggleChat = toggleChat ?? throw new ArgumentNullException(nameof(toggleChat));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _icon = LoadFallbackIcon();

        _showHideChat = new Forms.ToolStripMenuItem("Show ChatGPT");
        _showHideChat.Click += OnToggleChat;

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(_showHideChat);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateUnavailableItem("New PetChat — unavailable"));
        _menu.Items.Add(CreateUnavailableItem("History — unavailable"));
        _menu.Items.Add(CreateUnavailableItem("Pet — unavailable"));
        _menu.Items.Add(CreateUnavailableItem("Settings — unavailable"));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("Exit PetGPT");
        exitItem.Click += OnExit;
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _icon,
            Text = "PetGPT",
            Visible = true
        };
        _notifyIcon.DoubleClick += OnToggleChat;
    }

    public void SetChatVisible(bool visible)
    {
        if (!_disposed)
            _showHideChat.Text = visible ? "Hide ChatGPT" : "Show ChatGPT";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.DoubleClick -= OnToggleChat;
        _showHideChat.Click -= OnToggleChat;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }

    private void OnToggleChat(object? sender, EventArgs e) => _toggleChat();

    private void OnExit(object? sender, EventArgs e) => _exit();

    private static Forms.ToolStripMenuItem CreateUnavailableItem(string text) =>
        new(text) { Enabled = false };

    private static DrawingIcon LoadFallbackIcon()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedFallbackIcon);
        if (stream is null)
            return (DrawingIcon)System.Drawing.SystemIcons.Application.Clone();

        using var embeddedIcon = new DrawingIcon(stream);
        return (DrawingIcon)embeddedIcon.Clone();
    }
}
