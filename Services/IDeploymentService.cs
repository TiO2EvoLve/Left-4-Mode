using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

public interface IDeploymentService
{
    Task<DeploymentManifest> LoadManifestAsync(CancellationToken cancellationToken = default);

    ModDeploymentState GetStatus(DeploymentManifest manifest, string? addonsPath, ModRecord record);

    /// <summary>根据启用状态和部署清单生成推送计划。不会修改任何文件。</summary>
    DeploymentPlan CreatePlan(
        IReadOnlyList<ModRecord> mods,
        DeploymentManifest manifest,
        string addonsPath);

    Task<DeploymentResult> ApplyAsync(
        DeploymentPlan plan,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken);
}
