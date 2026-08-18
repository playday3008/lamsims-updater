using LamSims.App.Services;
using LamSims.Core.Catalogs;
using LamSims.Core.Installing;
using LamSims.Core.Settings;

namespace LamSims.App;

/// <summary>
/// Everything the shell needs, assembled once. This record is final: a later change wanting
/// another dependency should carry it on one it already has. A constructor that drifts between
/// tasks is a defect this project has already paid for.
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
    string? CommandLineCatalog);
