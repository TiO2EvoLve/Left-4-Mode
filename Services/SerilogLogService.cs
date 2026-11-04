using System;
using Serilog;

namespace Left_4_Mod.Services;

public sealed class SerilogLogService : ILogService
{
    private readonly ILogger _logger;

    public SerilogLogService(ILogger logger)
    {
        _logger = logger;
    }

    public void Info(string message) => _logger.Information("{Message}", message);

    public void Warn(string message) => _logger.Warning("{Message}", message);

    public void Error(Exception exception, string message) =>
        _logger.Error(exception, "{Message}", message);
}
