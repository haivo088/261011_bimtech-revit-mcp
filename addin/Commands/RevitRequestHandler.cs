using System.Collections.Concurrent;
using Autodesk.Revit.UI;
using Bimtech.RevitMcp.Protocol;

namespace Bimtech.RevitMcp.Commands;

// TCP runs in the background; Execute is called by Revit on its API thread.
public sealed class RevitRequestHandler : IExternalEventHandler, IDisposable
{
    private sealed record Job(RpcRequest Request, TaskCompletionSource<object> Completion);
    private readonly ConcurrentQueue<Job> _jobs = new();
    private readonly object _eventLock = new();
    private readonly ExternalEvent _externalEvent;
    private bool _disposed;

    public RevitRequestHandler() => _externalEvent = ExternalEvent.Create(this);

    public async Task<object> ExecuteAsync(RpcRequest request, CancellationToken cancellation)
    {
        var job = new Job(request, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_eventLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _jobs.Enqueue(job);
        }
        using var registration = cancellation.Register(() => job.Completion.TrySetCanceled(cancellation));

        // Retry signaling while pending so an enqueue near the end of Execute
        // cannot leave a request waiting without a future Revit event.
        while (!job.Completion.Task.IsCompleted)
        {
            lock (_eventLock)
            {
                if (_disposed) job.Completion.TrySetException(new ObjectDisposedException(nameof(RevitRequestHandler)));
                else if (_externalEvent.Raise() == ExternalEventRequest.Denied)
                    job.Completion.TrySetException(new RpcException(-32002, "Revit denied the external event."));
            }
            await Task.WhenAny(job.Completion.Task, Task.Delay(100, cancellation));
        }
        return await job.Completion.Task;
    }

    public void Execute(UIApplication application)
    {
        while (_jobs.TryDequeue(out var job))
        {
            if (job.Completion.Task.IsCompleted) continue;
            try { job.Completion.TrySetResult(Dispatch(application, job.Request)); }
            catch (Exception exception) { job.Completion.TrySetException(exception); }
        }
    }

    private static object Dispatch(UIApplication application, RpcRequest request)
    {
        switch (request.Method)
        {
            case "ping":
                return new { status = "ok", revitVersion = application.Application.VersionNumber, bridgeVersion = "0.1.0" };

            case "get_document_info":
                var document = application.ActiveUIDocument?.Document
                    ?? throw new RpcException(-32001, "Open a Revit model before calling get_document_info.");
                return new
                {
                    title = document.Title,
                    path = document.PathName,
                    isFamilyDocument = document.IsFamilyDocument,
                    isWorkshared = document.IsWorkshared,
                    activeView = new
                    {
                        id = document.ActiveView.Id.Value.ToString(),
                        name = document.ActiveView.Name,
                        type = document.ActiveView.ViewType.ToString(),
                    },
                };

            default:
                throw new RpcException(-32601, $"Unknown method: {request.Method}");
        }
    }

    public string GetName() => "BIMTECH MCP request handler";

    public void Dispose()
    {
        lock (_eventLock)
        {
            if (_disposed) return;
            _disposed = true;
            while (_jobs.TryDequeue(out var job))
                job.Completion.TrySetException(new ObjectDisposedException(nameof(RevitRequestHandler)));
            _externalEvent.Dispose();
        }
    }
}
