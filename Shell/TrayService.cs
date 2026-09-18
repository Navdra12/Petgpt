using System.Reflection;
using PetGPT.Characters;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace PetGPT.Shell;

public sealed class TrayService : IDisposable
{
    private const string EmbeddedFallbackIcon = "PetGPT.Assets.petgpt-fallback.ico";

    private readonly Action _toggleChat;
    private readonly Action _exit;
    private readonly Func<string, string, Task<PetSelectionResult>> _selectPetAsync;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showHideChat;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly TrayPetMenuState _petMenuState;
    private readonly Dictionary<(string Id, string Version), Forms.ToolStripMenuItem> _petItems = [];
    private readonly OwnedResourceSlot<DrawingIcon> _iconSlot;
    private bool _disposed;

    public TrayService(
        Action toggleChat,
        Action exit,
        IReadOnlyList<CharacterPack> packs,
        Func<string, string, Task<PetSelectionResult>> selectPetAsync)
    {
        _toggleChat = toggleChat ?? throw new ArgumentNullException(nameof(toggleChat));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _selectPetAsync = selectPetAsync ?? throw new ArgumentNullException(nameof(selectPetAsync));
        _petMenuState = new TrayPetMenuState(packs ?? throw new ArgumentNullException(nameof(packs)));
        _iconSlot = new OwnedResourceSlot<DrawingIcon>(LoadFallbackIcon());

        _showHideChat = new Forms.ToolStripMenuItem("Show ChatGPT");
        _showHideChat.Click += OnToggleChat;

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(_showHideChat);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(CreateUnavailableItem("New PetChat — unavailable"));
        _menu.Items.Add(CreateUnavailableItem("History — unavailable"));
        _menu.Items.Add(CreatePetMenu());
        _menu.Items.Add(CreateUnavailableItem("Settings — unavailable"));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("Exit PetGPT");
        exitItem.Click += OnExit;
        _menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _menu,
            Icon = _iconSlot.Current,
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

    internal void ApplySelection(CharacterPack pack, DrawingIcon preparedIcon)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TrayService));

        try
        {
            var replacement = (DrawingIcon)preparedIcon.Clone();
            _ = TrayIconSwap.TryReplace(_iconSlot, replacement, icon => _notifyIcon.Icon = icon);
        }
        catch
        {
            // Keep the prior owned icon. Icon presentation is optional.
        }

        _petMenuState.CommitSelection(pack.Id, pack.Version);
        foreach (var entry in _petMenuState.Entries)
        {
            if (_petItems.TryGetValue((entry.Pack.Id, entry.Pack.Version), out var item))
                item.Checked = entry.IsChecked;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.DoubleClick -= OnToggleChat;
        _showHideChat.Click -= OnToggleChat;
        foreach (var item in _petItems.Values)
            item.Click -= OnSelectPet;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _iconSlot.Dispose();
    }

    private Forms.ToolStripMenuItem CreatePetMenu()
    {
        var menu = new Forms.ToolStripMenuItem("Pet");
        if (_petMenuState.Entries.Count == 0)
        {
            menu.Enabled = false;
            return menu;
        }

        foreach (var entry in _petMenuState.Entries)
        {
            var item = new Forms.ToolStripMenuItem(entry.Label)
            {
                Checked = entry.IsChecked,
                CheckOnClick = false,
                Tag = entry
            };
            item.Click += OnSelectPet;
            menu.DropDownItems.Add(item);
            _petItems.Add((entry.Pack.Id, entry.Pack.Version), item);
        }

        return menu;
    }

    private async void OnSelectPet(object? sender, EventArgs e)
    {
        if (_disposed || sender is not Forms.ToolStripMenuItem { Tag: TrayPetMenuEntry entry })
            return;

        try
        {
            await _selectPetAsync(entry.Pack.Id, entry.Pack.Version);
        }
        catch
        {
            // PetSelectionService owns diagnostics; retain the previous check state.
        }
    }

    private void OnToggleChat(object? sender, EventArgs e) => _toggleChat();

    private void OnExit(object? sender, EventArgs e) => _exit();

    private static Forms.ToolStripMenuItem CreateUnavailableItem(string text) =>
        new(text) { Enabled = false };

    internal static DrawingIcon LoadFallbackIcon()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(EmbeddedFallbackIcon);
        if (stream is null)
            return (DrawingIcon)System.Drawing.SystemIcons.Application.Clone();

        using var embeddedIcon = new DrawingIcon(stream);
        return (DrawingIcon)embeddedIcon.Clone();
    }
}

internal sealed class TrayPetMenuEntry(CharacterPack pack, string label)
{
    public CharacterPack Pack { get; } = pack;
    public string Label { get; } = label;
    public bool IsChecked { get; internal set; }
}

internal sealed class TrayPetMenuState
{
    public TrayPetMenuState(IReadOnlyList<CharacterPack> packs)
    {
        var versionCounts = packs
            .GroupBy(pack => pack.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Entries = packs
            .OrderBy(pack => pack.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pack => pack.Id, StringComparer.Ordinal)
            .ThenBy(pack => pack.Version, StringComparer.Ordinal)
            .Select(pack => new TrayPetMenuEntry(
                pack,
                versionCounts[pack.Id] > 1
                    ? $"{pack.DisplayName} ({pack.Version})"
                    : pack.DisplayName))
            .ToArray();
    }

    public IReadOnlyList<TrayPetMenuEntry> Entries { get; }

    public void CommitSelection(string id, string version)
    {
        foreach (var entry in Entries)
        {
            entry.IsChecked = entry.Pack.Id.Equals(id, StringComparison.Ordinal) &&
                              entry.Pack.Version.Equals(version, StringComparison.Ordinal);
        }
    }
}

internal sealed class OwnedResourceSlot<T> : IDisposable where T : class, IDisposable
{
    private T? _current;

    public OwnedResourceSlot(T initial) => _current = initial ?? throw new ArgumentNullException(nameof(initial));

    public T Current => _current ?? throw new ObjectDisposedException(nameof(OwnedResourceSlot<T>));

    public void Replace(T replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = Interlocked.Exchange(ref _current, replacement);
        if (!ReferenceEquals(previous, replacement))
            previous?.Dispose();
    }

    public void Dispose() => Interlocked.Exchange(ref _current, null)?.Dispose();
}

internal static class TrayIconSwap
{
    public static bool TryReplace<T>(
        OwnedResourceSlot<T> slot,
        T candidate,
        Action<T> assign) where T : class, IDisposable
    {
        try
        {
            assign(candidate);
            slot.Replace(candidate);
            return true;
        }
        catch
        {
            candidate.Dispose();
            return false;
        }
    }
}
