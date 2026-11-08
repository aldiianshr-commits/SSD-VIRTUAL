using System.Numerics;

namespace SsdVirtual.Cache;

/// <summary>Growable bitmap of cached blocks.</summary>
public sealed class BlockBitmap
{
    private byte[] _bits;

    public BlockBitmap(byte[]? bits = null) => _bits = bits ?? Array.Empty<byte>();

    public byte[] ToArray() => (byte[])_bits.Clone();

    public bool Get(long index)
    {
        var b = index >> 3;
        return b < _bits.Length && (_bits[b] & (1 << (int)(index & 7))) != 0;
    }

    public void Set(long index)
    {
        var b = (int)(index >> 3);
        if (b >= _bits.Length) Array.Resize(ref _bits, Math.Max(b + 1, _bits.Length * 2));
        _bits[b] |= (byte)(1 << (int)(index & 7));
    }

    public void Clear(long index)
    {
        var b = index >> 3;
        if (b < _bits.Length) _bits[b] &= (byte)~(1 << (int)(index & 7));
    }

    /// <summary>Clears every bit at or above <paramref name="fromIndex"/>.</summary>
    public void TruncateFrom(long fromIndex)
    {
        var len = _bits.Length;
        for (long i = fromIndex; i < (long)len * 8; i++) Clear(i);
        var needed = (int)((fromIndex + 7) >> 3);
        if (needed < _bits.Length) Array.Resize(ref _bits, needed);
    }

    public long CountSet(long upToExclusive)
    {
        long count = 0;
        var fullBytes = (int)Math.Min(_bits.Length, upToExclusive >> 3);
        for (int i = 0; i < fullBytes; i++) count += BitOperations.PopCount(_bits[i]);
        for (long i = (long)fullBytes * 8; i < upToExclusive && (i >> 3) < _bits.Length; i++) if (Get(i)) count++;
        return count;
    }

    public void Reset() => _bits = Array.Empty<byte>();
}
