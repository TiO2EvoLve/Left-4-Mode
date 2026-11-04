using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

public interface IModCatalogService
{
    Task<ModCatalog> LoadAsync(string? addonsPath, CancellationToken cancellationToken = default);

    Task SaveStateAsync(ModState state, CancellationToken cancellationToken = default);

    Task RefreshDeploymentStatesAsync(
        ModCatalog catalog,
        string? addonsPath,
        CancellationToken cancellationToken = default);
}
