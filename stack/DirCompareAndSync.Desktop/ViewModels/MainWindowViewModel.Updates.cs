using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DirCompareAndSync.Desktop.Deploy;
using DirCompareAndSync.Desktop.Services;
using DirCompareAndSync.Desktop.Views;

namespace DirCompareAndSync.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private const string CheckForUpdatesMenuDefault = "Verificar actualizações…";
    private const string CheckForUpdatesMenuReady = "Reiniciar para actualizar…";

    private readonly AppUpdateService _appUpdateService = new();
    private bool _startupUpdateCheckDone;
    private bool _startupReleaseNotesDone;
    private bool _isUpdateDownloadInProgress;
    private string? _pendingUpdateVersion;

    [ObservableProperty]
    private string _checkForUpdatesMenuHeader = CheckForUpdatesMenuDefault;

    public async Task RunStartupExperienceAsync()
    {
        if (_ownerWindow is null)
        {
            return;
        }

        RefreshUpdateMenuState();
        await CheckForUpdatesOnStartupAsync();
        await ShowReleaseNotesOnStartupIfNeededAsync();
    }

    public async Task CheckForUpdatesOnStartupAsync()
    {
        if (_ownerWindow is null || _startupUpdateCheckDone)
        {
            return;
        }

        _startupUpdateCheckDone = true;

        if (_pendingUpdateVersion is not null)
        {
            return;
        }

        var check = await _appUpdateService.CheckForUpdatesAsync();
        if (check.Status != AppUpdateStatus.UpdateAvailable)
        {
            return;
        }

        await PromptForUpdateAsync(check, fromStartup: true);
    }

    public async Task ShowReleaseNotesOnStartupIfNeededAsync()
    {
        if (_ownerWindow is null || _startupReleaseNotesDone)
        {
            return;
        }

        _startupReleaseNotesDone = true;

        if (_pendingUpdateVersion is not null)
        {
            return;
        }

        var updateCheck = await _appUpdateService.CheckForUpdatesAsync();
        if (updateCheck.Status == AppUpdateStatus.UpdateAvailable)
        {
            return;
        }

        var prefs = await _sessions.LoadAppPreferencesAsync(_storageRoot);
        if (string.Equals(
                prefs.LastSeenVersionDisplay,
                AppInfo.VersionDisplay,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fromVersion = prefs.LastSeenVersionDisplay;
        var notesRange = ReleaseNotesService.GetNotesSinceVersion(fromVersion, AppInfo.AssemblyVersion);
        if (notesRange.Count == 0)
        {
            var single = ReleaseNotesService.GetForCurrentVersion();
            if (single is null)
            {
                await _sessions.MarkVersionSeenAsync(_storageRoot, AppInfo.VersionDisplay);
                return;
            }

            notesRange = [single];
        }

        var fromLabel = ReleaseNotesService.FormatTechnicalVersion(fromVersion);
        var intro = notesRange.Count > 1 && !string.IsNullOrWhiteSpace(fromLabel)
            ? $"Bem-vindo ao DirCompareAndSync {AppInfo.VersionDisplay}.\n\nPrincipais alterações desde a versão {fromLabel}:"
            : $"Bem-vindo ao DirCompareAndSync {AppInfo.VersionDisplay}.\n\nPrincipais alterações nesta versão:";

        var title = ReleaseNotesService.BuildCumulativeNotesTitle(
            fromVersion,
            AppInfo.AssemblyVersion,
            notesRange.Count);

        await ShowReleaseNotesDialogAsync(title, intro, notesRange);
        await _sessions.MarkVersionSeenAsync(_storageRoot, AppInfo.VersionDisplay);
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_ownerWindow is null)
        {
            return;
        }

        if (_isUpdateDownloadInProgress)
        {
            var pendingLabel = ReleaseNotesService.FormatTechnicalVersion(_pendingUpdateVersion);
            var versionText = string.IsNullOrWhiteSpace(pendingLabel)
                ? "nova versão"
                : $"versão {pendingLabel}";
            StatusText = $"Download da {versionText} em curso…";
            var busyDialog = new MessageWindow(
                "Actualizações",
                $"O download da {versionText} já está em curso.\n\nAguarde a conclusão ou reinicie mais tarde em Ajuda → {CheckForUpdatesMenuHeader}.");
            await busyDialog.ShowDialog(_ownerWindow);
            return;
        }

        if (_pendingUpdateVersion is not null)
        {
            await PromptForRestartAfterDownloadAsync(_pendingUpdateVersion);
            return;
        }

        StatusText = "A verificar actualizações…";
        var check = await _appUpdateService.CheckForUpdatesAsync();

        if (check.Status == AppUpdateStatus.UpdateAvailable)
        {
            await PromptForUpdateAsync(check, fromStartup: false);
            return;
        }

        var message = AppUpdateOutcomeMessages.ToUserMessage(check);
        StatusText = message.Split('\n')[0];
        var infoDialog = new MessageWindow("Actualizações", message);
        await infoDialog.ShowDialog(_ownerWindow);
    }

    [RelayCommand]
    private async Task ShowReleaseNotesAsync()
    {
        if (_ownerWindow is null)
        {
            return;
        }

        try
        {
            await Task.Yield();

            var notes = ReleaseNotesService.GetForCurrentVersion();
            if (notes is null)
            {
                var dialog = new MessageWindow(
                    "Novidades da versão",
                    $"Não há notas de versão disponíveis para a versão {AppInfo.VersionDisplay}.");
                await dialog.ShowDialog(_ownerWindow);
                return;
            }

            await ShowReleaseNotesDialogAsync(
                $"Versão instalada: {AppInfo.VersionDisplay}",
                notes);
        }
        catch (Exception ex)
        {
            var dialog = new MessageWindow(
                "Novidades da versão",
                $"Não foi possível mostrar as notas de versão.\n\n{ex.Message}");
            await dialog.ShowDialog(_ownerWindow);
        }
    }

    private async Task PromptForUpdateAsync(AppUpdateOutcome check, bool fromStartup)
    {
        if (_ownerWindow is null)
        {
            return;
        }

        await Task.Yield();

        var versionLabel = ReleaseNotesService.FormatTechnicalVersion(check.Detail);
        if (string.IsNullOrWhiteSpace(versionLabel))
        {
            versionLabel = check.Detail ?? "nova versão";
        }

        StatusText = $"Nova versão {versionLabel} — a carregar novidades…";
        StatsBarText = StatusText;

        IReadOnlyList<ReleaseNotesEntry> releaseNotesRange;
        try
        {
            releaseNotesRange = await ReleaseNotesService.TryResolveRangeForUpdateAsync(
                check.Detail,
                AppInfo.AssemblyVersion);
        }
        catch
        {
            releaseNotesRange = [];
        }

        var source = AppDeployInfo.DescribeUpdateSource();
        var confirmMessage = string.IsNullOrWhiteSpace(source)
            ? $"Existe uma nova versão disponível: {versionLabel}\n\nDeseja descarregar agora?"
            : $"Existe uma nova versão disponível: {versionLabel}\n\nOrigem: {source}\n\nDeseja descarregar agora?";

        var confirm = new UpdateConfirmWindow(
            confirmMessage,
            releaseNotesRange,
            check.Detail,
            AppInfo.AssemblyVersion);
        if (await confirm.ShowDialog<bool>(_ownerWindow) != true)
        {
            StatusText = fromStartup
                ? $"Nova versão {versionLabel} disponível — actualização adiada. Ajuda → Verificar actualizações…"
                : "Actualização adiada. Ajuda → Verificar actualizações…";
            return;
        }

        await DownloadUpdateAsync(check.Detail);
    }

    private async Task DownloadUpdateAsync(string? version)
    {
        if (_ownerWindow is null)
        {
            return;
        }

        _isUpdateDownloadInProgress = true;
        _pendingUpdateVersion = version;
        CheckForUpdatesMenuHeader = BuildDownloadMenuHeader(version);
        StatusText = "A descarregar actualização…";
        StatsBarText = StatusText;

        try
        {
            var download = await _appUpdateService.DownloadUpdatesAsync(progress =>
            {
                StatusText = progress > 0
                    ? $"A descarregar actualização… {progress}%"
                    : "A descarregar actualização…";
                StatsBarText = StatusText;
            });

            if (download.Status == AppUpdateStatus.DownloadedPendingRestart)
            {
                SetUpdateReadyToRestart(download.Detail ?? version);
                await PromptForRestartAfterDownloadAsync(download.Detail ?? version);
                return;
            }

            ResetUpdateMenuState();
            var message = AppUpdateOutcomeMessages.ToUserMessage(download);
            StatusText = message.Split('\n')[0];
            var dialog = new MessageWindow("Actualizações", message);
            await dialog.ShowDialog(_ownerWindow);
        }
        catch (Exception ex)
        {
            ResetUpdateMenuState();
            StatusText = "Falha ao descarregar actualização.";
            var dialog = new MessageWindow(
                "Actualizações",
                $"Não foi possível descarregar a actualização.\n\n{ex.Message}");
            await dialog.ShowDialog(_ownerWindow);
        }
        finally
        {
            _isUpdateDownloadInProgress = false;
        }
    }

    private async Task PromptForRestartAfterDownloadAsync(string? version)
    {
        if (_ownerWindow is null)
        {
            return;
        }

        var versionLabel = ToDisplayVersionLabel(version, "nova");
        var dialog = new ConfirmDialogWindow(
            "Actualização pronta",
            $"A versão {versionLabel} foi descarregada.\n\nDeseja reiniciar a aplicação agora para concluir a actualização?",
            confirmText: "Reiniciar agora",
            cancelText: "Mais tarde");

        if (await dialog.ShowDialog<ConfirmDialogResult>(_ownerWindow) != ConfirmDialogResult.Confirm)
        {
            StatusText = $"Actualização {versionLabel} pronta — Ajuda → Reiniciar para actualizar…";
            StatsBarText = StatusText;
            return;
        }

        StatusText = "A reiniciar para aplicar actualização…";
        var apply = _appUpdateService.ApplyPendingRestart();
        if (apply.Status == AppUpdateStatus.AppliedPendingRestart)
        {
            AppendLog(AppUpdateOutcomeMessages.ToUserMessage(apply));
            return;
        }

        var message = AppUpdateOutcomeMessages.ToUserMessage(apply);
        StatusText = message.Split('\n')[0];
        var errorDialog = new MessageWindow("Actualizações", message);
        await errorDialog.ShowDialog(_ownerWindow);
    }

    private void RefreshUpdateMenuState()
    {
        var pending = _appUpdateService.GetPendingRestartState();
        if (pending.Status == AppUpdateStatus.DownloadedPendingRestart)
        {
            SetUpdateReadyToRestart(pending.Detail);
            return;
        }

        ResetUpdateMenuState();
    }

    private void SetUpdateReadyToRestart(string? version)
    {
        _pendingUpdateVersion = version;
        CheckForUpdatesMenuHeader = CheckForUpdatesMenuReady;
        if (string.IsNullOrWhiteSpace(StatusText) ||
            StatusText.StartsWith("Pronto", StringComparison.OrdinalIgnoreCase))
        {
            var label = ReleaseNotesService.FormatTechnicalVersion(version);
            StatusText = string.IsNullOrWhiteSpace(label)
                ? "Actualização pronta — Ajuda → Reiniciar para actualizar…"
                : $"Actualização {label} pronta — Ajuda → Reiniciar para actualizar…";
        }
    }

    private void ResetUpdateMenuState()
    {
        _pendingUpdateVersion = null;
        CheckForUpdatesMenuHeader = CheckForUpdatesMenuDefault;
    }

    private static string BuildDownloadMenuHeader(string? version)
    {
        var label = ReleaseNotesService.FormatTechnicalVersion(version);
        return string.IsNullOrWhiteSpace(label)
            ? "Download da nova versão…"
            : $"Download da versão {label}…";
    }

    private static string ToDisplayVersionLabel(string? version, string fallback)
    {
        var label = ReleaseNotesService.FormatTechnicalVersion(version);
        return string.IsNullOrWhiteSpace(label) ? fallback : label;
    }

    private async Task ShowReleaseNotesDialogAsync(
        string title,
        string intro,
        IReadOnlyList<ReleaseNotesEntry> notes)
    {
        if (_ownerWindow is null)
        {
            return;
        }

        await Task.Yield();

        var dialogTitle = string.IsNullOrWhiteSpace(title)
            ? "Novidades da versão"
            : title;
        var message = ReleaseNotesService.BuildCumulativeDisplayMessage(intro, notes);
        var dialog = new MessageWindow(dialogTitle, message, width: 520);
        await dialog.ShowDialog(_ownerWindow);
    }

    private async Task ShowReleaseNotesDialogAsync(string intro, ReleaseNotesEntry notes) =>
        await ShowReleaseNotesDialogAsync(notes.Title, intro, [notes]);
}
