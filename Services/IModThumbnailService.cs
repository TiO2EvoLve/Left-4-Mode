using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Left_4_Mod.Models;

namespace Left_4_Mod.Services;

public interface IModThumbnailService
{
    Bitmap? FolderImage { get; }

    Bitmap? PlaceholderImage { get; }

    /// <summary>
    /// 返回模组缩略图。优先读取模组库 photo 缓存，其次从 VPK 内部提取，
    /// 再次下载创意工坊预览图；找不到时返回 null，由调用方使用占位图。
    /// </summary>
    Task<Bitmap?> LoadAsync(
        ModRecord record,
        string vpkFullPath,
        CancellationToken cancellationToken = default);
}
