
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
        // 🔴 這裡是 + alignment,不是 + alignment - 1。
        // 位移的值域是 1..alignment(見上面的說明),所以最壞情況要多配一整個 alignment。
        // 少配一個位元組的後果是「從 Aligned 起算只有 sizeInBytes - 1 個位元組可以用」,
        // 而且不是偶爾:AllocHGlobal 在 x64 恆回 16 對齊,本外掛用的 alignment 又是 8 或 16,
        // 於是 base % alignment 恆為 0、位移恆等於一整個 alignment ⇒ 每一次配置都短一個位元組。
        int alignedSize = sizeInBytes + alignment;
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
