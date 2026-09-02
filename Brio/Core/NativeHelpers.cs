
using System.Runtime.InteropServices;

namespace Brio.Core;
public static class NativeHelpers
{
    /// <summary>
    /// 配一塊對齊過的記憶體,回傳 (對齊後可以用的位址, 配置器給的基底位址)。
    ///
    /// <para>
    /// 🔴 <b>兩個值必定不相等。</b> 位移是 <c>alignment - (base % alignment)</c>,值域 <c>1..alignment</c> ——
    /// 基底本來就對齊時算出來的是一整個 <c>alignment</c>,<b>不是 0</b>。
    /// ⇒ <b>釋放一定要用 <see cref="FreeAlignedMemory"/>(它釋放 Unaligned)</b>;
    /// 拿 <c>Aligned</c> 去 <see cref="FreeMemory"/> 是對堆積區塊中間呼叫 <c>LocalFree</c>,
    /// 結果是堆積損壞,而且當場不報錯。
    /// </para>
    /// </summary>
    public static (nint Aligned, nint Unaligned) AllocateAlignedMemory(int sizeInBytes, int alignment)
    {
        int alignedSize = sizeInBytes + alignment - 1;
        nint unalignedMemory = Marshal.AllocHGlobal(alignedSize);
        int alignmentOffset = (int)(alignment - (unalignedMemory % alignment));
        nint alignedMemory = unalignedMemory + alignmentOffset;

        return (alignedMemory, unalignedMemory);
    }

    public static void FreeAlignedMemory((nint Aligned, nint Unaligned) addrs)
    {
        Marshal.FreeHGlobal(addrs.Unaligned);
    }

    /// <summary>
    /// 釋放一塊<b>直接由 <c>Marshal.AllocHGlobal</c> 配出來、沒有做過對齊調整</b>的記憶體。
    /// 🔴 <b>絕對不要拿 <see cref="AllocateAlignedMemory"/> 的 <c>Aligned</c> 餵給它</b> —— 那個位址
    /// 一定不是配置基底(位移永遠 ≥ 1),會弄壞堆積。那種情形要用 <see cref="FreeAlignedMemory"/>。
    /// </summary>
    public static void FreeMemory(nint addr)
    {
        Marshal.FreeHGlobal(addr);
    }
}
