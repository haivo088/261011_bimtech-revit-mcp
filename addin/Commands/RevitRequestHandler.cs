using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Protocol;
using Bimtech.RevitMcp.Settings;

namespace Bimtech.RevitMcp.Commands;

// TCP runs in the background; Execute is called by Revit on its API thread.
public sealed class RevitRequestHandler : IExternalEventHandler, IDisposable
{
    private sealed record Job(
        IRevitCommand Command,
        CommandParams Params,
        McpSettings Settings,
        TaskCompletionSource<object?> Completion,
        CancellationToken Cancellation);

    private readonly ConcurrentQueue<Job> _jobs = new();
    private readonly object _eventLock = new();
    private readonly CommandRegistry _registry;
    private readonly SessionStore _sessions = new();
    private readonly ExternalEvent _externalEvent;
    private bool _disposed;

    public RevitRequestHandler(CommandRegistry registry)
    {
        _registry = registry;
        _externalEvent = ExternalEvent.Create(this);
    }

    // Called on the TCP thread. Checks that need no Revit API run here, so bad
    // requests fail immediately instead of waiting for Revit to become idle.
    public async Task<object?> ExecuteAsync(RpcRequest request, CancellationToken cancellation)
    {
        if (!_registry.TryGet(request.Method!, out var command))
            throw new RpcException(RpcErrorCodes.MethodNotFound, $"Unknown method: {request.Method}",
                new { available = _registry.Methods.Order().ToArray() });
        var parameters = new CommandParams(request.Params);
        var settings = SettingsStore.Current;
        CommandPolicy.EnsureAllowed(command, parameters, settings);

        var job = new Job(command, parameters, settings, new(TaskCreationOptions.RunContinuationsAsynchronously), cancellation);
        lock (_eventLock)
        {
            if (_disposed) throw BridgeStopped();
            _jobs.Enqueue(job);
        }
        using var registration = cancellation.Register(() => job.Completion.TrySetCanceled(cancellation));

        // Retry signaling while pending so an enqueue near the end of Execute
        // cannot leave a request waiting without a future Revit event.
        while (!job.Completion.Task.IsCompleted)
        {
            lock (_eventLock)
            {
                if (_disposed) job.Completion.TrySetException(BridgeStopped());
                else if (_externalEvent.Raise() == ExternalEventRequest.Denied)
                    job.Completion.TrySetException(new RpcException(RpcErrorCodes.EventDenied,
                        "Revit denied the external event. Finish or cancel the current Revit command, then retry."));
            }
            await Task.WhenAny(job.Completion.Task, Task.Delay(100, cancellation));
        }
        return await job.Completion.Task;
    }

    public void Execute(UIApplication application)
    {
        while (_jobs.TryDequeue(out var job))
        {
            // Skip jobs whose client already timed out or disconnected.
            if (job.Completion.Task.IsCompleted) continue;
            try
            {
                var context = new CommandContext(application, job.Params, _registry, _sessions, job.Settings, job.Cancellation);
                job.Completion.TrySetResult(job.Command.Execute(context));
            }
            catch (OperationCanceledException) { job.Completion.TrySetCanceled(job.Cancellation); }
            catch (Exception exception) { job.Completion.TrySetException(Translate(exception)); }
        }
    }

    // Gives every failure a JSON-RPC code so the bridge never leaks a raw stack trace.
    private static RpcException Translate(Exception exception) => exception switch
    {
        RpcException rpc => rpc,
        Autodesk.Revit.Exceptions.ApplicationException revit => new RpcException(
            RpcErrorCodes.RevitApiError, revit.Message, new { type = revit.GetType().Name }),
        _ => new RpcException(RpcErrorCodes.InternalError, exception.Message,
            new { type = exception.GetType().Name }),
    };

    private static RpcException BridgeStopped() =>
        new(RpcErrorCodes.BridgeStopped, "The BIMTECH bridge is stopping. Start it again from the Revit ribbon.");

    public string GetName() => "BIMTECH MCP request handler";

    public void Dispose()
    {
        lock (_eventLock)
        {
            if (_disposed) return;
            _disposed = true;
            while (_jobs.TryDequeue(out var job))
                job.Completion.TrySetException(BridgeStopped());
            _externalEvent.Dispose();
        }
    }
}
