using System.Reflection;
using System.Text;
using PetGPT.Characters;
using PetGPT.Models;
using PetGPT.Services;
using PetGPT.Shell;
using PetGPT.Windows;
using Xunit;

namespace PetGPT.Tests;

public sealed class NavigationAndCommandsTests
{
    private static readonly Uri Root = new("https://chatgpt.com/");
    private static readonly Uri ProjectHome = new("https://chatgpt.com/g/opaque-project/project");
    private static readonly Uri ProjectConversation = new("https://chatgpt.com/g/opaque-project/c/opaque-chat");

    [Theory]
    [InlineData("/pet", LocalCommandKind.OpenPetChooser, null)]
    [InlineData("/pet list", LocalCommandKind.ListPets, null)]
    [InlineData("/pet trixie", LocalCommandKind.SelectPet, "trixie")]
    [InlineData("/pet sleep", LocalCommandKind.Sleep, null)]
    [InlineData("/pet wake", LocalCommandKind.Wake, null)]
    [InlineData("/theme", LocalCommandKind.OpenThemeSettings, null)]
    [InlineData("/history", LocalCommandKind.History, null)]
    [InlineData("/new", LocalCommandKind.NewPetChat, null)]
    [InlineData("  /PET TRIXIE  ", LocalCommandKind.SelectPet, "trixie")]
    [InlineData("/ThEmE", LocalCommandKind.OpenThemeSettings, null)]
    public void LocalCommands_ParseToTypedLocalIntents(
        string input,
        LocalCommandKind expectedKind,
        string? expectedPetId)
    {
        var result = LocalCommandRouter.Parse(input);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Intent);
        Assert.Equal(expectedKind, result.Intent.Kind);
        Assert.Equal(expectedPetId, result.Intent.PetId);
        Assert.False(result.SubmitToChatGpt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/unknown")]
    [InlineData("/pet trixie | calc")]
    [InlineData("/new && something")]
    [InlineData("/history; /new")]
    [InlineData("$(calc)")]
    [InlineData("`command`")]
    [InlineData("/pet ../foo")]
    [InlineData("/pet ..\\foo")]
    [InlineData("/pet trixie\n/new")]
    [InlineData("/pet trixie\r/new")]
    [InlineData("/pet trixie extra")]
    [InlineData("pet trixie")]
    public void LocalCommands_RejectInvalidOrShellLookingInputWithoutRemoteFallback(string input)
    {
        var result = LocalCommandRouter.Parse(input);

        Assert.False(result.Succeeded);
        Assert.Null(result.Intent);
        Assert.False(result.SubmitToChatGpt);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void LocalCommands_RejectInputOver128Characters()
    {
        var result = LocalCommandRouter.Parse("/pet " + new string('a', 124));

        Assert.False(result.Succeeded);
        Assert.False(result.SubmitToChatGpt);
    }

    [Theory]
    [InlineData("/new", NavigationIntent.NewPetChat)]
    [InlineData("/history", NavigationIntent.History)]
    public async Task LocalCommandRouting_UsesExistingTypedNavigationOwner(
        string input,
        NavigationIntent expected)
    {
        var harness = new CommandHarness();

        var result = await harness.Router.ExecuteAsync(input);

        Assert.True(result.Succeeded);
        Assert.Equal([expected], harness.NavigationIntents);
        Assert.False(result.SubmitToChatGpt);
    }

    [Theory]
    [InlineData("/pet sleep", true)]
    [InlineData("/pet wake", false)]
    public async Task LocalCommandRouting_SleepWakeEmitOnlyTypedLocalPetEvents(
        string input,
        bool expectedSleeping)
    {
        var harness = new CommandHarness();

        var result = await harness.Router.ExecuteAsync(input);

        var sleep = Assert.IsType<PetEvent.SleepChanged>(Assert.Single(harness.PetEvents));
        Assert.Equal(expectedSleeping, sleep.IsSleeping);
        Assert.Empty(harness.NavigationIntents);
        Assert.False(result.SubmitToChatGpt);
    }

    [Fact]
    public async Task LocalCommandRouting_SelectPetUsesSelectionAuthorityWithCanonicalId()
    {
        var harness = new CommandHarness();

        var result = await harness.Router.ExecuteAsync("/PET TRIXIE");

        Assert.True(result.Succeeded);
        Assert.Equal(["trixie"], harness.SelectedPetIds);
        Assert.False(result.SubmitToChatGpt);
    }

    [Fact]
    public async Task LocalCommandRouting_ThemeOpensSettingsOnly()
    {
        var harness = new CommandHarness();

        var result = await harness.Router.ExecuteAsync("/theme");

        Assert.True(result.Succeeded);
        Assert.Equal(1, harness.ThemeSettingsCalls);
        Assert.Empty(harness.NavigationIntents);
        Assert.Empty(harness.SelectedPetIds);
        Assert.False(result.SubmitToChatGpt);
    }

    [Fact]
    public void SettingsWorkingCopy_IsDetachedFromLiveSettings()
    {
        var live = new AppSettings { CompactMode = true, ChatHomeUrl = null };

        var working = live.Copy();
        working.CompactMode = false;
        working.ChatHomeUrl = ProjectHome.AbsoluteUri;

        Assert.True(live.CompactMode);
        Assert.Null(live.ChatHomeUrl);
    }

    [Fact]
    public async Task SettingsApply_InvalidCandidateBlocksAllCommit()
    {
        var harness = new SettingsApplyHarness { ValidationCode = "home_invalid" };
        var candidate = harness.Live.Copy();
        candidate.ChatHomeUrl = ProjectConversation.AbsoluteUri;

        var result = await harness.Coordinator.ApplyAsync(candidate, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("home_invalid", result.DiagnosticCode);
        Assert.Empty(harness.RuntimeSnapshots);
        Assert.Equal(0, harness.PersistCalls);
        Assert.Null(harness.Live.ChatHomeUrl);
    }

    [Fact]
    public async Task SettingsApply_RuntimeFailureRestoresPersistedAndRuntimeSnapshots()
    {
        var harness = new SettingsApplyHarness { RuntimeSucceeds = false };
        var candidate = harness.Live.Copy();
        candidate.CompactMode = false;

        var result = await harness.Coordinator.ApplyAsync(candidate, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(2, harness.RuntimeSnapshots.Count);
        Assert.False(harness.RuntimeSnapshots[0].CompactMode);
        Assert.True(harness.RuntimeSnapshots[1].CompactMode);
        Assert.Equal(2, harness.PersistCalls);
        Assert.False(harness.PersistedSnapshots[0].CompactMode);
        Assert.True(harness.PersistedSnapshots[1].CompactMode);
        Assert.True(harness.Live.CompactMode);
    }

    [Fact]
    public async Task SettingsApply_PersistenceFailureRestoresPreviousRuntimeAndLiveSnapshot()
    {
        var harness = new SettingsApplyHarness { PersistenceSucceeds = false };
        var candidate = harness.Live.Copy();
        candidate.ThemesEnabled = false;

        var result = await harness.Coordinator.ApplyAsync(candidate, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Single(harness.RuntimeSnapshots);
        Assert.True(harness.RuntimeSnapshots[0].ThemesEnabled);
        Assert.Equal(1, harness.PersistCalls);
        Assert.True(harness.Live.ThemesEnabled);
    }

    [Fact]
    public async Task SettingsApply_ValidCandidatePersistsOnceThenCommitsCompleteLiveSnapshot()
    {
        var harness = new SettingsApplyHarness();
        var candidate = harness.Live.Copy();
        candidate.ChatHomeUrl = ProjectHome.AbsoluteUri;
        candidate.CompactMode = false;
        candidate.PetOptions["legacy"] = new PetOptionSettings { Scale = 2, ReducedMotion = true };

        var result = await harness.Coordinator.ApplyAsync(candidate, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.AppliedSnapshot);
        Assert.Single(harness.RuntimeSnapshots);
        Assert.Equal(1, harness.PersistCalls);
        Assert.Equal(ProjectHome.AbsoluteUri, harness.Live.ChatHomeUrl);
        Assert.False(harness.Live.CompactMode);
        Assert.Equal(2, harness.Live.PetOptions["legacy"].Scale);
        Assert.True(harness.Live.PetOptions["legacy"].ReducedMotion);
    }

    [Fact]
    public async Task SettingsApply_PreparationEnrichmentIsPersistedCommittedAndReturnedForDialogRebase()
    {
        var harness = new SettingsApplyHarness
        {
            Prepare = snapshot =>
            {
                snapshot.ChatWindow.PlacementMode = "Free";
                snapshot.ChatWindow.MonitorId = "DISPLAY-2";
                snapshot.ChatWindow.XWithinWorkAreaDip = 120;
                snapshot.ChatWindow.YWithinWorkAreaDip = 80;
                snapshot.PetPlacement.MonitorId = "DISPLAY-2";
                snapshot.PetPlacement.XWithinWorkAreaDip = 240;
                snapshot.PetPlacement.YWithinWorkAreaDip = 160;
                return true;
            }
        };

        var result = await harness.Coordinator.ApplyAsync(harness.Live.Copy(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(120, result.AppliedSnapshot!.ChatWindow.XWithinWorkAreaDip);
        Assert.Equal(240, result.AppliedSnapshot.PetPlacement.XWithinWorkAreaDip);
        Assert.Equal(120, harness.PersistedSnapshots.Single().ChatWindow.XWithinWorkAreaDip);
        Assert.Equal(240, harness.Live.PetPlacement.XWithinWorkAreaDip);
    }

    [Fact]
    public async Task SettingsApply_RuntimeGeometrySaveCannotQueuePreCommitPreferences()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path, TimeSpan.FromHours(1));
        var live = new AppSettings { CompactMode = true };
        var coordinator = new SettingsApplyCoordinator(
            live,
            _ => null,
            (_, _) => Task.FromResult(true),
            (_, _, _) =>
            {
                service.RequestSave(live);
                return Task.FromResult(true);
            },
            async (snapshot, token) =>
                (await service.SaveImmediateAsync(snapshot, token)).Succeeded);
        var candidate = live.Copy();
        candidate.CompactMode = false;

        var result = await coordinator.ApplyAsync(candidate, CancellationToken.None);
        await service.FlushAsync(CancellationToken.None);
        var persisted = new SettingsService(directory.Path, TimeSpan.Zero).Load().Settings;

        Assert.True(result.Succeeded);
        Assert.False(persisted.CompactMode);
    }

    [Fact]
    public async Task SettingsApply_GeometrySaveDuringImmediateWriteCannotQueuePreCommitPreferences()
    {
        using var directory = new TemporaryDirectory();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SettingsService(
            directory.Path,
            TimeSpan.FromHours(1),
            async token =>
            {
                writeEntered.TrySetResult();
                await releaseWrite.Task.WaitAsync(token);
            });
        var live = new AppSettings { CompactMode = true };
        var coordinator = new SettingsApplyCoordinator(
            live,
            _ => null,
            (_, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult(true),
            async (snapshot, token) =>
                (await service.SaveImmediateAsync(snapshot, token)).Succeeded);
        var candidate = live.Copy();
        candidate.CompactMode = false;

        var pending = coordinator.ApplyAsync(candidate, CancellationToken.None);
        await writeEntered.Task;
        live.ChatWindow.PlacementMode = "Free";
        live.ChatWindow.XWithinWorkAreaDip = 333;
        live.ChatWindow.YWithinWorkAreaDip = 222;
        service.RequestSave(live);
        releaseWrite.TrySetResult();
        var result = await pending;
        Assert.True(result.Succeeded);
        await service.FlushAsync(CancellationToken.None);
        var persisted = new SettingsService(directory.Path, TimeSpan.Zero).Load().Settings;

        Assert.False(persisted.CompactMode);
        Assert.Equal(333, result.AppliedSnapshot!.ChatWindow.XWithinWorkAreaDip);
        Assert.Equal(333, persisted.ChatWindow.XWithinWorkAreaDip);
    }

    [Fact]
    public async Task SettingsApply_RuntimeReceivesPreviousSnapshotForFinalIdentityCommit()
    {
        var live = new AppSettings();
        live.Roleplay.Enabled = true;
        AppSettings? runtimePrevious = null;
        AppSettings? runtimeCandidate = null;
        var coordinator = new SettingsApplyCoordinator(
            live,
            _ => null,
            (_, _) => Task.FromResult(true),
            (candidate, previous, _) =>
            {
                runtimeCandidate = candidate.Copy();
                runtimePrevious = previous.Copy();
                return Task.FromResult(true);
            },
            (_, _) => Task.FromResult(true));
        var requested = live.Copy();
        requested.Roleplay.Enabled = false;

        var result = await coordinator.ApplyAsync(requested, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(runtimePrevious!.Roleplay.Enabled);
        Assert.False(runtimeCandidate!.Roleplay.Enabled);
    }

    [Fact]
    public async Task SettingsApply_CancellationRollsBackRuntimeAndDoesNotMutateLiveSnapshot()
    {
        var live = new AppSettings();
        var runtime = new List<AppSettings>();
        var coordinator = new SettingsApplyCoordinator(
            live,
            _ => null,
            (snapshot, _) => Task.FromResult(true),
            (snapshot, _, _) =>
            {
                runtime.Add(snapshot.Copy());
                return Task.FromResult(true);
            },
            async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            });
        var candidate = live.Copy();
        candidate.CompactMode = false;
        using var cancellation = new CancellationTokenSource();
        var pending = coordinator.ApplyAsync(candidate, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(runtime);
        Assert.True(runtime[0].CompactMode);
        Assert.True(live.CompactMode);
    }

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(1.0, true)]
    [InlineData(2.0, true)]
    [InlineData(0.49, false)]
    [InlineData(2.01, false)]
    public void SettingsCandidateValidation_EnforcesPerPetScaleRange(double scale, bool expected)
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path, TimeSpan.FromHours(1));
        var candidate = new AppSettings();
        candidate.PetOptions["legacy"] = new PetOptionSettings { Scale = scale };

        var result = service.ValidateCandidate(candidate);

        Assert.Equal(expected, result.Succeeded);
    }

    [Fact]
    public async Task SettingsImmediateSave_ValidatesAndFlushesCompleteSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var service = new SettingsService(directory.Path, TimeSpan.FromHours(1));
        var candidate = new AppSettings
        {
            ChatHomeUrl = ProjectHome.AbsoluteUri,
            CompactMode = false
        };

        var result = await service.SaveImmediateAsync(candidate, CancellationToken.None);
        var loaded = new SettingsService(directory.Path, TimeSpan.Zero).Load().Settings;

        Assert.True(result.Succeeded);
        Assert.Equal(ProjectHome.AbsoluteUri, loaded.ChatHomeUrl);
        Assert.False(loaded.CompactMode);
    }

    [Fact]
    public async Task SettingsImmediateSave_PreservesNewerSnapshotQueuedWhileAtomicWriteIsBlocked()
    {
        using var directory = new TemporaryDirectory();
        var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SettingsService(
            directory.Path,
            TimeSpan.FromHours(1),
            async cancellationToken =>
            {
                writeEntered.TrySetResult();
                await releaseWrite.Task.WaitAsync(cancellationToken);
            });
        var immediate = new AppSettings { CompactMode = false };
        var newer = new AppSettings { CompactMode = true, ThemesEnabled = false };

        var pending = service.SaveImmediateAsync(immediate, CancellationToken.None);
        await writeEntered.Task;
        service.RequestSave(newer);
        releaseWrite.TrySetResult();
        Assert.True((await pending).Succeeded);
        await service.FlushAsync(CancellationToken.None);
        var loaded = new SettingsService(directory.Path, TimeSpan.Zero).Load().Settings;

        Assert.True(loaded.CompactMode);
        Assert.False(loaded.ThemesEnabled);
    }

    [Fact]
    public async Task SettingsMutationGate_SerializesSelectionBehindSettingsCommit()
    {
        using var gate = new SettingsMutationGate();
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionEntered = false;

        var apply = gate.RunAsync(async _ =>
        {
            applyEntered.TrySetResult();
            await releaseApply.Task;
            return true;
        }, CancellationToken.None);
        await applyEntered.Task;
        var selection = gate.RunAsync(_ =>
        {
            selectionEntered = true;
            return Task.FromResult(true);
        }, CancellationToken.None);

        await Task.Yield();
        Assert.False(selectionEntered);
        releaseApply.TrySetResult();
        Assert.True(await apply);
        Assert.True(await selection);
        Assert.True(selectionEntered);
    }

    [Fact]
    public void SettingsPetPresentationPlan_UsesCandidateScaleAndMotionForChangedExactPack()
    {
        var candidate = new AppSettings { SelectedPetId = "robot" };
        candidate.SelectedPackVersions["robot"] = "2.0.0";
        candidate.PetOptions["robot"] = new PetOptionSettings
        {
            Scale = 1.75,
            ReducedMotion = true
        };

        var plan = SettingsPetPresentationPlan.Create(
            candidate,
            currentPetId: "legacy",
            currentVersion: "1.0.0");

        Assert.True(plan.RequiresSelection);
        Assert.Equal("robot", plan.PetId);
        Assert.Equal("2.0.0", plan.Version);
        Assert.Equal(1.75, plan.Scale);
        Assert.True(plan.ReducedMotion);
    }

    [Fact]
    public void HomeRuntimeReconfiguration_ChangesFutureNavigationWithoutNavigatingCurrentPage()
    {
        var harness = new NavigationHarness(null, ProjectConversation);

        var result = harness.Service.ReconfigureHome(ProjectHome.AbsoluteUri);

        Assert.True(result);
        Assert.True(harness.Service.HasConfiguredHome);
        Assert.Empty(harness.Navigations);
    }

    [Fact]
    public void HomeRuntimeReconfiguration_ClearingHomeDisablesNewAndHistoryWithoutNavigation()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation);

        var result = harness.Service.ReconfigureHome(null);

        Assert.True(result);
        Assert.False(harness.Service.HasConfiguredHome);
        Assert.Empty(harness.Navigations);
    }

    [Fact]
    public void HomeRuntimeReconfiguration_RejectsConversationUrlAndKeepsPriorHome()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation);

        var result = harness.Service.ReconfigureHome(ProjectConversation.AbsoluteUri);

        Assert.False(result);
        Assert.True(harness.Service.HasConfiguredHome);
        Assert.Empty(harness.Navigations);
    }

    [Fact]
    public void UninitializedBrowserHomeReconfiguration_ChangesOnlyFutureInitialTarget()
    {
        var state = new ChatWebViewNavigationState(null);

        var result = state.ReconfigureHome(ProjectHome.AbsoluteUri);

        Assert.True(result);
        Assert.Equal(ProjectHome, state.InitialNavigationUri);
        Assert.Null(state.CurrentUri);
    }

    [Fact]
    public void ProjectInstructionsCopy_UsesOnlyBundledAppOwnedInstructions()
    {
        string? copied = null;

        var result = ProjectInstructionsCopy.TryCopy(text =>
        {
            copied = text;
            return true;
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(copied);
        Assert.StartsWith("# PetChats Project Instructions", copied, StringComparison.Ordinal);
        Assert.Contains("PetGPT does not edit the project", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("%LOCALAPPDATA%", copied, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectInstructionsCopy_ClipboardFailureIsBoundedAndLocal()
    {
        var result = ProjectInstructionsCopy.TryCopy(_ => false);

        Assert.False(result.Succeeded);
        Assert.Equal("clipboard_unavailable", result.DiagnosticCode);
    }

    [Theory]
    [InlineData(0.5, 75, 50)]
    [InlineData(1.0, 150, 100)]
    [InlineData(2.0, 300, 200)]
    public void PetScalePolicy_ScalesPresentationWithoutChangingAspectRatio(
        double scale,
        double expectedWidth,
        double expectedHeight)
    {
        var size = PetScalePolicy.GetScaledSize(new CharacterPresentation(150, 100, 0.5, 1), scale);

        Assert.Equal(expectedWidth, size.Width);
        Assert.Equal(expectedHeight, size.Height);
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(2.01)]
    [InlineData(double.NaN)]
    public void PetScalePolicy_RejectsInvalidScale(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PetScalePolicy.GetScaledSize(new CharacterPresentation(150, 100, 0.5, 1), scale));
    }

    [Theory]
    [InlineData("https://chatgpt.com/")]
    [InlineData("https://chatgpt.com/g/opaque-project/project")]
    [InlineData("https://chatgpt.com/g/a%2Db/project")]
    public void SafeChatGptUrl_AcceptsExactHttpsOriginWithoutUnsafeComponents(string value)
    {
        Assert.True(ChatNavigationUrlPolicy.TryValidateSafeUrl(value, out var uri));
        Assert.Equal("chatgpt.com", uri.Host);
    }

    [Fact]
    public void ConfiguredHome_AcceptsOnlyOpaqueProjectLanding()
    {
        Assert.True(ChatNavigationUrlPolicy.TryValidateHome(ProjectHome.AbsoluteUri, out var home));
        Assert.Equal(ProjectHome, home);
        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(ProjectConversation.AbsoluteUri, out _));
        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(Root.AbsoluteUri, out _));
    }

    [Theory]
    [InlineData("http://chatgpt.com/g/x/project")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,x")]
    [InlineData("file:///c:/temp/x")]
    [InlineData("https://chatgpt.com.evil.example/g/x/project")]
    [InlineData("https://evil-chatgpt.com/g/x/project")]
    [InlineData("https://www.chatgpt.com/g/x/project")]
    [InlineData("https://sub.chatgpt.com/g/x/project")]
    [InlineData("https://chatgpt.com:444/g/x/project")]
    [InlineData("https://user:password@chatgpt.com/g/x/project")]
    [InlineData("https://chatgpt.com/g/x/project?mode=history")]
    [InlineData("https://chatgpt.com/g/x/project#fragment")]
    [InlineData("https://chatgpt.com/auth/login")]
    [InlineData("https://chatgpt.com/login")]
    [InlineData("https://chatgpt.com/share/opaque")]
    [InlineData("https://chatgpt.com/c/opaque")]
    [InlineData("https://chatgpt.com/g/x/c/y")]
    [InlineData("https://chatgpt.com/g/x/files")]
    public void ConfiguredHome_RejectsUnsafeOrUnsupportedRoutes(string value)
    {
        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(value, out _));
    }

    [Theory]
    [InlineData("https://chatgpt.com/g/%/project")]
    [InlineData("https://chatgpt.com/g/%0/project")]
    [InlineData("https://chatgpt.com/g/%GG/project")]
    [InlineData("https://chatgpt.com/g/%0A/project")]
    [InlineData("https://chatgpt.com/g/%7F/project")]
    [InlineData("https://chatgpt.com/g/x/pro\u0001ject")]
    public void ConfiguredHome_RejectsMalformedPercentEncodingAndControls(string value)
    {
        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(value, out _));
    }

    [Fact]
    public void ConfiguredHome_RejectsOverlongUrl()
    {
        var value = "https://chatgpt.com/g/" + new string('a', 2048) + "/project";

        Assert.False(ChatNavigationUrlPolicy.TryValidateHome(value, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://www.chatgpt.com/g/x/project")]
    public void InitialNavigation_UnconfiguredOrInvalidFallsBackToRoot(string? value)
    {
        Assert.Equal(Root, ChatNavigationUrlPolicy.ResolveInitialNavigation(value));
    }

    [Fact]
    public void InitialNavigation_UsesConfiguredHomeAsTheOnlyFirstTarget()
    {
        var state = new ChatWebViewNavigationState(ProjectHome.AbsoluteUri);

        Assert.Equal(ProjectHome, state.InitialNavigationUri);
        Assert.NotEqual(Root, state.InitialNavigationUri);
        Assert.Null(state.CurrentUri);
    }

    [Theory]
    [InlineData("https://chatgpt.com/", ChatRouteKind.ChatRoot)]
    [InlineData("https://chatgpt.com/g/x/project", ChatRouteKind.ProjectLanding)]
    [InlineData("https://chatgpt.com/g/x/c/y", ChatRouteKind.ProjectConversation)]
    [InlineData("https://chatgpt.com/auth/login", ChatRouteKind.Authentication)]
    [InlineData("https://chatgpt.com/login", ChatRouteKind.Authentication)]
    [InlineData("https://example.com/", ChatRouteKind.Unrelated)]
    [InlineData("https://chatgpt.com/share/x", ChatRouteKind.UnknownChatGpt)]
    [InlineData("https://chatgpt.com/g/x/files", ChatRouteKind.UnknownChatGpt)]
    public void RouteClassification_IsTypedAndConservative(string value, ChatRouteKind expected)
    {
        Assert.Equal(expected, ChatNavigationUrlPolicy.Classify(new Uri(value)));
    }

    [Fact]
    public void ShowHideAndReloadState_PreserveObservedRoute()
    {
        var state = new ChatWebViewNavigationState(ProjectHome.AbsoluteUri);
        state.ObserveSource(ProjectConversation);

        state.OnVisibilityChanged(false);
        state.OnVisibilityChanged(true);

        Assert.Equal(ProjectConversation, state.CurrentUri);
        Assert.Equal(ProjectConversation, state.ReloadUri);
    }

    [Fact]
    public async Task NewPetChat_NavigatesDirectlyToExactConfiguredHome()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation);
        harness.Service.UpdateGuardState(GenerationCapabilityState.Idle, ComposerDraftState.Empty);

        var result = await harness.Service.NavigateAsync(NavigationIntent.NewPetChat, CancellationToken.None);

        Assert.Equal(NavigationResult.Navigated, result);
        Assert.Equal([ProjectHome], harness.Navigations);
        Assert.False(harness.Service.HistoryMode);
        Assert.Equal(0, harness.GlobalNewChatCalls);
    }

    [Fact]
    public async Task NewPetChat_WhenUnconfiguredDoesNotFakeGlobalCreation()
    {
        var harness = new NavigationHarness(null, Root);

        var result = await harness.Service.NavigateAsync(NavigationIntent.NewPetChat, CancellationToken.None);

        Assert.Equal(NavigationResult.InvalidHome, result);
        Assert.Empty(harness.Navigations);
        Assert.Equal(0, harness.GlobalNewChatCalls);
    }

    [Fact]
    public async Task History_NavigatesToProjectHomeAndSetsHistoryIntent()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation);
        harness.Service.UpdateGuardState(GenerationCapabilityState.Idle, ComposerDraftState.Empty);

        var result = await harness.Service.NavigateAsync(NavigationIntent.History, CancellationToken.None);

        Assert.Equal(NavigationResult.Navigated, result);
        Assert.Equal([ProjectHome], harness.Navigations);
        Assert.True(harness.Service.HistoryMode);
    }

    [Fact]
    public async Task InvalidHome_CannotNavigateToArbitraryUrl()
    {
        var harness = new NavigationHarness("https://example.com/project", ProjectConversation);

        var result = await harness.Service.NavigateAsync(NavigationIntent.History, CancellationToken.None);

        Assert.Equal(NavigationResult.InvalidHome, result);
        Assert.Empty(harness.Navigations);
    }

    [Theory]
    [InlineData(GenerationCapabilityState.Generating, ComposerDraftState.Empty, true)]
    [InlineData(GenerationCapabilityState.Idle, ComposerDraftState.NonEmpty, true)]
    [InlineData(GenerationCapabilityState.Idle, ComposerDraftState.Unknown, true)]
    [InlineData(GenerationCapabilityState.Idle, ComposerDraftState.Empty, false)]
    [InlineData(GenerationCapabilityState.Unknown, ComposerDraftState.Empty, false)]
    public void LeaveGuard_UsesPositiveGenerationAndConservativeDraftState(
        GenerationCapabilityState generation,
        ComposerDraftState draft,
        bool expected)
    {
        Assert.Equal(expected, ChatNavigationService.RequiresLeaveConfirmation(generation, draft));
    }

    [Fact]
    public async Task LeaveGuard_UserChoosesStayAndNavigationDoesNotOccur()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation)
        {
            ConfirmLeave = false
        };
        harness.Service.UpdateGuardState(GenerationCapabilityState.Generating, ComposerDraftState.Empty);

        var result = await harness.Service.NavigateAsync(NavigationIntent.NewPetChat, CancellationToken.None);

        Assert.Equal(NavigationResult.Stayed, result);
        Assert.Empty(harness.Navigations);
        Assert.Equal(1, harness.PromptCalls);
    }

    [Fact]
    public async Task LeaveGuard_UserChoosesLeaveAndNavigationOccursOnce()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation)
        {
            ConfirmLeave = true
        };
        harness.Service.UpdateGuardState(GenerationCapabilityState.Generating, ComposerDraftState.Unknown);

        var result = await harness.Service.NavigateAsync(NavigationIntent.History, CancellationToken.None);

        Assert.Equal(NavigationResult.Navigated, result);
        Assert.Single(harness.Navigations);
        Assert.Equal(1, harness.PromptCalls);
    }

    [Theory]
    [InlineData("https://chatgpt.com/", true)]
    [InlineData("https://chatgpt.com/g/x/project", true)]
    [InlineData("https://chatgpt.com/auth/login", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://petgpt.invalid/", false)]
    public void EnhancementEligibility_RequiresExactOriginAndNonAuthenticationRoute(string value, bool expected)
    {
        Assert.Equal(expected, ChatNavigationUrlPolicy.IsEnhancementEligible(new Uri(value)));
    }

    [Theory]
    [InlineData(false, false, true, WebDocumentTransition.TeardownAndDisable)]
    [InlineData(true, true, false, WebDocumentTransition.AwaitNavigationCompletion)]
    [InlineData(true, false, true, WebDocumentTransition.AdvanceRoute)]
    [InlineData(true, false, false, WebDocumentTransition.AttachSameDocument)]
    public void SourceTransitionPolicy_HandlesEligibilityLossAndSameDocumentReturn(
        bool eligible,
        bool isNewDocument,
        bool hasCurrentDocument,
        WebDocumentTransition expected)
    {
        Assert.Equal(
            expected,
            WebDocumentTransitionPolicy.Decide(eligible, isNewDocument, hasCurrentDocument));
    }

    [Fact]
    public void Bridge_AcceptsClosedReadyMessageForCurrentDocumentOnly()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");

        var result = bridge.AcceptMessage(
            ProjectConversation.AbsoluteUri,
            ProjectConversation,
            ReadyMessage(document));

        Assert.Equal(BridgeMessageResult.Accepted, result);
        Assert.True(bridge.Capabilities.Generation);
        Assert.True(bridge.Capabilities.Submission);
        Assert.False(bridge.Capabilities.ComposerEmpty);
    }

    [Fact]
    public void Bridge_RejectsOldDocumentAndRouteRevision()
    {
        using var bridge = NewBridge();
        var old = bridge.BeginDocument(ProjectHome, "0123456789abcdef");
        bridge.AdvanceRoute(ProjectConversation);

        Assert.Equal(
            BridgeMessageResult.StaleDocument,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(old)));
    }

    [Fact]
    public void Bridge_RejectsOversizedUnknownAndUntrustedMessages()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var unknownProperty = ReadyMessage(document).Replace(
            "\"payload\":",
            "\"unexpected\":true,\"payload\":",
            StringComparison.Ordinal);

        Assert.Equal(
            BridgeMessageResult.Oversized,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, new string('x', 2049)));
        Assert.Equal(
            BridgeMessageResult.InvalidSchema,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, unknownProperty));
        Assert.Equal(
            BridgeMessageResult.InvalidOrigin,
            bridge.AcceptMessage("https://example.com/", ProjectConversation, ReadyMessage(document)));
        Assert.Equal(
            BridgeMessageResult.InvalidOrigin,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, new Uri("https://example.com/"), ReadyMessage(document)));
    }

    [Fact]
    public void Bridge_RateLimitsPageStatusBursts()
    {
        var now = TimeSpan.Zero;
        using var bridge = new WebViewBridge(() => now);
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var ready = ReadyMessage(document);

        for (var index = 0; index < 40; index++)
            Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));

        Assert.Equal(
            BridgeMessageResult.RateLimited,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));
        now = TimeSpan.FromMilliseconds(50);
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));
    }

    [Fact]
    public void Bridge_CommandMessageCannotInvokeNavigation()
    {
        var harness = new NavigationHarness(ProjectHome.AbsoluteUri, ProjectConversation);
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var command = Envelope(document, "command", "{\"name\":\"navigate\"}");

        var result = bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, command);

        Assert.Equal(BridgeMessageResult.UnsupportedKind, result);
        Assert.Empty(harness.Navigations);
    }

    [Fact]
    public void Bridge_UnsupportedComposerCapabilityRemainsUnknown()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document)));

        var composer = Envelope(document, "activity", "{\"event\":\"composer\",\"empty\":true}");

        Assert.Equal(
            BridgeMessageResult.CapabilityUnavailable,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, composer));
        Assert.Equal(ComposerDraftState.Unknown, bridge.ComposerDraftState);
    }

    [Fact]
    public void Bridge_SyntheticComposerBooleanCapabilityCanReportNonEmptyWithoutText()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var ready = ReadyMessage(document, composerEmpty: true);
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));

        var composer = Envelope(document, "activity", "{\"event\":\"composer\",\"empty\":false}");

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, composer));
        Assert.Equal(ComposerDraftState.NonEmpty, bridge.ComposerDraftState);
    }

    [Fact]
    public void Bridge_MissingGenerationSignalIsUnknownUntilObserved()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, generation: false)));

        Assert.Equal(GenerationCapabilityState.Unknown, bridge.GenerationState);
    }

    [Fact]
    public void Bridge_GenerationCapabilityReportsTypedStateOnly()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, generation: true)));

        var generating = Envelope(document, "activity", "{\"event\":\"generation\",\"state\":\"generating\"}");
        var idle = Envelope(document, "activity", "{\"event\":\"generation\",\"state\":\"idle\"}");

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, generating));
        Assert.Equal(GenerationCapabilityState.Generating, bridge.GenerationState);
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, idle));
        Assert.Equal(GenerationCapabilityState.Idle, bridge.GenerationState);
    }

    [Fact]
    public void Bridge_RevokedGenerationCapabilityResetsStateToUnknown()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, generation: true)));
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(
                ProjectConversation.AbsoluteUri,
                ProjectConversation,
                Envelope(document, "activity", "{\"event\":\"generation\",\"state\":\"generating\"}")));

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, generation: false)));

        Assert.Equal(GenerationCapabilityState.Unknown, bridge.GenerationState);
    }

    [Fact]
    public void Bridge_RevokedComposerCapabilityResetsStateToUnknown()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, composerEmpty: true)));
        Assert.Equal(
            BridgeMessageResult.Accepted,
            bridge.AcceptMessage(
                ProjectConversation.AbsoluteUri,
                ProjectConversation,
                Envelope(document, "activity", "{\"event\":\"composer\",\"empty\":true}")));

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document, composerEmpty: false)));

        Assert.Equal(ComposerDraftState.Unknown, bridge.ComposerDraftState);
    }

    [Fact]
    public void Bridge_RepeatedReadyConfigurationIsIdempotent()
    {
        using var bridge = NewBridge();
        var events = 0;
        bridge.StatusChanged += (_, _) => events++;
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        var ready = ReadyMessage(document);

        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ready));

        Assert.Equal(1, events);
    }

    [Fact]
    public void Bridge_TeardownPreventsLateStatusUpdate()
    {
        using var bridge = NewBridge();
        var document = bridge.BeginDocument(ProjectConversation, "0123456789abcdef");
        bridge.InvalidateDocument();

        Assert.Equal(
            BridgeMessageResult.StaleDocument,
            bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(document)));
        Assert.False(bridge.Capabilities.Generation);
    }

    [Fact]
    public void Bridge_SpaRouteRevisionRequiresFreshCapabilityAttachment()
    {
        using var bridge = NewBridge();
        var landing = bridge.BeginDocument(ProjectHome, "0123456789abcdef");
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectHome.AbsoluteUri, ProjectHome, ReadyMessage(landing)));

        var conversation = bridge.AdvanceRoute(ProjectConversation);

        Assert.Equal(landing.DocumentSession, conversation.DocumentSession);
        Assert.Equal(landing.RouteRevision + 1, conversation.RouteRevision);
        Assert.False(bridge.Capabilities.Generation);
        Assert.Equal(BridgeMessageResult.Accepted, bridge.AcceptMessage(ProjectConversation.AbsoluteUri, ProjectConversation, ReadyMessage(conversation)));
    }

    [Fact]
    public void Bridge_PublicContractHasNoResponseTextExtractionApi()
    {
        var publicMembers = typeof(WebViewBridge).GetMembers(BindingFlags.Instance | BindingFlags.Public);

        Assert.DoesNotContain(publicMembers, member =>
            member.Name.Contains("ResponseText", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("Conversation", StringComparison.OrdinalIgnoreCase) ||
            member.Name.Contains("InnerHtml", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompactOff_RemovesOnlyCompactLayer()
    {
        var state = new AppearanceLayerState();
        state.Apply("compact", new ThemeLayer("theme", null));

        var operations = state.Apply(null, new ThemeLayer("theme", null));

        Assert.Equal([new AppearanceLayerOperation(AppearanceLayerKind.Compact, AppearanceOperation.Remove, null)], operations);
        Assert.Equal("theme", state.ThemeCss);
    }

    [Fact]
    public void ThemeOff_RemovesOnlyThemeLayer()
    {
        var state = new AppearanceLayerState();
        state.Apply("compact", new ThemeLayer("theme", null));

        var operations = state.Apply("compact", null);

        Assert.Equal([new AppearanceLayerOperation(AppearanceLayerKind.Theme, AppearanceOperation.Remove, null)], operations);
        Assert.Equal("compact", state.CompactCss);
    }

    [Fact]
    public void RepeatedAppearanceApply_IsIdempotent()
    {
        var state = new AppearanceLayerState();
        var theme = new ThemeLayer("theme", null);

        Assert.NotEmpty(state.Apply("compact", theme));
        Assert.Empty(state.Apply("compact", theme));
    }

    [Theory]
    [InlineData(ChatRouteKind.ProjectLanding, false, true, false)]
    [InlineData(ChatRouteKind.ProjectConversation, true, true, false)]
    [InlineData(ChatRouteKind.ProjectConversation, false, true, true)]
    [InlineData(ChatRouteKind.ProjectConversation, false, false, false)]
    public void CompactPolicy_RelaxesHistoryAndFailsOpenWithoutSelector(
        ChatRouteKind route,
        bool historyMode,
        bool selectorAvailable,
        bool expectedSuppression)
    {
        var result = CompactAppearancePolicy.Evaluate(true, route, historyMode, selectorAvailable);

        Assert.Equal(expectedSuppression, result.SuppressNavigation);
    }

    [Fact]
    public void ThemeService_LegacyOrDisabledThemeRemovesPreviousTheme()
    {
        var service = new ThemeService();
        var capabilities = ThemeSelectorCapabilities.All;

        Assert.Null(service.BuildLayer(null, enabled: true, capabilities));
        Assert.Null(service.BuildLayer(ValidTheme(), enabled: false, capabilities));
    }

    [Fact]
    public void ThemeService_PackSwitchChangesThemeWithoutNavigationOrReload()
    {
        var navigationCalls = 0;
        var reloadCalls = 0;
        var controller = new AppearanceController(_ => { }, () => navigationCalls++, () => reloadCalls++);

        controller.UpdateTheme(new ThemeService().BuildLayer(ValidTheme("#112233"), true, ThemeSelectorCapabilities.All));
        controller.UpdateTheme(new ThemeService().BuildLayer(ValidTheme("#334455"), true, ThemeSelectorCapabilities.All));

        Assert.Equal(0, navigationCalls);
        Assert.Equal(0, reloadCalls);
        Assert.Contains("#334455", controller.State.ThemeCss, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeService_PartialForegroundBackgroundCapabilitySkipsPair()
    {
        var layer = new ThemeService().BuildLayer(
            ValidTheme(),
            enabled: true,
            new ThemeSelectorCapabilities(PageSurfacePair: false, SecondarySurfacePair: false, ComposerPair: true, Scrollbar: true));

        Assert.NotNull(layer);
        Assert.DoesNotContain("--petgpt-page-background", layer.Css, StringComparison.Ordinal);
        Assert.DoesNotContain("--petgpt-page-text", layer.Css, StringComparison.Ordinal);
        Assert.Contains("--petgpt-composer-background", layer.Css, StringComparison.Ordinal);
        Assert.Contains("--petgpt-composer-text", layer.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeService_InvalidTokenCannotInjectCss()
    {
        var colors = ValidColors();
        colors["accent"] = "red;} body{display:none";
        var tokens = new ThemeTokens("tokens.json", colors, ValidMetrics(), null);

        Assert.Null(new ThemeService().BuildLayer(tokens, true, ThemeSelectorCapabilities.All));
    }

    [Fact]
    public void ThemeService_DecorationIsReencodedAndNeverExposesPathOrNetworkUrl()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Pets", "legacy", "assets", "idle.png");
        var tokens = new ThemeTokens(
            "tokens.json",
            ValidColors(),
            ValidMetrics(),
            new ThemeDecoration(path, 20, "corner"));

        var layer = new ThemeService().BuildLayer(tokens, true, ThemeSelectorCapabilities.All);

        Assert.NotNull(layer);
        Assert.StartsWith("data:image/png;base64,", layer.DecorationDataUri, StringComparison.Ordinal);
        Assert.DoesNotContain(path, layer.Css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", layer.Css, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", layer.Css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThemeService_FailedDecorationKeepsSolidTheme()
    {
        var tokens = new ThemeTokens(
            "tokens.json",
            ValidColors(),
            ValidMetrics(),
            new ThemeDecoration(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"), 20, "background"));

        var layer = new ThemeService().BuildLayer(tokens, true, ThemeSelectorCapabilities.All);

        Assert.NotNull(layer);
        Assert.Null(layer.DecorationDataUri);
        Assert.Contains("--petgpt-page-background", layer.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenInBrowser_PrefersSafeCurrentChatGptUrl()
    {
        Assert.Equal(
            ProjectConversation,
            ChatNavigationUrlPolicy.ResolveExternalBrowserUri(ProjectConversation, ProjectHome.AbsoluteUri));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://petgpt.invalid/")]
    [InlineData("javascript:alert(1)")]
    public void OpenInBrowser_UnsafeCurrentFallsBackToHome(string current)
    {
        Assert.Equal(
            ProjectHome,
            ChatNavigationUrlPolicy.ResolveExternalBrowserUri(new Uri(current), ProjectHome.AbsoluteUri));
    }

    [Fact]
    public void OpenInBrowser_InvalidHomeFallsBackToRoot()
    {
        Assert.Equal(
            Root,
            ChatNavigationUrlPolicy.ResolveExternalBrowserUri(new Uri("https://example.com/"), "https://example.com/project"));
    }

    private static WebViewBridge NewBridge() => new();

    private static string ReadyMessage(
        BridgeDocumentIdentity document,
        bool generation = true,
        bool composerEmpty = false) =>
        Envelope(
            document,
            "ready",
            "{\"adapterVersion\":\"1\",\"capabilities\":{" +
            "\"generation\":" + generation.ToString().ToLowerInvariant() + "," +
            "\"composerEmpty\":" + composerEmpty.ToString().ToLowerInvariant() + "," +
            "\"pageSurface\":true,\"secondarySurface\":false," +
            "\"composerSurface\":true,\"scrollbar\":true," +
            "\"compactNavigation\":false,\"submission\":true}}");

    private static string Envelope(BridgeDocumentIdentity document, string kind, string payload) =>
        "{\"v\":1,\"kind\":\"" + kind + "\",\"documentSession\":\"" +
        document.DocumentSession + "\",\"routeRevision\":" + document.RouteRevision +
        ",\"payload\":" + payload + "}";

    private static ThemeTokens ValidTheme(string background = "#171D38") =>
        new("tokens.json", ValidColors(background), ValidMetrics(), null);

    private static Dictionary<string, string> ValidColors(string background = "#171D38") =>
        new(StringComparer.Ordinal)
        {
            ["background"] = background,
            ["surface"] = "#222B4A",
            ["text"] = "#F4F1FF",
            ["mutedText"] = "#C4CAE3",
            ["accent"] = "#B9A1FF",
            ["border"] = "#596388",
            ["composerBackground"] = "#292F50",
            ["composerText"] = "#F4F1FF",
            ["scrollbarThumb"] = "#8792C4"
        };

    private static Dictionary<string, int> ValidMetrics() =>
        new(StringComparer.Ordinal)
        {
            ["surfaceRadiusPx"] = 14,
            ["composerRadiusPx"] = 18,
            ["borderWidthPx"] = 1,
            ["scrollbarWidthPx"] = 8
        };

    private sealed class NavigationHarness
    {
        private Uri? _current;

        public NavigationHarness(string? home, Uri? current)
        {
            _current = current;
            Service = new ChatNavigationService(
                home,
                () => _current,
                uri =>
                {
                    Navigations.Add(uri);
                    _current = uri;
                },
                _ =>
                {
                    PromptCalls++;
                    return Task.FromResult(ConfirmLeave);
                });
            Service.ObserveSource(current);
        }

        public ChatNavigationService Service { get; }
        public List<Uri> Navigations { get; } = [];
        public bool ConfirmLeave { get; set; } = true;
        public int PromptCalls { get; private set; }
        public int GlobalNewChatCalls { get; private set; }
    }

    private sealed class CommandHarness
    {
        public CommandHarness()
        {
            Router = new LocalCommandRouter(new LocalCommandActions(
                OpenPetChooserAsync: () =>
                {
                    PetChooserCalls++;
                    return Task.CompletedTask;
                },
                GetInstalledPacks: () => [],
                SelectPetByIdAsync: (id, _) =>
                {
                    SelectedPetIds.Add(id);
                    return Task.FromResult(PetSelectionResult.Selected());
                },
                RaisePetEvent: PetEvents.Add,
                OpenThemeSettingsAsync: () =>
                {
                    ThemeSettingsCalls++;
                    return Task.CompletedTask;
                },
                NavigateAsync: (intent, _) =>
                {
                    NavigationIntents.Add(intent);
                    return Task.FromResult(NavigationResult.Navigated);
                }));
        }

        public LocalCommandRouter Router { get; }
        public List<NavigationIntent> NavigationIntents { get; } = [];
        public List<PetEvent> PetEvents { get; } = [];
        public List<string> SelectedPetIds { get; } = [];
        public int PetChooserCalls { get; private set; }
        public int ThemeSettingsCalls { get; private set; }
    }

    private sealed class SettingsApplyHarness
    {
        public SettingsApplyHarness()
        {
            Coordinator = new SettingsApplyCoordinator(
                Live,
                _ => ValidationCode,
                (snapshot, _) => Task.FromResult(Prepare(snapshot)),
                (snapshot, _, _) =>
                {
                    RuntimeSnapshots.Add(snapshot.Copy());
                    var succeeds = RuntimeSucceeds;
                    RuntimeSucceeds = true;
                    return Task.FromResult(succeeds);
                },
                (snapshot, _) =>
                {
                    PersistCalls++;
                    PersistedSnapshots.Add(snapshot.Copy());
                    return Task.FromResult(PersistenceSucceeds);
                });
        }

        public AppSettings Live { get; } = new();
        public SettingsApplyCoordinator Coordinator { get; }
        public List<AppSettings> RuntimeSnapshots { get; } = [];
        public List<AppSettings> PersistedSnapshots { get; } = [];
        public Func<AppSettings, bool> Prepare { get; set; } = _ => true;
        public string? ValidationCode { get; set; }
        public bool RuntimeSucceeds { get; set; } = true;
        public bool PersistenceSucceeds { get; set; } = true;
        public int PersistCalls { get; private set; }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PetGPT-T11-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
