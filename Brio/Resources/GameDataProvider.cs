using Brio.Core;
using Brio.Game.Actor.Appearance;
using Brio.Resources.Extra;
using Brio.Resources.Sheets;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;
using Glasses = Lumina.Excel.Sheets.Glasses;

namespace Brio.Resources;

public class GameDataProvider
{
    public static GameDataProvider Instance { get; private set; } = null!;

    public IDataManager DataManager { get; private set; }

    public readonly IReadOnlyDictionary<uint, TerritoryType> TerritoryTypes;
    public readonly IReadOnlyDictionary<uint, Weather> Weathers;
    public readonly IReadOnlyDictionary<uint, WeatherRate> WeatherRates;
    public readonly IReadOnlyDictionary<uint, Companion> Companions;
    public readonly IReadOnlyDictionary<uint, Ornament> Ornaments;
    public readonly IReadOnlyDictionary<uint, Mount> Mounts;
    public readonly IReadOnlyDictionary<uint, Festival> Festivals;
    public readonly IReadOnlyDictionary<uint, Status> Statuses;
    public readonly IReadOnlyDictionary<uint, BrioActionTimeline> ActionTimelines;
    public readonly IReadOnlyDictionary<uint, Emote> Emotes;
    public readonly IReadOnlyDictionary<uint, Action> Actions;
    public readonly IReadOnlyDictionary<uint, ENpcBase> ENpcBases;
    public readonly IReadOnlyDictionary<uint, ENpcResident> ENpcResidents;
    public readonly IReadOnlyDictionary<uint, BNpcBase> BNpcBases;
    public readonly IReadOnlyDictionary<uint, BNpcCustomize> BNpcCustomizations;
    public readonly IReadOnlyDictionary<uint, BNpcName> BNpcNames;
    public readonly IReadOnlyDictionary<uint, NpcEquip> NpcEquips;
    public readonly IReadOnlyDictionary<uint, Stain> Stains;
    public readonly IReadOnlyDictionary<uint, CharaMakeCustomize> CharaMakeCustomizations;
    public readonly IReadOnlyDictionary<uint, BrioCharaMakeType> CharaMakeTypes;
    public readonly IReadOnlyDictionary<uint, BrioHairMakeType> HairMakeTypes;
    public readonly IReadOnlyDictionary<uint, Item> Items;
    public readonly IReadOnlyDictionary<uint, Glasses> Glasses;

    public readonly ModelDatabase ModelDatabase;

    public readonly HumanData HumanData;

    public GameDataProvider(IDataManager dataManager, ResourceProvider _resourceProvider)
    {
        Instance = this;

        TerritoryTypes = SafeSheet<TerritoryType>(dataManager, "TerritoryType");

        Weathers = SafeSheet<Weather>(dataManager, "Weather");

        WeatherRates = SafeSheet<WeatherRate>(dataManager, "WeatherRate");

        Companions = SafeSheet<Companion>(dataManager, "Companion");

        Ornaments = SafeSheet<Ornament>(dataManager, "Ornament");

        Mounts = SafeSheet<Mount>(dataManager, "Mount");

        Festivals = SafeSheet<Festival>(dataManager, "Festival");

        Statuses = SafeSheet<Status>(dataManager, "Status");

        ActionTimelines = SafeSheet<BrioActionTimeline>(dataManager, "BrioActionTimeline");

        Emotes = SafeSheet<Emote>(dataManager, "Emote");

        Actions = SafeSheet<Action>(dataManager, "Action");

        ENpcBases = SafeSheet<ENpcBase>(dataManager, "ENpcBase");

        ENpcResidents = SafeSheet<ENpcResident>(dataManager, "ENpcResident");

        BNpcBases = SafeSheet<BNpcBase>(dataManager, "BNpcBase");

        BNpcCustomizations = SafeSheet<BNpcCustomize>(dataManager, "BNpcCustomize");

        BNpcNames = SafeSheet<BNpcName>(dataManager, "BNpcName");

        NpcEquips = SafeSheet<NpcEquip>(dataManager, "NpcEquip");

        Stains = SafeSheet<Stain>(dataManager, "Stain");

        CharaMakeCustomizations = SafeSheet<CharaMakeCustomize>(dataManager, "CharaMakeCustomize");

        CharaMakeTypes = SafeSheet<BrioCharaMakeType>(dataManager, "BrioCharaMakeType");

        HairMakeTypes = SafeSheet<BrioHairMakeType>(dataManager, "BrioHairMakeType");

        Items = SafeSheet<Item>(dataManager, "Item");

        Glasses = SafeSheet<Glasses>(dataManager, "Glasses");

        HumanData = new HumanData(dataManager.GetFile("chara/xls/charamake/human.cmp")!.Data);

        ModelDatabase = new(_resourceProvider);

        DataManager = dataManager;
    }

    /// <summary>
    /// 讀一張 Excel 表並轉成字典。台服的表集合與國際服不同,缺表/欄位對不上時上游會直接
    /// NullReferenceException,而 GameDataProvider 是 DI 單例 ⇒ 整個外掛載不起來。
    /// 這裡改成:記一筆 Information 級診斷,回空字典,讓其餘功能照常運作。
    /// </summary>
    private static IReadOnlyDictionary<uint, T> SafeSheet<T>(IDataManager dataManager, string label)
        where T : struct, IExcelRow<T>
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<T>();
            if(sheet is not null)
                return sheet.ToDictionary(x => x.RowId, x => x).AsReadOnly();

            NativeBinding.Fail($"遊戲資料表 {label}", "本客戶端沒有這張表", "遊戲資料");
        }
        catch(System.Exception ex)
        {
            NativeBinding.Fail($"遊戲資料表 {label}", $"讀取失敗:{ex.Message}", "遊戲資料");
        }

        return new Dictionary<uint, T>().AsReadOnly();
    }
}
