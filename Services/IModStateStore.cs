using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

public interface IModStateStore
{
    Task<ModState> LoadAsync(string libraryRoot, CancellationToken cancellationToken = default);

    Task SaveAsync(ModState state, CancellationToken cancellationToken = default);
}
