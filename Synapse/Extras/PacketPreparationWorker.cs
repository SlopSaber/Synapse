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
using Synapse.Networking;
using Synapse.Networking.Models;

namespace Synapse.Extras;

internal readonly struct PreparedPacket
{
    internal PreparedPacket(byte opcode, object? value = null, object? second = null)
    {
        Opcode = opcode;
        Value = value;
        Second = second;
    }

    internal byte Opcode { get; }

    internal object? Value { get; }

    internal object? Second { get; }
}

internal static class PacketPreparationWorker
{
    private static readonly object _gate = new();
    private static readonly Queue<ParseRequest> _requests = new();
    private static Task? _physicalTask;

    internal static bool CanPrepare =>
        SynchronizationContext.Current != null &&
        JsonConvert.DefaultSettings == null &&
        CultureInfo.CurrentCulture.GetType() == typeof(CultureInfo) &&
        CultureInfo.CurrentUICulture.GetType() == typeof(CultureInfo);

    internal static Task<PreparedPacket> ParseAsync(byte opcode, byte[] payload)
    {
        if (!HasPayload((ClientOpcode)opcode))
        {
            return Task.FromResult(new PreparedPacket(opcode));
        }

        ParseRequest request = new(
            opcode,
            payload,
            CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentCulture.Clone()),
            CultureInfo.ReadOnly((CultureInfo)CultureInfo.CurrentUICulture.Clone()));
        lock (_gate)
        {
            _requests.Enqueue(request);
            StartNextLocked();
        }

        return request.Completion.Task;
    }

    internal static PreparedPacket ParseOnCaller(byte opcode, byte[] payload)
    {
        return Parse(opcode, payload, false);
    }

    private static bool HasPayload(ClientOpcode opcode)
    {
        return opcode is ClientOpcode.Disconnect or ClientOpcode.RefusedPacket or
            ClientOpcode.PlayerCount or ClientOpcode.Ping or ClientOpcode.Status or
            ClientOpcode.ChatMessage or ClientOpcode.UserJoin or ClientOpcode.UserLeave or
            ClientOpcode.UserBanned or ClientOpcode.AcknowledgeScore or ClientOpcode.InvalidateScores or
            ClientOpcode.LeaderboardScores;
    }

    private static void StartNextLocked()
    {
        if (_physicalTask != null || _requests.Count == 0)
        {
            return;
        }

        ParseRequest request = _requests.Dequeue();
        Task<PreparedPacket> task = Task.Factory.StartNew(
            static state => Process((ParseRequest)state!),
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

    private static PreparedPacket Process(ParseRequest request)
    {
        CultureInfo oldCulture = CultureInfo.CurrentCulture;
        CultureInfo oldUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = request.Culture;
            CultureInfo.CurrentUICulture = request.UiCulture;
            return Parse(request.Opcode, request.Payload, true);
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            CultureInfo.CurrentUICulture = oldUiCulture;
        }
    }

    private static PreparedPacket Parse(byte opcode, byte[] payload, bool controlledJson)
    {
        using MemoryStream stream = new(payload);
        using BinaryReader reader = new(stream);
        return (ClientOpcode)opcode switch
        {
            ClientOpcode.Disconnect => new PreparedPacket(opcode, (DisconnectCode)reader.ReadByte()),
            ClientOpcode.RefusedPacket or ClientOpcode.UserJoin or ClientOpcode.UserLeave or
                ClientOpcode.UserBanned => new PreparedPacket(opcode, reader.ReadString()),
            ClientOpcode.PlayerCount => new PreparedPacket(opcode, reader.ReadUInt16(), reader.ReadUInt16()),
            ClientOpcode.Ping => new PreparedPacket(opcode, reader.ReadSingle(), reader.ReadSingle()),
            ClientOpcode.Status => new PreparedPacket(opcode, DeserializeStatus(reader.ReadString(), controlledJson)),
            ClientOpcode.ChatMessage => new PreparedPacket(opcode, Deserialize<ChatMessage>(reader.ReadString(), controlledJson)),
            ClientOpcode.AcknowledgeScore => new PreparedPacket(opcode, reader.ReadByte(), reader.ReadInt32()),
            ClientOpcode.InvalidateScores => new PreparedPacket(opcode, reader.ReadByte()),
            ClientOpcode.LeaderboardScores => new PreparedPacket(opcode, Deserialize<LeaderboardScores>(reader.ReadString(), controlledJson)),
            _ => new PreparedPacket(opcode)
        };
    }

    private static Status? DeserializeStatus(string json, bool controlled)
    {
        Status? status = Deserialize<Status>(json, controlled);
        if (controlled)
        {
            LaunchModifierPreparation.Prepare(status);
        }

        return status;
    }

    private static T? Deserialize<T>(string json, bool controlled)
    {
        if (!controlled)
        {
            return JsonConvert.DeserializeObject<T>(json, JsonSettings.Settings);
        }

        using StringReader text = new(json);
        using JsonTextReader reader = new(text);
        JsonSerializer serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            Converters = new List<JsonConverter> { new StageStatusConverter() },
            ContractResolver = new PacketResolver { NamingStrategy = new CamelCaseNamingStrategy() }
        });
        serializer.CheckAdditionalContent = true;
        return serializer.Deserialize<T>(reader);
    }

    private static void Complete(ParseRequest request, Task<PreparedPacket> task)
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
        internal ParseRequest(byte opcode, byte[] payload, CultureInfo culture, CultureInfo uiCulture)
        {
            Opcode = opcode;
            Payload = payload;
            Culture = culture;
            UiCulture = uiCulture;
        }

        internal byte Opcode { get; }

        internal byte[] Payload { get; }

        internal CultureInfo Culture { get; }

        internal CultureInfo UiCulture { get; }

        internal TaskCompletionSource<PreparedPacket> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PacketResolver : CamelCasePropertyNamesContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty property = base.CreateProperty(member, memberSerialization);
            if (!property.Writable)
            {
                property.Writable = (member as PropertyInfo)?.GetSetMethod(true) != null;
            }

            return property;
        }
    }
}
