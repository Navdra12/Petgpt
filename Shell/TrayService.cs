using System.Reflection;
using PetGPT.Characters;
using PetGPT.Models;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace PetGPT.Shell;

public sealed class TrayService : IDisposable
{
    private const string EmbeddedFallbackIcon = "PetGPT.Assets.petgpt-fallback.ico";

    private readonly Action _toggleChat;
    private readonly Action _exit;
    private readonly Func<NavigationIntent, Task<NavigationResult>> _navigateAsync;
    private readonly Func<string, string, Task<PetSelectionResult>> _selectPetAsync;
    private readonly Action _commands;
    private readonly Action _settings;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showHideChat;
    private readonly Forms.ToolStripMenuItem _newPetChat;
    private readonly Forms.ToolStripMenuItem _history;
    private readonly Forms.ToolStripMenuItem _petMenu;
    private readonly Forms.ToolStripMenuItem _commandsItem;
    private readonly Forms.ToolStripMenuItem _settingsItem;
    private readonly Forms.NotifyIcon _notifyIcon;
    private TrayPetMenuState _petMenuState;
    private readonly Dictionary<(string Id, string Version), Forms.ToolStripMenuItem> _petItems = [];
    private readonly OwnedResourceSlot<DrawingIcon> _iconSlot;
    private bool _disposed;

    public TrayService(
        Action toggleChat,
        Action exit,
        IReadOnlyList<CharacterPack> packs,
        Func<string, string, Task<PetSelectionResult>> selectPetAsync,
        Func<NavigationIntent, Task<NavigationResult>> navigateAsync,
        bool hasConfiguredPetChatsHome,
        Action commands,
        Action settings)
    {
        _toggleChat = toggleChat ?? throw new ArgumentNullException(nameof(toggleChat));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _selectPetAsync = selectPetAsync ?? throw new ArgumentNullException(nameof(selectPetAsync));
        _navigateAsync = navigateAsync ?? throw new ArgumentNullException(nameof(navigateAsync));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _petMenuState = new TrayPetMenuState(packs ?? throw new ArgumentNullException(nameof(packs)));
        _iconSlot = new OwnedResourceSlot<DrawingIcon>(LoadFallbackIcon());

        _showHideChat = new Forms.ToolStripMenuItem("Show ChatGPT");
        _showHideChat.Click += OnToggleChat;

        _newPetChat = new Forms.ToolStripMenuItem(
            hasConfiguredPetChatsHome ? "New PetChat" : "New PetChat — PetChats not configured")
        {
            Enabled = hasConfiguredPetChatsHome
        };
        _newPetChat.Click += OnNewPetChat;
        _history = new Forms.ToolStripMenuItem(
            hasConfiguredPetChatsHome ? "History" : "History — PetChats not configured")
        {
            Enabled = hasConfiguredPetChatsHome
        };
        _history.Click += OnHistory;
        _petMenu = CreatePetMenu();
        _commandsItem = new Forms.ToolStripMenuItem("PetGPT Commands");
        _commandsItem.Click += OnCommands;
        _settingsItem = new Forms.ToolStripMenuItem("Settings");
        _settingsItem.Click += OnSettings;

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(_showHideChat);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_newPetChat);
        _menu.Items.Add(_history);
        _menu.Items.Add(_petMenu);
        _menu.Items.Add(_commandsItem);
        _menu.Items.Add(_settingsItem);
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

    public void SetPetChatsConfigured(bool configured)
    {
        if (_disposed)
            return;
        _newPetChat.Enabled = configured;
        _history.Enabled = configured;
        _newPetChat.Text = configured ? "New PetChat" : "New PetChat — PetChats not configured";
        _history.Text = configured ? "History" : "History — PetChats not configured";
    }

    public void RefreshPacks(
        IReadOnlyList<CharacterPack> packs,
        string? selectedId,
        string? selectedVersion)
    {
        if (_disposed)
            return;
        foreach (var item in _petItems.Values)
            item.Click -= OnSelectPet;
        _petItems.Clear();
        _petMenu.DropDownItems.Clear();
        _petMenuState = new TrayPetMenuState(packs);
        _petMenuState.CommitSelection(selectedId ?? string.Empty, selectedVersion ?? string.Empty);
        PopulatePetMenu(_petMenu);
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
        _newPetChat.Click -= OnNewPetChat;
        _history.Click -= OnHistory;
        _commandsItem.Click -= OnCommands;
        _settingsItem.Click -= OnSettings;
        foreach (var item in _petItems.Values)
            item.Click -= OnSelectPet;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _iconSlot.Dispose();
    }

    private Forms.ToolStripMenuItem CreatePetMenu()
    {
        var menu = new Forms.ToolStripMenuItem("Pet");
        PopulatePetMenu(menu);
        return menu;
    }

    private void PopulatePetMenu(Forms.ToolStripMenuItem menu)
    {
        if (_petMenuState.Entries.Count == 0)
        {
            menu.Enabled = false;
            return;
        }

        menu.Enabled = true;

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

    private void OnCommands(object? sender, EventArgs e) => _commands();

    private void OnSettings(object? sender, EventArgs e) => _settings();

    private async void OnNewPetChat(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        try
        {
            await _navigateAsync(NavigationIntent.NewPetChat);
        }
        catch
        {
            // Navigation owner retains the current page on failure.
        }
    }

    private async void OnHistory(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        try
        {
            await _navigateAsync(NavigationIntent.History);
        }
        catch
        {
            // Navigation owner retains the current page on failure.
        }
    }

    private void OnExit(object? sender, EventArgs e) => _exit();

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
