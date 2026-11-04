using System;

namespace Left_4_Mod.Services;

/// <summary>当路径越出模组库范围或不符合规则时抛出。</summary>
public sealed class LibraryPathException : Exception
{
    public LibraryPathException(string message)
        : base(message)
    {
    }

    public LibraryPathException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
