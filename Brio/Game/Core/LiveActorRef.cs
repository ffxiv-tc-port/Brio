using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Brio.Game.Core;

/// <summary>
/// 跨幀安全的角色參照。
///
/// <para>
/// 🔴 Dalamud 的 <see cref="IGameObject"/> 包裝在建構當下就把 <c>Address</c> 凍結,之後永不重新解析。
/// 角色消失後那個位址就懸空,任何解參(含 <c>Native()</c>、<c>Name</c>、<c>ObjectIndex</c>)都會踩到已釋放的記憶體;
/// <c>IsValid()</c> 只檢查「有沒有登入」(本 pin 的 Dalamud/Game/ClientState/Objects/Types/GameObject.cs:170-177
/// 逐字是 <c>if (actor == null) return false; return playerState.IsLoaded == true;</c>),對懸空位址零作用。
/// AccessViolationException 在 .NET Core 是 corrupted-state exception,<c>try/catch</c> 攔不到,結果是整個遊戲崩潰。
/// </para>
///
/// <para>
/// 本結構只保存兩個值型別(物件表索引 + 建構當下的位址),每次取用時重新問物件表要位址。
/// <see cref="IObjectTable.GetObjectAddress(int)"/> 只讀物件表自己的指標陣列(IndexSorted)並做邊界檢查,
/// 完全不解參任何先前存下來的位址 ⇒ 這個查詢本身永遠安全。
/// 位址對得起來才回傳,所以拿到的一定是「還在物件表裡的同一個物件」;
/// 角色已消失(或槽位被別人接手)時拿到的是 <see cref="nint.Zero"/> 而不是懸空位址。
/// </para>
///
/// <para>
/// ⚠️ 建構子會讀 <c>gameObject.ObjectIndex</c>(這是一次解參),所以 <b>只能在物件確定還活著的當下建構</b>
/// —— 也就是事件/呼叫發生的那一幀,不可以延後建構。與 <c>Brio.Entities.Actor.ActorEntity.IsGameObjectAlive</c> 同一套做法。
/// </para>
/// </summary>
public readonly struct LiveActorRef
{
    private readonly IObjectTable? _objects;
    private readonly int _objectIndex;
    private readonly nint _capturedAddress;

    /// <summary>在物件確定還活著的當下抄走它的物件表索引與位址。</summary>
    public LiveActorRef(IObjectTable objects, IGameObject gameObject)
        : this(objects, gameObject.ObjectIndex, gameObject.Address)
    {
    }

    public LiveActorRef(IObjectTable objects, int objectIndex, nint capturedAddress)
    {
        _objects = objects;
        _objectIndex = objectIndex;
        _capturedAddress = capturedAddress;
    }

    /// <summary>
    /// 完全不解參的建構方式:只抄走 <c>gameObject.Address</c>(讀的是包裝物件自己的欄位,不碰遊戲記憶體)。
    /// 呼叫端「不確定這個包裝是不是已經過期」時用這支 —— 例如延後好幾幀之後才拿到的 IGameObject。
    /// 代價是沒有索引可以走快路徑,每次查詢都要掃一次物件表的指標陣列(只讀指標,成本可忽略)。
    /// </summary>
    public static LiveActorRef FromAddress(IObjectTable objects, nint address) => new(objects, -1, address);

    /// <summary>
    /// 物件現在還在物件表裡就回傳它的位址,否則回傳 <see cref="nint.Zero"/>。
    /// 只讀物件表的指標陣列,不解參任何存下來的位址。
    /// </summary>
    public nint Address
    {
        get
        {
            if(_objects is null || _capturedAddress == nint.Zero)
                return nint.Zero;

            // 快路徑:知道索引(角色通常留在同一個槽位)時只要一次讀取。索引為負代表是 FromAddress 建的。
            if(_objectIndex >= 0 && _objects.GetObjectAddress(_objectIndex) == _capturedAddress)
                return _capturedAddress;

            // 慢路徑:槽位真的換過就整張表比一次指標(只讀指標陣列,不建立任何包裝物件)。
            for(var i = 0; i < _objects.Length; i++)
            {
                if(_objects.GetObjectAddress(i) == _capturedAddress)
                    return _capturedAddress;
            }

            return nint.Zero;
        }
    }

    /// <summary>物件是否還在物件表裡。</summary>
    public bool IsAlive => Address != nint.Zero;

    /// <summary>還活著就回傳原生 GameObject 指標,否則為 <c>null</c>。<b>拿到之後只能在同一幀之內使用。</b></summary>
    public unsafe NativeGameObject* GameObject => (NativeGameObject*)Address;

    /// <summary>還活著就回傳原生 Character 指標,否則為 <c>null</c>。<b>拿到之後只能在同一幀之內使用。</b></summary>
    public unsafe NativeCharacter* Character => (NativeCharacter*)Address;

    public override string ToString() => $"LiveActorRef(index: {_objectIndex}, address: 0x{_capturedAddress:X})";
}
