using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Synapse.Models;
using Synapse.Networking.Models;

namespace Synapse.Extras;

internal static class ListingPreparationWorker
{
    private static readonly object _gate = new();
    private static readonly Queue<ParseRequest> _requests = new();
    private static Task? _physicalTask;

    internal static Task<Listing?> ParseAsync(string json)
    {
        if (JsonConvert.DefaultSettings != null ||
            CultureInfo.CurrentCulture.GetType() != typeof(CultureInfo) ||
            CultureInfo.CurrentUICulture.GetType() != typeof(CultureInfo))
        {
            return Task.FromResult(JsonConvert.DeserializeObject<Listing>(json, JsonSettings.Settings));
        }

        ParseRequest request = new(
            json,
            CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone()),
            CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone()));
        lock (_gate)
        {
            _requests.Enqueue(request);
            StartNextLocked();
        }

        return request.Completion.Task;
    }

    private static void StartNextLocked()
    {
        if (_physicalTask != null || _requests.Count == 0)
        {
            return;
        }

        ParseRequest request = _requests.Dequeue();
        Task<Listing?> task = Task.Factory.StartNew(
            static state => Deserialize((ParseRequest)state!),
            request,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
        _physicalTask = task;
        _ = task.ContinueWith(
            static (completed, state) => Complete((ParseRequest)state!, completed),
            request,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Listing? Deserialize(ParseRequest request)
    {
        CultureInfo oldCulture = CultureInfo.CurrentCulture;
        CultureInfo oldUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = request.Culture;
            CultureInfo.CurrentUICulture = request.UiCulture;
            using StringReader text = new(request.Json);
            using JsonTextReader reader = new(text);
            JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                Converters = new List<JsonConverter> { new StageStatusConverter() },
                ContractResolver = new ListingResolver { NamingStrategy = new CamelCaseNamingStrategy() }
            });
            serializer.CheckAdditionalContent = true;
            return serializer.Deserialize<Listing>(reader);
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            CultureInfo.CurrentUICulture = oldUiCulture;
        }
    }

    private static void Complete(ParseRequest request, Task<Listing?> task)
    {
        try
        {
            if (task.IsFaulted)
            {
                request.Completion.TrySetException(task.Exception!.InnerExceptions);
            }
            else if (task.IsCanceled)
            {
                request.Completion.TrySetCanceled();
            }
            else
            {
                request.Completion.TrySetResult(task.Result);
            }
        }
        finally
        {
            lock (_gate)
            {
                _physicalTask = null;
                StartNextLocked();
            }
        }
    }

    private sealed class ParseRequest
    {
        internal ParseRequest(string json, CultureInfo culture, CultureInfo uiCulture)
        {
            Json = json;
            Culture = culture;
            UiCulture = uiCulture;
        }

        internal string Json { get; }

        internal CultureInfo Culture { get; }

        internal CultureInfo UiCulture { get; }

        internal TaskCompletionSource<Listing?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // A distinct resolver type keeps its contract cache separate from the shared JSON preset.
    private sealed class ListingResolver : CamelCasePropertyNamesContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty prop = base.CreateProperty(member, memberSerialization);
            if (prop.Writable)
            {
                return prop;
            }

            PropertyInfo? property = member as PropertyInfo;
            bool hasPrivateSetter = property?.GetSetMethod(true) != null;
            prop.Writable = hasPrivateSetter;
            return prop;
        }
    }
}
