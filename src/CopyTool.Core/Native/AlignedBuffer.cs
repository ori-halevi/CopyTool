using System.Buffers;
using System.Runtime.InteropServices;

namespace CopyTool.Core.Native;

/// <summary>
/// Sector-aligned unmanaged block exposed as <see cref="Memory{T}"/>.
///
/// FILE_FLAG_NO_BUFFERING rejects any buffer whose address is not a multiple of
/// the volume sector size, and GC-managed arrays offer no alignment guarantee.
/// Deriving from <see cref="MemoryManager{T}"/> is what lets this unmanaged block
/// be handed to the async RandomAccess APIs, which only speak Memory&lt;byte&gt;.
/// </summary>
internal sealed unsafe class AlignedBuffer : MemoryManager<byte>
{
    private byte* _ptr;
    private readonly int _length;

    public AlignedBuffer(int length, int alignment)
    {
        _length = length;
        _ptr = (byte*)NativeMemory.AlignedAlloc((nuint)length, (nuint)alignment);
    }

    public int Length => _length;

    public override Span<byte> GetSpan() => new(_ptr, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_ptr is null, this);
        return new MemoryHandle(_ptr + elementIndex);
    }

    // The block never moves, so there is nothing to release.
    public override void Unpin() { }

    protected override void Dispose(bool disposing)
    {
        if (_ptr is not null)
        {
            NativeMemory.AlignedFree(_ptr);
            _ptr = null;
        }
    }
}
