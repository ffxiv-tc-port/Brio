
//
// Some code in this file was generated and is from the Lumina.Excel project (https://github.com/NotAdam/Lumina.Excel)
// (Lumina.Excel.Sheets.ActionTimeline)
//

using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace Brio.Resources.Sheets;

// 🔴 台服(TC 7.20)的 ActionTimeline 欄位布局與國際服不同:
//    byte 欄位是 offset 8~19(12 個),packed bool 在 offset 20;
//    上游(國際服 7.35)是 byte 8~18(11 個)+ packed bool 在 19。
//    離線實測自台服 sqpack 的 exd/ActionTimeline.exh(22 欄、data_offset=24)。
//    欄位雜湊也因此對不上,故移除 —— Dalamud 的 DataManager 設 PanicOnSheetChecksumMismatch=false,
//    留著錯的雜湊沒有保護作用,只會在有人開啟驗證時變成假警報。
[Sheet("ActionTimeline")]
readonly public struct BrioActionTimeline(ExcelPage page, uint offset, uint row) : IExcelRow<BrioActionTimeline>
{
    public uint RowId => row;

    public readonly ReadOnlySeString Key => page.ReadString(offset, offset);
    public readonly RowRef<WeaponTimeline> WeaponTimeline => new(page.Module, (uint)page.ReadUInt16(offset + 4), page.Language);
    public readonly ushort Unknown => page.ReadUInt16(offset + 6);
    public readonly byte Type => page.ReadUInt8(offset + 8);
    public readonly byte Priority => page.ReadUInt8(offset + 9);
    public readonly byte Stance => page.ReadUInt8(offset + 10);
    public readonly byte Slot => page.ReadUInt8(offset + 11);
    public readonly byte LookAtMode => page.ReadUInt8(offset + 12);
    public readonly byte ActionTimelineIDMode => page.ReadUInt8(offset + 13);
    public readonly byte LoadType => page.ReadUInt8(offset + 14);
    public readonly byte StartAttach => page.ReadUInt8(offset + 15);
    public readonly byte ResidentPap => page.ReadUInt8(offset + 16);
    public readonly byte Unknown6 => page.ReadUInt8(offset + 17);
    public readonly byte Unknown1 => page.ReadUInt8(offset + 18);
    public readonly bool Pause => page.ReadPackedBool(offset + 20, 0);
    public readonly bool Resident => page.ReadPackedBool(offset + 20, 1);
    public readonly bool IsMotionCanceledByMoving => page.ReadPackedBool(offset + 20, 2);
    public readonly bool Unknown2 => page.ReadPackedBool(offset + 20, 3);
    public readonly bool Unknown3 => page.ReadPackedBool(offset + 20, 4);
    public readonly bool IsLoop => page.ReadPackedBool(offset + 20, 5);
    public readonly bool Unknown4 => page.ReadPackedBool(offset + 20, 6);

    static BrioActionTimeline IExcelRow<BrioActionTimeline>.Create(ExcelPage page, uint offset, uint row) =>
        new(page, offset, row);
}
