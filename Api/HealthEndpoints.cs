using EventForge.Persistence;
using EventForge.Queue;
using EventForge.WebSocket;

namespace EventForge.Api;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", (WriteBehindPersistence persist, InMemoryJobQueue queue) => Results.Ok(new
        {
            ok = true,
            service = "event-forge",
            cache_loaded = persist.IsLoaded,
            pending_writes = persist.PendingWrites,
            jobs_total = queue.TotalCount,
            jobs_queued = queue.QueuedCount,
        }));

        app.MapGet("/healthws", (WsConnectionManager ws) => Results.Ok(new
        {
            ok = true,
            service = "event-forge-ws",
            ws_ready = true,
        }));
    }
}
