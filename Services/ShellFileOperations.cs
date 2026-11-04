using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Left_4_Mod.Services;

/// <summary>
/// 调用 Windows Shell 的文件操作。删除时优先送入回收站，行为与资源管理器一致。
/// </summary>
internal static class ShellFileOperations
{
    private const uint FO_DELETE = 0x0003;

    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;

        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// 在专用 STA 线程中执行回收站删除。Shell 的删除 API 需要 STA 单元，
    /// 直接在线程池（MTA）中调用会失败。
    /// </summary>
    public static Task<int> MoveToRecycleBinAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return Task.FromResult(0);
        }

        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(MoveToRecycleBin(paths));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "L4Mod.RecycleBin",
        };

#pragma warning disable CA1416 // 本项目只面向 Windows。
        thread.SetApartmentState(ApartmentState.STA);
#pragma warning restore CA1416
        thread.Start();
        return completion.Task;
    }

    /// <summary>把一组文件/文件夹送入回收站。</summary>
    /// <returns>Shell 返回码，0 表示成功。</returns>
    public static int MoveToRecycleBin(IReadOnlyList<string> paths)
    {
        var existing = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToArray();

        if (existing.Length == 0)
        {
            return 0;
        }

        // SHFileOperation 要求以 "\0" 分隔、以双 "\0" 结尾。
        var from = string.Join('\0', existing) + "\0\0";

        var operation = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT |
                     FOF_NOERRORUI | FOF_NOCONFIRMMKDIR,
        };

        try
        {
            return SHFileOperation(ref operation);
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
