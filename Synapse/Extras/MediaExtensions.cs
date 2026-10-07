using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Object = UnityEngine.Object;

namespace Synapse.Extras;

internal static class MediaExtensions
{
    private static readonly ConcurrentDictionary<string, Sprite> _spriteCache = new();

    internal static async Task<MemoryStream> DownloadHash(
        string url,
        string hash,
        Action<float>? progress,
        CancellationToken token)
    {
        using UnityWebRequest www = UnityWebRequest.Get(url);
        await www.SendAndVerify(progress, token);

        DownloadHashRequest request = new(www.downloadHandler.data, hash, Plugin.SkipHash);
        MemoryStream stream = await Task.Factory.StartNew(
            PrepareDownloadHash,
            request,
            token,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        try
        {
            token.ThrowIfCancellationRequested();
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static Task Unzip(
        Stream stream,
        string unzipPath,
        Action<float>? unzipProgress)
    {
        return Task.Run(() =>
        {
            using ZipArchive zip = new(stream, ZipArchiveMode.Read, false);
            ZipArchiveEntry[] entries = zip.Entries.ToArray();
            for (int j = 0; j < entries.Length; j++)
            {
                unzipProgress?.Invoke((float)j / entries.Length);
                ZipArchiveEntry entry = entries[j];
                string fullPath = Path.GetFullPath(Path.Combine(unzipPath, entry.FullName));
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
        });
    }

    internal static async Task<CryptoStream> Decrypt(Stream stream, string key)
    {
        using Aes aes = Aes.Create();
        byte[] iv = new byte[aes.IV.Length];
        int numBytesToRead = aes.IV.Length;
        int numBytesRead = 0;
        while (numBytesToRead > 0)
        {
            int n = await stream.ReadAsync(iv, numBytesRead, numBytesToRead);
            if (n == 0)
            {
                break;
            }

            numBytesRead += n;
            numBytesToRead -= n;
        }

        byte[] hex = Enumerable
            .Range(0, key.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(key.Substring(x, 2), 16))
            .ToArray();
        return new CryptoStream(stream, aes.CreateDecryptor(hex, iv), CryptoStreamMode.Read);
    }

    internal static Sprite GetEmbeddedResourceSprite(string path)
    {
        if (EmbeddedSpriteResourceBytes.TryGet(path, out byte[] preparedBytes))
        {
            Texture2D preparedTexture = new(2, 2);
            preparedTexture.LoadImage(preparedBytes);
            return preparedTexture.GetSprite();
        }

        using Stream stream =
            typeof(MediaExtensions).Assembly.GetManifestResourceStream(path) ?? throw new InvalidOperationException();
        using MemoryStream memStream = new();
        stream.CopyTo(memStream);
        Texture2D tex = new(2, 2);
        tex.LoadImage(memStream.ToArray());
        return tex.GetSprite();
    }

    internal static Sprite GetSprite(this Texture2D tex)
    {
        return Sprite.Create(
            tex,
            new Rect(0f, 0f, tex.width, tex.height),
            new Vector2(0.5f, 0.5f),
            256f,
            0U,
            SpriteMeshType.FullRect,
            new Vector4(0f, 0f, 0f, 0f),
            false);
    }

    internal static async Task<T> LoadAssetAsyncTask<T>(this AssetBundle assetBundle, string name)
        where T : Object
    {
        TaskCompletionSource<T> taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        AssetBundleRequest bundleRequest = assetBundle.LoadAssetAsync<T>(name);
        bundleRequest.completed += _ =>
        {
            if (bundleRequest.asset == null)
            {
                taskCompletionSource.TrySetException(new InvalidOperationException("Asset was null."));
                return;
            }

            if (bundleRequest.asset is not T asset)
            {
                taskCompletionSource.TrySetException(new InvalidOperationException($"Asset was not {typeof(T).Name}."));
                return;
            }

            taskCompletionSource.SetResult(asset);
        };

        return await taskCompletionSource.Task;
    }

    internal static async Task<AssetBundle?> LoadFromFileAsync(string path, uint crc)
    {
        TaskCompletionSource<AssetBundle?> taskCompletionSource = new();
        AssetBundleCreateRequest bundleRequest = Plugin.SkipHash
            ? AssetBundle.LoadFromFileAsync(path)
            : AssetBundle.LoadFromFileAsync(path, crc);
        bundleRequest.completed += _ => { taskCompletionSource.SetResult(bundleRequest.assetBundle); };

        return await taskCompletionSource.Task;
    }

    internal static bool MatchesGameVersion(this string gameVersion)
    {
        return gameVersion.Split(',').Any(n => n == Plugin.GameVersion);
    }

    internal static void Purge(this DirectoryInfo directory)
    {
        // cleanup
        if (!Directory.Exists(directory.FullName))
        {
            return;
        }

        try
        {
            foreach (FileInfo file in directory.GetFiles())
            {
                file.Delete();
            }

            foreach (DirectoryInfo dir in directory.GetDirectories())
            {
                dir.Delete(true);
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Error($"Exception while purging directory: [{directory}]\n{e}");
        }
    }

    internal static async Task<Sprite> RequestSprite(string url, CancellationToken token)
    {
        // ReSharper disable once InvertIf
        if (!_spriteCache.TryGetValue(url, out Sprite result))
        {
            using UnityWebRequest www = UnityWebRequestTexture.GetTexture(url);
            await www.SendAndVerify(token);
            Texture2D tex = DownloadHandlerTexture.GetContent(www);
            _spriteCache[url] = result = tex.GetSprite();
        }

        return result;
    }

    internal static Task SendAndVerify(
        this UnityWebRequest www,
        CancellationToken token)
    {
        return www.SendAndVerify(null, token);
    }

    internal static async Task SendAndVerify(
        this UnityWebRequest www,
        Action<float>? progress,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        UnityWebRequestAsyncOperation operation = www.SendWebRequest();
        while (!operation.isDone)
        {
            if (token.IsCancellationRequested)
            {
                www.Abort();
                token.ThrowIfCancellationRequested();
                return;
            }

            progress?.Invoke(www.downloadProgress);
            await Task.Delay(100, CancellationToken.None);
        }

        token.ThrowIfCancellationRequested();

#if MODERN_GAME_UI
        if (www.result != UnityWebRequest.Result.Success)
        {
            throw new InvalidOperationException($"Request to [{www.url}] failed ({www.result}, {www.responseCode}): {www.error}");
        }
#else
#pragma warning disable CS0618 // Type or member is obsolete
        if (www.isHttpError)
        {
            throw new InvalidOperationException(
                $"Failed to connect to [{www.url}], server returned an error response ({www.responseCode}).");
        }

        if (www.isNetworkError)
        {
            throw new InvalidOperationException($"Failed to connect to [{www.url}], network error ({www.error}).");
        }
#pragma warning restore CS0618 // Type or member is obsolete
#endif
    }

    private static MemoryStream PrepareDownloadHash(object state)
    {
        DownloadHashRequest request = (DownloadHashRequest)state;
        MemoryStream stream = new(request.Bytes);
        try
        {
            using MD5 md5 = MD5.Create();
            string computed = BitConverter
                .ToString(md5.ComputeHash(stream))
                .Replace("-", string.Empty)
                .ToLowerInvariant();
            if (computed != request.Hash && !request.SkipHash)
            {
                throw new InvalidOperationException($"MD5 mismatch, expected: [{request.Hash}], calculated: [{computed}].");
            }

            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private sealed class DownloadHashRequest(byte[] bytes, string hash, bool skipHash)
    {
        public byte[] Bytes { get; } = bytes;

        public string Hash { get; } = hash;

        public bool SkipHash { get; } = skipHash;
    }
}
