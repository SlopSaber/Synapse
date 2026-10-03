using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace Synapse.Extras;

internal static class CountdownFileWorker
{
    private static readonly object Gate = new();
    private static readonly Queue<Request> Requests = new();
    private static Task? _physicalTask;

    internal static Task<string> Prepare(Assembly assembly, string resourceName, string folder, string fileName)
    {
        Request request = new(assembly, resourceName, folder, fileName);
        lock (Gate)
        {
            Requests.Enqueue(request);
            StartWorker();
        }

        return request.Completion.Task;
    }

    private static void StartWorker()
    {
        if (_physicalTask == null && Requests.Count != 0)
        {
            _physicalTask = Task.Run(Drain);
            _physicalTask.ContinueWith(Completed, TaskScheduler.Default);
        }
    }

    private static void Completed(Task completed)
    {
        lock (Gate)
        {
            if (_physicalTask == completed)
            {
                _physicalTask = null;
                StartWorker();
            }
        }
    }

    private static void Drain()
    {
        while (true)
        {
            Request request;
            lock (Gate)
            {
                if (Requests.Count == 0)
                {
                    return;
                }

                request = Requests.Dequeue();
            }

            try
            {
                request.Completion.TrySetResult(Process(request));
            }
            catch (Exception exception)
            {
                request.Completion.TrySetException(exception);
            }
        }
    }

    private static string Process(Request request)
    {
        Directory.CreateDirectory(request.Folder);
        string path = Path.Combine(request.Folder, request.FileName);
        if (!File.Exists(path))
        {
            using Stream resource = request.Assembly.GetManifestResourceStream(request.ResourceName) ??
                                    throw new InvalidOperationException();
            using FileStream file = new(path, FileMode.Create, FileAccess.Write);
            resource.CopyTo(file);
        }

        return path;
    }

    private sealed class Request
    {
        internal Request(Assembly assembly, string resourceName, string folder, string fileName)
        {
            Assembly = assembly;
            ResourceName = resourceName;
            Folder = folder;
            FileName = fileName;
        }

        internal Assembly Assembly { get; }

        internal string ResourceName { get; }

        internal string Folder { get; }

        internal string FileName { get; }

        internal TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
