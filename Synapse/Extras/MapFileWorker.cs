using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Synapse.Extras;

internal static class MapFileWorker
{
    private static readonly object Gate = new();
    private static readonly Queue<Request> Requests = new();
    private static Task? _physicalTask;

    internal sealed class Progress
    {
        private float _value;

        internal float Value => Volatile.Read(ref _value);

        internal void Set(float value) => Volatile.Write(ref _value, value);
    }

    internal sealed class Result
    {
        internal bool Found { get; set; }

        internal List<(string Path, Exception Error)> Warnings { get; } = new();
    }

    private enum Operation
    {
        Initialize,
        Extract,
        Purge,
        Delete,
        ClearMapCache
    }

    private sealed class Request
    {
        internal Operation Kind;
        internal string Root = string.Empty;
        internal string? OldRoot;
        internal string? Destination;
        internal string? MapName;
        internal string? Key;
        internal MemoryStream? Payload;
        internal Progress? Progress;
        internal CancellationToken Token;
        internal readonly TaskCompletionSource<Result> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal static Task<Result> Initialize(string? oldRoot, string tempRoot, string cacheRoot) => Submit(new Request
    {
        Kind = Operation.Initialize,
        Root = cacheRoot,
        OldRoot = oldRoot,
        Destination = tempRoot
    });

    internal static Task<Result> Extract(
        string cacheRoot,
        string mapName,
        string destination,
        string? key,
        MemoryStream? payload,
        Progress progress,
        CancellationToken token) => Submit(new Request
    {
        Kind = Operation.Extract,
        Root = cacheRoot,
        MapName = mapName,
        Destination = destination,
        Key = key,
        Payload = payload,
        Progress = progress,
        Token = token
    });

    internal static Task<Result> Purge(string root) => Submit(new Request { Kind = Operation.Purge, Root = root });

    internal static Task<Result> Delete(string root) => Submit(new Request { Kind = Operation.Delete, Root = root });

    internal static Task<Result> ClearMapCache(string root, string mapName, CancellationToken token) => Submit(new Request
    {
        Kind = Operation.ClearMapCache,
        Root = root,
        MapName = mapName,
        Token = token
    });

    private static Task<Result> Submit(Request request)
    {
        lock (Gate)
        {
            Requests.Enqueue(request);
            StartWorker();
        }

        return request.Completion.Task;
    }

    private static void StartWorker()
    {
        if (_physicalTask != null || Requests.Count == 0)
        {
            return;
        }

        _physicalTask = Task.Factory.StartNew(Drain, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        _physicalTask.ContinueWith(Completed, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void Completed(Task completed)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_physicalTask, completed))
            {
                return;
            }

            _physicalTask = null;
            StartWorker();
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

    private static Result Process(Request request)
    {
        using MemoryStream? payload = request.Payload;
        request.Token.ThrowIfCancellationRequested();
        Result result = new();
        switch (request.Kind)
        {
            case Operation.Initialize:
                if (request.OldRoot != null && Directory.Exists(request.OldRoot))
                {
                    Directory.Delete(request.OldRoot, true);
                }

                Directory.CreateDirectory(request.Destination!);
                Directory.CreateDirectory(request.Root);
                string readme = Path.Combine(request.Root, "README.txt");
                if (!File.Exists(readme))
                {
                    File.WriteAllText(readme, "you wouldn't happen to be trying to steal maps, would you?");
                }

                break;
            case Operation.Purge:
                PurgeDirectory(request.Root, result);
                break;
            case Operation.Delete:
                if (Directory.Exists(request.Root))
                {
                    Directory.Delete(request.Root, true);
                }

                break;
            case Operation.ClearMapCache:
                string clearPath = CachePath(request.Root, request.MapName!);
                File.Delete(clearPath + ".aes");
                File.Delete(clearPath + ".zip");
                break;
            case Operation.Extract:
                string cachePath = CachePath(request.Root, request.MapName!);
                string aesPath = cachePath + ".aes";
                string zipPath = cachePath + ".zip";
                if (payload != null)
                {
                    using FileStream output = new(string.IsNullOrEmpty(request.Key) ? zipPath : aesPath, FileMode.CreateNew);
                    payload.CopyTo(output);
                }

                bool encrypted = File.Exists(aesPath);
                if (!encrypted && !File.Exists(zipPath))
                {
                    return result;
                }

                if (encrypted && request.Key == null)
                {
                    throw new InvalidOperationException($"[{request.MapName}] was encrypted but no key provided.");
                }

                Directory.CreateDirectory(request.Destination!);
                using (FileStream input = new(encrypted ? aesPath : zipPath, FileMode.Open))
                {
                    if (encrypted)
                    {
                        using Aes aes = Aes.Create();
                        byte[] iv = new byte[aes.IV.Length];
                        int offset = 0;
                        while (offset < iv.Length)
                        {
                            request.Token.ThrowIfCancellationRequested();
                            int count = input.Read(iv, offset, iv.Length - offset);
                            if (count == 0)
                            {
                                break;
                            }

                            offset += count;
                        }

                        byte[] key = Enumerable.Range(0, request.Key!.Length)
                            .Where(index => index % 2 == 0)
                            .Select(index => Convert.ToByte(request.Key.Substring(index, 2), 16)).ToArray();
                        using CryptoStream decrypted = new(input, aes.CreateDecryptor(key, iv), CryptoStreamMode.Read);
                        ExtractArchive(decrypted, request);
                    }
                    else
                    {
                        ExtractArchive(input, request);
                    }
                }

                result.Found = true;
                break;
        }

        return result;
    }

    private static string CachePath(string root, string name)
    {
        string safeName = Path.GetInvalidFileNameChars().Aggregate(name, (current, character) => current.Replace(character, '_'));
        return Path.Combine(root, safeName);
    }

    private static void ExtractArchive(Stream stream, Request request)
    {
        string root = Path.GetFullPath(request.Destination!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string prefix = root + Path.DirectorySeparatorChar;
        using ZipArchive zip = new(stream, ZipArchiveMode.Read, false);
        ZipArchiveEntry[] entries = zip.Entries.ToArray();
        for (int index = 0; index < entries.Length; index++)
        {
            request.Token.ThrowIfCancellationRequested();
            request.Progress!.Set(0.95f + (((float)index / entries.Length) * 0.02f));
            ZipArchiveEntry entry = entries[index];
            string fullPath = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Archive entry is outside the map directory.");
            }

            if (Path.GetFileName(fullPath).Length == 0)
            {
                Directory.CreateDirectory(fullPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                entry.ExtractToFile(fullPath, true);
            }
        }
    }

    private static void PurgeDirectory(string root, Result result)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (string file in Directory.GetFiles(root))
            {
                File.Delete(file);
            }

            foreach (string directory in Directory.GetDirectories(root))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (Exception exception)
        {
            result.Warnings.Add((root, exception));
        }
    }
}
