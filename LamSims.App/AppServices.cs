using LamSims.App.Services;
using LamSims.Core.Catalogs;
using LamSims.Core.Installing;
using LamSims.Core.Settings;
using LamSims.Core.Unlocking;

namespace LamSims.App;

/// <summary>
/// Everything the shell needs, assembled once. A task must not add a dependency mid-phase: a
/// constructor that drifts between tasks is a defect this project has already paid for. A new phase
/// bringing a new subsystem may add one field, deliberately — Phase 4's unlocker is that case.
/// </summary>
public sealed record AppServices(
    AppPaths Paths,
    SettingsStore Settings,
    AppSettings Current,
    string? SettingsError,
    CatalogLoader Catalog,
    InstallStateStore InstallState,
    IQueueController Queue,
    IUiDispatcher Dispatcher,
    IPickerService Pickers,
    IClock Clock,
    string? CommandLineCatalog,
    UnlockerService Unlocker,
    IUnlockerHost UnlockerHost,
    IUnlockerAssetSource UnlockerAssets);
