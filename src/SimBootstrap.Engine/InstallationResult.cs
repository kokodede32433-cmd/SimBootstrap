using System.Collections.Generic;

namespace SimBootstrap.Engine;

public record InstallationResult(
    bool IsSuccess,
    string SessionId,
    string? ErrorMessage,
    List<string> Logs
);
