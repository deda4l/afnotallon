using System.Buffers.Binary;
using System.Text;

namespace AfallonCoop.Protocol;

/// <summary>Growable little-endian writer. Reuse one instance per sender and call Reset() per message.</summary>
public sealed class NetWriter
{
    private byte[] _buf = new byte[256];
    private int _len;

    public int Length => _len;
    public ReadOnlySpan<byte> Span => new(_buf, 0, _len);
    public byte[] ToArray() => Span.ToArray();
    public void Reset() => _len = 0;

    private Span<byte> Take(int n)
    {
        if (_len + n > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
        var s = new Span<byte>(_buf, _len, n);
        _len += n;
        return s;
    }

    public void U8(byte v) => Take(1)[0] = v;
    public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
    public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
    public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
    public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);
    public void F32(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
    public void F64(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), v);
    public void I64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);

    public void Bytes(ReadOnlySpan<byte> data) => data.CopyTo(Take(data.Length));
    /// <summary>Length-prefixed bytes (up to 65535), read back with <see cref="NetReader.Blob"/>.</summary>
    public void Blob(ReadOnlySpan<byte> data) { U16((ushort)data.Length); Bytes(data); }

    /// <summary>Overwrite a U16 written earlier (e.g. a count known only after the items).</summary>
    public void PatchU16(int at, ushort v)
    {
        if (at < 0 || at + 2 > _len) throw new ArgumentOutOfRangeException(nameof(at));
        BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(_buf, at, 2), v);
    }

    public void Str(string s)
    {
        s ??= "";
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length > ushort.MaxValue) throw new ArgumentException("string too long");
        U16((ushort)bytes.Length);
        bytes.CopyTo(Take(bytes.Length));
    }
}

/// <summary>Bounds-checked reader; throws <see cref="ProtocolException"/> on malformed input.</summary>
public ref struct NetReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _pos;

    public NetReader(ReadOnlySpan<byte> buf) { _buf = buf; _pos = 0; }

    public int Remaining => _buf.Length - _pos;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _buf.Length) throw new ProtocolException("read past end");
        var s = _buf.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public byte U8() => Take(1)[0];
    public bool Bool() => U8() != 0;
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public string Str() => Encoding.UTF8.GetString(Take(U16()));
    public byte[] Blob() => Take(U16()).ToArray();
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}
