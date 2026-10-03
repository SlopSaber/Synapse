using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Synapse.Extras;

internal static class MenuBundleFileWorker
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, LeaseEntry> Leases = new(
        Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static Task<string> PreparePath(string folder, string title)
    {
        return Task.Factory.StartNew(
            ProcessPath,
            new PathRequest(folder, title),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    internal static async Task<IDisposable> Acquire(string path)
    {
        LeaseEntry entry;
        lock (Gate)
        {
            if (!Leases.TryGetValue(path, out entry!))
            {
                entry = new LeaseEntry(path);
                Leases.Add(path, entry);
            }

            entry.References++;
        }

        await entry.Semaphore.WaitAsync().ConfigureAwait(false);
        return new Lease(entry);
    }

    internal static Task<bool> Exists(string path)
    {
        return Start(new FileRequest(Operation.Exists, string.Empty, path, null));
    }

    internal static Task Write(string folder, string path, byte[] data)
    {
        return Start(new FileRequest(Operation.Write, folder, path, data));
    }

    internal static Task Delete(string path)
    {
        return Start(new FileRequest(Operation.Delete, string.Empty, path, null));
    }

    private static Task<bool> Start(FileRequest request)
    {
        return Task.Factory.StartNew(
            ProcessFile,
            request,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static string ProcessPath(object state)
    {
        PathRequest request = (PathRequest)state;
        string fileName = new(request.Title.Select(c => char.IsLetter(c) || char.IsNumber(c) ? c : '_').ToArray());
        return Path.GetFullPath(Path.Combine(request.Folder, fileName));
    }

    private static bool ProcessFile(object state)
    {
        FileRequest request = (FileRequest)state;
        switch (request.Operation)
        {
            case Operation.Exists:
                return File.Exists(request.Path);
            case Operation.Write:
                Directory.CreateDirectory(request.Folder);
                File.WriteAllBytes(request.Path, request.Data!);
                return true;
            case Operation.Delete:
                FileInfo file = new(request.Path);
                if (file.Exists)
                {
                    file.Delete();
                }

                return true;
            default:
                throw new InvalidOperationException();
        }
    }

    private enum Operation
    {
        Exists,
        Write,
        Delete
    }

    private sealed class PathRequest(string folder, string title)
    {
        internal string Folder { get; } = folder;

        internal string Title { get; } = title;
    }

    private sealed class FileRequest(Operation operation, string folder, string path, byte[]? data)
    {
        internal Operation Operation { get; } = operation;

        internal string Folder { get; } = folder;

        internal string Path { get; } = path;

        internal byte[]? Data { get; } = data;
    }

    private sealed class LeaseEntry(string path)
    {
        internal string Path { get; } = path;

        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int References { get; set; }
    }

    private sealed class Lease(LeaseEntry entry) : IDisposable
    {
        private LeaseEntry? _entry = entry;

        public void Dispose()
        {
            LeaseEntry? current = Interlocked.Exchange(ref _entry, null);
            if (current == null)
            {
                return;
            }

            lock (Gate)
            {
                current.Semaphore.Release();
                current.References--;
                if (current.References == 0)
                {
                    Leases.Remove(current.Path);
                    current.Semaphore.Dispose();
                }
            }
        }
    }
}
