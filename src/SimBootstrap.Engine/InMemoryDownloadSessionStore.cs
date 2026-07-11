using System.Collections.Concurrent;
using System.Collections.Generic;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class InMemoryDownloadSessionStore : IDownloadSessionStore
{
    private readonly ConcurrentDictionary<string, DownloadSession> _sessions = new();

    public void Save(DownloadSession session)
    {
        _sessions[session.SessionId] = session;
    }

    public DownloadSession? Get(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public IEnumerable<DownloadSession> GetAll()
    {
        return _sessions.Values;
    }
}
