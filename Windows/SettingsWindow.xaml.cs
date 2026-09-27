using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Services;
using PetGPT.Shell;
using Forms = System.Windows.Forms;

namespace PetGPT.Windows;

public enum SettingsSection
{
    PetChats,
    Character,
    Appearance,
    Roleplay,
    Placement,
    Advanced,
    Diagnostics
}

public partial class SettingsWindow : Window
{
    private readonly AppSettings _working;
    private readonly Func<AppSettings, CancellationToken, Task<SettingsApplyResult>> _applyAsync;
    private readonly Func<string, CancellationToken, Task<CharacterImportResult>> _importAsync;
    private readonly Func<IReadOnlyList<CharacterPack>> _getPacks;
    private readonly IReadOnlyList<string> _diagnostics;
    private CharacterChoice? _selectedChoice;
    private bool _loading;
    private bool _closing;

    internal SettingsWindow(
        AppSettings liveSnapshot,
        IReadOnlyList<CharacterPack> packs,
        IReadOnlyList<string> diagnostics,
        Func<AppSettings, CancellationToken, Task<SettingsApplyResult>> applyAsync,
        Func<string, CancellationToken, Task<CharacterImportResult>> importAsync,
        Func<IReadOnlyList<CharacterPack>> getPacks)
    {
        InitializeComponent();
        _working = (liveSnapshot ?? throw new ArgumentNullException(nameof(liveSnapshot))).Copy();
        _diagnostics = diagnostics ?? [];
        _applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        _importAsync = importAsync ?? throw new ArgumentNullException(nameof(importAsync));
        _getPacks = getPacks ?? throw new ArgumentNullException(nameof(getPacks));
        LoadControls(packs ?? []);
    }

    public void FocusSection(SettingsSection section)
    {
        SettingsTabs.SelectedItem = section switch
        {
            SettingsSection.Character => CharacterTab,
            SettingsSection.Appearance => AppearanceTab,
            SettingsSection.Roleplay => RoleplayTab,
            SettingsSection.Placement => PlacementTab,
            SettingsSection.Advanced => AdvancedTab,
            SettingsSection.Diagnostics => DiagnosticsTab,
            _ => PetChatsTab
        };
        Activate();
    }

    public void BeginAppShutdown()
    {
        _closing = true;
        Close();
    }

    private void LoadControls(IReadOnlyList<CharacterPack> packs)
    {
        _loading = true;
        try
        {
            HomeUrlTextBox.Text = _working.ChatHomeUrl ?? string.Empty;
            HomeStatusText.Text = _working.ChatHomeUrl is null
                ? "PetChats is not configured. New and History remain unavailable."
                : "Configured project landing route.";
            CompactModeCheckBox.IsChecked = _working.CompactMode;
            ThemesEnabledCheckBox.IsChecked = _working.ThemesEnabled;
            RoleplayEnabledCheckBox.IsChecked = _working.Roleplay.Enabled;
            ReactionsEnabledCheckBox.IsChecked = _working.ReactionsEnabled;
            ShowControlMarkersCheckBox.IsChecked = _working.ShowControlMarkers;
            SuspendHiddenBrowserCheckBox.IsChecked = _working.SuspendHiddenBrowser;
            PlacementComboBox.SelectedIndex = _working.ChatWindow.PlacementMode == "Free" ? 1 : 0;
            DiagnosticsTextBox.Text = _diagnostics.Count == 0
                ? "No local diagnostic codes."
                : string.Join(Environment.NewLine, _diagnostics.Take(32));
            RefreshPacks(packs, _working.SelectedPetId,
                _working.SelectedPackVersions.GetValueOrDefault(_working.SelectedPetId));
        }
        finally
        {
            _loading = false;
        }
        LoadSelectedPetOptions();
    }

    private void RefreshPacks(IReadOnlyList<CharacterPack> packs, string? selectedId, string? selectedVersion)
    {
        _loading = true;
        try
        {
            var choices = packs
                .OrderBy(pack => pack.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pack => pack.Id, StringComparer.Ordinal)
                .ThenBy(pack => pack.Version, StringComparer.Ordinal)
                .Select(pack => new CharacterChoice(pack))
                .ToArray();
            CharacterComboBox.ItemsSource = choices;
            _selectedChoice = choices.FirstOrDefault(choice =>
                choice.Pack.Id.Equals(selectedId, StringComparison.Ordinal) &&
                choice.Pack.Version.Equals(selectedVersion, StringComparison.Ordinal));
            CharacterComboBox.SelectedItem = _selectedChoice;
            if (_selectedChoice is null && choices.Length > 0)
            {
                _selectedChoice = choices[0];
                CharacterComboBox.SelectedItem = _selectedChoice;
            }
        }
        finally
        {
            _loading = false;
        }
        UpdateCharacterStatus();
    }

    private void OnCharacterSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        SaveSelectedPetOptions(showError: false);
        _selectedChoice = CharacterComboBox.SelectedItem as CharacterChoice;
        if (_selectedChoice is not null)
        {
            _working.SelectedPetId = _selectedChoice.Pack.Id;
            _working.SelectedPackVersions[_selectedChoice.Pack.Id] = _selectedChoice.Pack.Version;
        }
        LoadSelectedPetOptions();
        UpdateCharacterStatus();
    }

    private void LoadSelectedPetOptions()
    {
        var id = _selectedChoice?.Pack.Id ?? _working.SelectedPetId;
        var options = _working.PetOptions.GetValueOrDefault(id) ?? new PetOptionSettings();
        ScaleTextBox.Text = options.Scale.ToString("0.##", CultureInfo.InvariantCulture);
        ReducedMotionCheckBox.IsChecked = options.ReducedMotion;
    }

    private bool SaveSelectedPetOptions(bool showError)
    {
        var id = _selectedChoice?.Pack.Id ?? _working.SelectedPetId;
        if (!double.TryParse(
                ScaleTextBox.Text,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var scale) ||
            !double.IsFinite(scale) || scale is < 0.5 or > 2.0)
        {
            if (showError)
                ApplyStatusText.Text = "Scale must be a number from 0.5 through 2.0.";
            return false;
        }

        _working.PetOptions[id] = new PetOptionSettings
        {
            Scale = scale,
            ReducedMotion = ReducedMotionCheckBox.IsChecked == true
        };
        return true;
    }

    private void UpdateCharacterStatus()
    {
        if (_selectedChoice is null)
        {
            SelectedCharacterText.Text = "No valid character pack is available.";
            RoleplayAvailabilityText.Text = "Roleplay and reactions are unavailable.";
            RoleplayEnabledCheckBox.IsEnabled = false;
            ReactionsEnabledCheckBox.IsEnabled = false;
            return;
        }

        var pack = _selectedChoice.Pack;
        SelectedCharacterText.Text = $"Selected: {pack.Id}@{pack.Version}";
        var supportsRoleplay = pack.Persona is not null && pack.Reactions.Count > 0;
        RoleplayEnabledCheckBox.IsEnabled = supportsRoleplay;
        ReactionsEnabledCheckBox.IsEnabled = supportsRoleplay;
        RoleplayAvailabilityText.Text = supportsRoleplay
            ? "This character supplies a validated persona and reaction vocabulary. Activation remains ReviewThenSend."
            : "The selected character has no persona/reactions; roleplay remains inactive.";
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_closing || !SaveSelectedPetOptions(showError: true))
            return;
        var home = HomeUrlTextBox.Text.Trim();
        _working.ChatHomeUrl = home.Length == 0 ? null : home;
        _working.CompactMode = CompactModeCheckBox.IsChecked == true;
        _working.ThemesEnabled = ThemesEnabledCheckBox.IsChecked == true;
        _working.Roleplay.Enabled = RoleplayEnabledCheckBox.IsChecked == true;
        _working.Roleplay.ActivationMode = "ReviewThenSend";
        _working.ReactionsEnabled = ReactionsEnabledCheckBox.IsChecked == true;
        _working.ShowControlMarkers = ShowControlMarkersCheckBox.IsChecked == true;
        _working.ChatWindow.PlacementMode = PlacementComboBox.SelectedIndex == 1 ? "Free" : "FollowPet";
        _working.SuspendHiddenBrowser = false;

        ApplyButton.IsEnabled = false;
        ApplyStatusText.Text = "Applying…";
        try
        {
            var result = await _applyAsync(_working.Copy(), CancellationToken.None);
            if (!result.Succeeded)
            {
                ApplyStatusText.Text = $"Apply failed: {result.DiagnosticCode ?? "unknown"}";
                return;
            }
            if (result.AppliedSnapshot is not null)
                _working.CopyFrom(result.AppliedSnapshot);
            ApplyStatusText.Text = "Settings applied and saved.";
            HomeStatusText.Text = _working.ChatHomeUrl is null
                ? "PetChats is not configured. New and History are unavailable."
                : "Configured project landing route.";
        }
        catch
        {
            ApplyStatusText.Text = "Apply failed: settings_apply_failed";
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }

    private void OnCopyProjectInstructions(object sender, RoutedEventArgs e)
    {
        var result = ProjectInstructionsCopy.TryCopy(text =>
        {
            System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
            return true;
        });
        CopyStatusText.Text = result.Succeeded
            ? "Project Instructions copied. Paste them manually into the PetChats Project instructions."
            : $"Copy failed: {result.DiagnosticCode}";
    }

    private async void OnImportFolder(object sender, RoutedEventArgs e)
    {
        using var picker = new Forms.FolderBrowserDialog
        {
            Description = "Select a PetGPT character pack folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (picker.ShowDialog() == Forms.DialogResult.OK)
            await ImportAsync(picker.SelectedPath);
    }

    private async void OnImportArchive(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import PetGPT character pack",
            Filter = "PetGPT packs (*.petpack)|*.petpack",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) == true)
            await ImportAsync(picker.FileName);
    }

    private async Task ImportAsync(string path)
    {
        ImportStatusText.Text = "Validating and importing…";
        try
        {
            var result = await _importAsync(path, CancellationToken.None);
            if (!result.Succeeded || result.Pack is null)
            {
                ImportStatusText.Text = "Import failed: " +
                    string.Join(", ", result.DiagnosticCodes.Take(8));
                return;
            }
            RefreshPacks(
                _getPacks(),
                _working.SelectedPetId,
                _working.SelectedPackVersions.GetValueOrDefault(_working.SelectedPetId));
            ImportStatusText.Text = $"Imported {result.Pack.Id}@{result.Pack.Version}. Choose it explicitly, then Apply.";
        }
        catch (OperationCanceledException)
        {
            ImportStatusText.Text = "Import cancelled.";
        }
        catch
        {
            ImportStatusText.Text = "Import failed: import_failed";
        }
    }

    private sealed class CharacterChoice(CharacterPack pack)
    {
        public CharacterPack Pack { get; } = pack;
        public override string ToString() => $"{Pack.DisplayName} — {Pack.Id}@{Pack.Version}";
    }
}

internal sealed record ProjectInstructionsCopyResult(bool Succeeded, string? DiagnosticCode);

internal static class ProjectInstructionsCopy
{
    private const string ResourceName = "PetGPT.ProjectInstructions.md";

    public static ProjectInstructionsCopyResult TryCopy(Func<string, bool> writeClipboard)
    {
        ArgumentNullException.ThrowIfNull(writeClipboard);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException();
            using var reader = new StreamReader(stream);
            return writeClipboard(reader.ReadToEnd())
                ? new ProjectInstructionsCopyResult(true, null)
                : new ProjectInstructionsCopyResult(false, "clipboard_unavailable");
        }
        catch
        {
            return new ProjectInstructionsCopyResult(false, "clipboard_unavailable");
        }
    }
}
