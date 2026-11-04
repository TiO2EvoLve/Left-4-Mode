using System;

namespace Left_4_Mod.Services;

/// <summary>轻量日志抽象，便于测试时替换。</summary>
public interface ILogService
{
    void Info(string message);

    void Warn(string message);

    void Error(Exception exception, string message);
}
