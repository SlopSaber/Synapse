using System;
using System.IO;
#if NET
using System.Buffers;
using Microsoft.IO;
#endif

namespace Synapse.Networking;

public sealed class PacketBuilder : IDisposable
{
#if NET
    private static readonly RecyclableMemoryStreamManager _streamManager = new(new RecyclableMemoryStreamManager.Options
    {
        BlockSize = 1024,
        LargeBufferMultiple = 1024 * 1024,
        MaximumBufferSize = 16 * 1024 * 1024,
        MaximumLargePoolFreeBytes = 16 * 1024 * 1024 * 4,
        MaximumSmallPoolFreeBytes = 100 * 1024
    });
#endif

#if NET
    private readonly RecyclableMemoryStream _stream;
#else
    private readonly MemoryStream _stream;
#endif
    private readonly BinaryWriter _writer;

    public PacketBuilder(byte opcode)
    {
#if NET
        _stream = _streamManager.GetStream();
        _stream.Advance(2);
#else
        _stream = new MemoryStream();
        _stream.Position += 2;
#endif
        _writer = new BinaryWriter(_stream);
        _writer.Write(opcode);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _writer.Dispose();
    }

#if NET
    public ReadOnlySequence<byte> ToBytes()
    {
        long length = _stream.Length;
        ushort ushortLength = checked((ushort)(length - 2));
        _stream.Position = 0;
        Span<byte> span = _stream.GetSpan(2);
        span[0] = (byte)ushortLength;
        span[1] = (byte)(ushortLength >> 8);
        return _stream.GetReadOnlySequence();
    }
#else
    public byte[] ToBytes()
    {
        byte[] bytes = _stream.GetBuffer();
        ushort ushortLength = checked((ushort)(bytes.Length - 2));
        bytes[0] = (byte)ushortLength;
        bytes[1] = (byte)(ushortLength >> 8);
        return bytes;
    }
#endif

    public void Write(byte value)
    {
        _writer.Write(value);
    }

    public void Write(bool value)
    {
        _writer.Write(value);
    }

    public void Write(string value)
    {
        _writer.Write(value);
    }

    public void Write(float value)
    {
        _writer.Write(value);
    }

    public void Write(int value)
    {
        _writer.Write(value);
    }

    public void Write(ushort value)
    {
        _writer.Write(value);
    }
}
