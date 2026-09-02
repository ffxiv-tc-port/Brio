
//
// Brio's lights would not be possible without the help of the following projects:
// Massive Help with Dynamis: https://github.com/Exter-N/Dynamis by Exter-N (Ny)
// LightsCameraAction: https://github.com/NeNeppie/LightsCameraAction by NeNeppie
// ZoomTilt https://github.com/Tenrys/ZoomTilt
// Some signatures from Ktisis: https://github.com/ktisis-tools/Ktisis
//

using Brio.Core;
using Brio.Entities;
using Brio.Entities.World;
using Brio.Game.Camera;
using Brio.Game.GPose;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using StructsTransforms = FFXIVClientStructs.FFXIV.Client.Graphics.Transform;

namespace Brio.Game.World;


// TODO (Ken) Separate this file's types into their own files

public unsafe class LightingService : IDisposable
{
    public LightGizmoOperation Operation { get; set; } = LightGizmoOperation.Universal;
    public LightGizmoCoordinateMode CoordinateMode { get; set; } = LightGizmoCoordinateMode.Local;

    //

    private readonly IFramework _framework;
    private readonly IServiceProvider _serviceProvider;
    private readonly GPoseService _gPoseService;
    private readonly EntityManager _entityManager;
    private readonly VirtualCameraManager _virtualCameraManager;

    // Spawn Lights
    private readonly unsafe delegate* unmanaged<GameLight*, void> _spawnGameLight;
    private readonly unsafe delegate* unmanaged<GameLight*, void> _spawnGameLightCreate;
    private readonly unsafe delegate* unmanaged<GameLight*, void> _spawnGameLightFinalize;

    // Update Lights
    private readonly unsafe delegate* unmanaged<LightRenderObject*, char, void> _updateGameLightRange;
    private readonly unsafe delegate* unmanaged<GameLight*, void> _updateGameLightCulling;
    private readonly unsafe delegate* unmanaged<GameLight*, void> _updateGameLightMaterial;

    private delegate bool ToggleLightDelegate(EventGPoseControllerEX* state, uint index);
    private readonly Hook<ToggleLightDelegate>? _toggleLightHook;

    private readonly unsafe delegate* unmanaged<EventGPoseControllerEX*, uint, char> _toggleGPoseLight;

    //
    //

    private readonly ComponentSet<IGameLight> _spawnedLights = [];
    private readonly ComponentSet<LightEntity> _lightEntities = [];

    // EventFramework.Instance() 可能為 null;對 null 取欄位位址不會當場崩,但回傳的指標一解參考就是 AVE。
    // 實作搬到 EventGPoseControllerEX.Current(靜態),讓不持有本服務的 Light 也查得到 GPose 光源槽位。
    public unsafe EventGPoseControllerEX* CurrentGPoseState => EventGPoseControllerEX.Current;

    public int SpawnedLightEntitiesCount => _lightEntities.ActiveCount;
    public List<LightEntity> SpawnedLightEntities => [.. _lightEntities];

    //

    public LightEntity? SelectedLightEntity = null;

    //

    public unsafe LightingService(IServiceProvider serviceProvider, EntityManager entityManager, GPoseService gPoseService, VirtualCameraManager virtualCameraManager, IFramework framework, ISigScanner sigScanner, IGameInteropProvider hooks)
    {
        _serviceProvider = serviceProvider;
        _gPoseService = gPoseService;
        _entityManager = entityManager;
        _virtualCameraManager = virtualCameraManager;
        _framework = framework;

        var spawnGameLightAddress = NativeBinding.Scan(sigScanner, "E8 ?? ?? ?? ?? 48 89 84 ?? ?? ?? ?? ?? 48 85 C0 0F ?? ?? ?? ?? ?? 48 8B C8", "光源:配置 SpawnGameLight");
        _spawnGameLight = (delegate* unmanaged<GameLight*, void>)spawnGameLightAddress;

        var createGameLightAddress = NativeBinding.Scan(sigScanner, "E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 48 8B D3 E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8B ?? ?? ?? ?? ?? 40 0F", "光源:建立 CreateGameLight");
        _spawnGameLightCreate = (delegate* unmanaged<GameLight*, void>)createGameLightAddress;

        var finalizeGameLightAddress = NativeBinding.Scan(sigScanner, "F6 41 38 01 ?? ?? 48 8B ?? ?? ?? ?? ?? 48", "光源:結算 FinalizeGameLight");
        _spawnGameLightFinalize = (delegate* unmanaged<GameLight*, void>)finalizeGameLightAddress;

        var updateGameLightTypeRangeAddress = NativeBinding.Scan(sigScanner, "E8 ?? ?? ?? ?? 48 8D 8F ?? ?? ?? ?? FF 15 ?? ?? ?? ?? 48 8D 8F ?? ?? ?? ?? 48 8D 55", "光源:類型/範圍更新");
        _updateGameLightRange = (delegate* unmanaged<LightRenderObject*, char, void>)updateGameLightTypeRangeAddress;

        // 上游把位移常數整個 wildcard 掉,在台服有 4 個命中(另外三支讀的是 [rcx+0x250] / [rcx+0x268],
        // 屬於別的類別)。把 0x80 這個位移寫死之後在台服是唯一命中,解出來與原本取到的第一個命中相同。
        var updateGameLightCullingAddress = NativeBinding.Scan(sigScanner, "48 89 5C 24 ?? 57 48 83 EC 40 48 8B B9 80 00 00 00", "光源:剔除更新 UpdateCulling");
        _updateGameLightCulling = (delegate* unmanaged<GameLight*, void>)updateGameLightCullingAddress;

        var updateGameLightMaterialAddress = NativeBinding.Scan(sigScanner, "40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 75 45 0C 04 B2 05", "光源:材質更新 UpdateMaterial");
        _updateGameLightMaterial = (delegate* unmanaged<GameLight*, void>)updateGameLightMaterialAddress;

        var toggleLightHookAddress = NativeBinding.Scan(sigScanner, "48 83 EC 28 4C 8B C1 83 FA 03 ?? ?? 8B C2", "光源:GPose 光源開關 ToggleLight");

        _toggleGPoseLight = (delegate* unmanaged<EventGPoseControllerEX*, uint, char>)toggleLightHookAddress;

        _toggleLightHook = NativeBinding.CreateHook<ToggleLightDelegate>(hooks, toggleLightHookAddress, ToggleLightDetour, "光源:GPose 光源開關 ToggleLight");

        _gPoseService.OnGPoseStateChange += OnGPoseStateChange;
        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>自訂光源需要的六個原生函式全部繫結成功才可用。</summary>
    public bool IsLightingAvailable => _spawnGameLight != null && _spawnGameLightCreate != null && _spawnGameLightFinalize != null
        && _updateGameLightCulling != null && _updateGameLightMaterial != null && _updateGameLightRange != null;

    public char ToggleGPoseLight(EventGPoseControllerEX* ptr, uint index)
        => _toggleGPoseLight == null ? (char)0 : _toggleGPoseLight(ptr, index);

    public unsafe bool ToggleLightDetour(EventGPoseControllerEX* state, uint index)
    {
        //
        // This is using a similar method that Ktisis' uses for a OnGposeLightToggle
        // It has issues like when using the Gpose Load/Save light preset it will desync from Brio
        // This is because this only fires when you "click" the light toggle buttons in Gpose
        //

        var result = _toggleLightHook!.Original(state, index);

        try
        {
            var gposeLight = state->GetLight(index);

            if(gposeLight != null)
            {
                Light light = new(gposeLight, gposeLight->Transform.Position, gposeLight->Transform.Rotation, gposeLight->Transform.Scale)
                {
                    IsGPoseLight = true,
                    GposeLightIndex = index
                };
                light.SetIndex(_spawnedLights.Add(light));

                SpawnGPoseLight(light);
            }
            else if(gposeLight == null)
            {
                var lightToRemove = _spawnedLights.AsEnumerable().FirstOrDefault(x => x.IsGPoseLight && x.GposeLightIndex == index);

                if(lightToRemove is not null)
                    RemoveGposeLight(lightToRemove);
            }
        }
        catch(Exception ex)
        {
            Brio.Log.Error(ex, "An Exception while trying to handle a Gpose light toggle");
        }

        return result;
    }

    //

    public void UpdateLight(GameLight* light)
    {
        if(light is not null && _updateGameLightCulling != null && _updateGameLightMaterial != null)
        {
            _updateGameLightCulling(light);
            _updateGameLightMaterial(light);
        }
    }

    public unsafe void SpawnGPoseLight(Light light)
    {
        LightEntity camEnt = ActivatorUtilities.CreateInstance<LightEntity>(_serviceProvider, light);

        if(_entityManager.TryGetEntity("environment", out var ent))
        {
            _entityManager.AttachEntity(camEnt, ent);

            light.SetEntityIndex(_lightEntities.Add(camEnt));
        }
        else
        {
            // TODO: Remove the light we just created if the entity is not found
        }
    }

    public unsafe void SpawnLight(LightType lightType)
    {
        _framework.RunOnFrameworkThread(() =>
        {
            var gamelight = SpawnGameLight(lightType, out var allocationBase);

            // 🔴 SpawnGameLight 在特徵碼沒繫結上時會回 null(見那支的註解)。這裡以前直接解參,
            //    對 null 讀 Transform 是必崩的存取違規,而且 AVE 是 corrupted-state exception 攔不到。
            if(gamelight is null)
            {
                Brio.Log.Information("原生光源函式沒有繫結成功,略過生成光源。");
                return;
            }

            Light light = new(gamelight, allocationBase, gamelight->Transform.Position, gamelight->Transform.Rotation, gamelight->Transform.Scale);
            light.SetIndex(_spawnedLights.Add(light));

            UpdateLight(gamelight);

            LightEntity camEnt = ActivatorUtilities.CreateInstance<LightEntity>(_serviceProvider, light);

            if(_entityManager.TryGetEntity("environment", out var ent))
            {
                _entityManager.AttachEntity(camEnt, ent);

                light.SetEntityIndex(_lightEntities.Add(camEnt));
            }
            else
            {
                // TODO: Remove the light we just created if the entity is not found
            }

            foreach(var gameLight in _spawnedLights.AsEnumerable())
            {
                Brio.Log.Debug($"GameLight addres: {gameLight.Address}");
            }

        });
    }

    /// <summary>
    /// 配置並初始化一盞 Brio 自己的光源。原生函式沒繫結上時回 <c>null</c>,<b>呼叫端必須判空</b>。
    ///
    /// <para>
    /// 🔴 <paramref name="allocationBase"/> 是 <c>Marshal.AllocHGlobal</c> 回傳的<b>未對齊基底位址</b>,
    /// 釋放光源時<b>只能</b>用它,不可以用回傳的(已對齊的)光源指標。
    /// <c>NativeHelpers.AllocateAlignedMemory</c> 算的位移是 <c>alignment - (base % alignment)</c>,
    /// 值域是 <c>1..alignment</c> —— <b>永遠不會是 0</b>(基底本來就對齊時得到的是一整個 alignment)。
    /// 所以對齊後的指標與配置基底<b>必定不同</b>,拿對齊後的指標去 <c>Marshal.FreeHGlobal</c>
    /// 等於對堆積區塊的中間位置呼叫 <c>LocalFree</c>:堆積損壞,而且當場不會報錯,
    /// 要等到之後某次不相干的配置才炸,現場完全指認不出來。
    /// 光源沒生出來時這個值是 <see cref="nint.Zero"/>。
    /// </para>
    /// </summary>
    public unsafe GameLight* SpawnGameLight(LightType lightType, out nint allocationBase)
    {
        allocationBase = nint.Zero;

        // 原生光源函式沒繫結上時直接放棄:呼叫 null 函式指標是 AVE,try/catch 攔不到。
        if(IsLightingAvailable == false)
            return null;

        // This causes memory fragmentation over time I think, maybe we can implement a pooling system later?
        var allocation = NativeHelpers.AllocateAlignedMemory(sizeof(GameLight), 8);
        allocationBase = allocation.Unaligned;
        GameLight* light = (GameLight*)allocation.Aligned;

        _spawnGameLight(light);
        _spawnGameLightCreate(light);
        _spawnGameLightFinalize(light);

        if(_virtualCameraManager.CurrentCamera is not null)
        {
            if(_virtualCameraManager.CurrentCamera.IsFreeCamera)
            {
                light->Transform.Position = _virtualCameraManager.CurrentCamera.Position;
                light->Transform.Rotation = _virtualCameraManager.CurrentCamera.Rotation.ToEulerAngles();
            }
            else
            {
                light->Transform.Position = _virtualCameraManager.CurrentCamera.BrioCamera->Position;
                light->Transform.Rotation = _virtualCameraManager.CurrentCamera.BrioCamera->CalculateDirectionAsQuaternion();
            }
        }

        if(light->LightRenderObject != null)
        {
            light->LightRenderObject->EmissionType = lightType;
            light->LightRenderObject->Transform = &light->Transform;
            light->LightRenderObject->LightFlags = LightFlags.Reflection;

            light->LightRenderObject->Color = new Vector3(20f);
            light->LightRenderObject->Intensity = 1f;

            light->LightRenderObject->FalloffType = FalloffType.Quadratic;
            light->LightRenderObject->Falloff = 1f;
            light->LightRenderObject->LightAngle = 45.0f;
            light->LightRenderObject->FalloffAngle = 0.5f;

            light->LightRenderObject->Range = 35;
            light->LightRenderObject->Angle = Vector2.Zero;

            light->LightRenderObject->CharacterShadowRange = 110f;
            light->LightRenderObject->ShadowPlaneNear = 0.01f;
            light->LightRenderObject->ShadowPlaneFar = 17.0f;
        }

        return light;
    }

    public void Clone(IGameLight sourceLight)
    {
        if(sourceLight == null || !sourceLight.IsValid)
        {
            Brio.Log.Error("Cannot clone an invalid or null light.");
            return;
        }

        _framework.RunOnFrameworkThread(() =>
        {
            // Spawn a new GameLight
            var clonedGameLight = SpawnGameLight(sourceLight.GameLight->LightRenderObject->EmissionType, out var allocationBase);

            // 🔴 同 SpawnLight:特徵碼沒繫結上時 SpawnGameLight 回 null,解參就是存取違規。
            if(clonedGameLight is null)
            {
                Brio.Log.Information("原生光源函式沒有繫結成功,略過複製光源。");
                return;
            }

            Light clonedLight = new(clonedGameLight, allocationBase, clonedGameLight->Transform.Position, clonedGameLight->Transform.Rotation, clonedGameLight->Transform.Scale);
            clonedLight.SetIndex(_spawnedLights.Add(clonedLight));

            // Copy properties from the source light to the cloned light
            clonedGameLight->Transform.Position = sourceLight.GameLight->Transform.Position;
            clonedGameLight->Transform.Rotation = sourceLight.GameLight->Transform.Rotation;

            if(clonedGameLight->LightRenderObject != null && sourceLight.GameLight->LightRenderObject != null)
            {
                clonedGameLight->LightRenderObject->Color = sourceLight.GameLight->LightRenderObject->Color;
                clonedGameLight->LightRenderObject->Intensity = sourceLight.GameLight->LightRenderObject->Intensity;
                clonedGameLight->LightRenderObject->Range = sourceLight.GameLight->LightRenderObject->Range;
                clonedGameLight->LightRenderObject->Falloff = sourceLight.GameLight->LightRenderObject->Falloff;
                clonedGameLight->LightRenderObject->LightAngle = sourceLight.GameLight->LightRenderObject->LightAngle;
                clonedGameLight->LightRenderObject->FalloffAngle = sourceLight.GameLight->LightRenderObject->FalloffAngle;
                clonedGameLight->LightRenderObject->CharacterShadowRange = sourceLight.GameLight->LightRenderObject->CharacterShadowRange;
                clonedGameLight->LightRenderObject->ShadowPlaneNear = sourceLight.GameLight->LightRenderObject->ShadowPlaneNear;
                clonedGameLight->LightRenderObject->ShadowPlaneFar = sourceLight.GameLight->LightRenderObject->ShadowPlaneFar;
            }

            UpdateLight(clonedGameLight);

            if(_entityManager.TryGetEntity("environment", out var ent))
            {
                var camEnt = ActivatorUtilities.CreateInstance<LightEntity>(_serviceProvider, clonedLight);
                _entityManager.AttachEntity(camEnt, ent);

                clonedLight.SetEntityIndex(_lightEntities.Add(camEnt));
            }
            else
            {
                // TODO: Remove the light we just created if the entity is not found
            }

            foreach(var gameLight in _spawnedLights.AsEnumerable())
            {
                Brio.Log.Debug($"Cloned GameLight address: {gameLight.Address}");
            }
        });
    }

    //

    public LightData? SaveLight(IGameLight light)
    {
        try
        {
            if(light == null || !light.IsValid)
            {
                Brio.Log.Warning("Cannot save an invalid or null light.");
                return null;
            }

            return new LightData
            {
                AbsolutePosition = light.Position,
                Rotation = light.Rotation,
                Color = light.GameLight->LightRenderObject->Color,
                Intensity = light.GameLight->LightRenderObject->Intensity,
                Range = light.GameLight->LightRenderObject->Range,
                Falloff = light.GameLight->LightRenderObject->Falloff,
                LightAngle = light.GameLight->LightRenderObject->LightAngle,
                FalloffAngle = light.GameLight->LightRenderObject->FalloffAngle,
                CharacterShadowRange = light.GameLight->LightRenderObject->CharacterShadowRange,
                ShadowPlaneNear = light.GameLight->LightRenderObject->ShadowPlaneNear,
                ShadowPlaneFar = light.GameLight->LightRenderObject->ShadowPlaneFar,
                LightType = light.GameLight->LightRenderObject->EmissionType
            };
        }
        catch(Exception ex)
        {
            Brio.Log.Error("Failed to save light.", ex);
        }

        return null;
    }

    public void LoadLight(LightData lightData, IGameLight igameLight, Vector3? centralPosition = null)
    {
        try
        {
            // var lightData = JsonSerializer.Deserialize<LightData>(json);

            if(lightData is null)
            {
                Brio.Log.Warning("No light data to load.");
                return;
            }

            _framework.RunOnFrameworkThread(() =>
            {
                GameLight* gameLight = igameLight.GameLight;

                // 沿用既有光源時這裡是 Zero:那塊記憶體的配置基底在原本那個 Light 身上,
                // 由它負責釋放。這個新包裝不是配置者,絕對不可以跟著釋放(會變成重複釋放)。
                nint allocationBase = nint.Zero;

                if(gameLight is null)
                {
                    gameLight = SpawnGameLight(lightData.LightType, out allocationBase);

                    // 🔴 同 SpawnLight:特徵碼沒繫結上時 SpawnGameLight 回 null,解參就是存取違規。
                    if(gameLight is null)
                    {
                        Brio.Log.Information("原生光源函式沒有繫結成功,略過載入光源。");
                        return;
                    }
                }

                // Adjust position relative to the central position if provided
                gameLight->Transform.Position = centralPosition.HasValue
                    ? centralPosition.Value + lightData.RelativePosition
                    : lightData.AbsolutePosition;

                gameLight->Transform.Rotation = lightData.Rotation;

                if(gameLight->LightRenderObject != null)
                {
                    gameLight->LightRenderObject->Color = lightData.Color;
                    gameLight->LightRenderObject->Intensity = lightData.Intensity;
                    gameLight->LightRenderObject->Range = lightData.Range;
                    gameLight->LightRenderObject->Falloff = lightData.Falloff;
                    gameLight->LightRenderObject->LightAngle = lightData.LightAngle;
                    gameLight->LightRenderObject->FalloffAngle = lightData.FalloffAngle;
                    gameLight->LightRenderObject->CharacterShadowRange = lightData.CharacterShadowRange;
                    gameLight->LightRenderObject->ShadowPlaneNear = lightData.ShadowPlaneNear;
                    gameLight->LightRenderObject->ShadowPlaneFar = lightData.ShadowPlaneFar;
                }

                UpdateLight(gameLight);

                var light = new Light(gameLight, allocationBase, gameLight->Transform.Position, gameLight->Transform.Rotation, gameLight->Transform.Scale);
                light.SetIndex(_spawnedLights.Add(light));

                if(_entityManager.TryGetEntity("environment", out var ent))
                {
                    var camEnt = ActivatorUtilities.CreateInstance<LightEntity>(_serviceProvider, light);
                    _entityManager.AttachEntity(camEnt, ent);

                    light.SetEntityIndex(_lightEntities.Add(camEnt));
                }
            });

            Brio.Log.Info($"Light loaded from {igameLight.Index}");
        }
        catch(Exception ex)
        {
            Brio.Log.Error("Failed to load light.", ex);
        }
    }

    //

    public unsafe void RemoveGposeLight(IGameLight light)
    {
        _spawnedLights.Remove(light.Index);

        _framework.RunOnFrameworkThread(() =>
        {
            // 🔴 這裡原本寫 light.IsValid &&。本函式唯一的呼叫端是 ToggleLightDetour 判定
            //    「遊戲剛剛把這個 GPose 光源槽位清掉」之後 —— 也就是 IsValid 為 false 才會走到這裡。
            //    IsValid 改成真的會查槽位之後,那個條件會永遠不成立,實體就再也拆不下來
            //    (UI 會留著一個指向已消失光源的項目)。拆 UI 實體與原生指標還有沒有效本來就無關,所以拿掉。
            if(_entityManager.TryGetEntity("environment", out var ent))
            {
                var camEnt = _lightEntities.Components[light.Index];
                if(camEnt is not null)
                {
                    ent.RemoveChild(camEnt);
                    _lightEntities.Remove(light.Index);
                }
            }
        });
    }

    public unsafe void Destroy(IGameLight light)
    {
        _spawnedLights.Remove(light.Index);

        _framework.RunOnFrameworkThread(() =>
        {
            if(light.IsGPoseLight && CurrentGPoseState != null)
            {
                ToggleGPoseLight(CurrentGPoseState, light.GposeLightIndex);
            }

            light.Destroy();

            if(_entityManager.TryGetEntity("environment", out var ent))
            {
                var camEnt = _lightEntities.Components[light.Index];
                if(camEnt is not null)
                {
                    ent.RemoveChild(camEnt);
                    _lightEntities.Remove(light.Index);
                }
            }
        });
    }

    public unsafe void DestroyAllLights()
    {
        if(_framework.IsFrameworkUnloading)
            return;

        _framework.RunOnFrameworkThread(() =>
        {
            foreach(var lights in _spawnedLights)
            {
                Destroy(lights);
            }

            _spawnedLights.Clear();
            _lightEntities.Clear();
        });
    }

    /// <summary>
    /// 每幀把 Brio 管著的光源推回渲染器。
    ///
    /// <para>
    /// 🔴 條件原本是 <c>IsGPosing || IsFrameworkUnloading == false</c>。Dalamud 的
    /// <c>Framework.HandleFrameworkDestroy</c> 是先 <c>frameworkDestroy.Cancel()</c>
    /// (<c>IsFrameworkUnloading</c> 從此為 true)再<b>緊接著</b>把 <c>DispatchUpdateEvents</c> 設為 false,
    /// 而 <c>Update</c> 事件只在 <c>DispatchUpdateEvents</c> 為 true 時才發送 ——
    /// 兩者都在遊戲主執行緒上、Update 與 Destroy 兩個 hook 不會交錯,
    /// 所以<b>在 Update 處理常式裡 <c>IsFrameworkUnloading</c> 恆為 false</b>。
    /// 於是舊式的右半恆真 ⇒ 整條恆真 ⇒ <c>IsGPosing</c> 完全沒有作用;
    /// 而它唯一能讓右半變 false 的情況(卸載中)反而要靠左半的 <c>IsGPosing</c> 把迴圈<b>打開</b>,
    /// 正好是那個卸載檢查想擋的事。原意是 <c>&amp;&amp;</c>。
    /// </para>
    ///
    /// <para>
    /// 改成 <c>&amp;&amp;</c> 之後「不在 GPose」就整段不跑,這是安全的:光源只能在 GPose 中生出來
    /// (<c>EnvironmentContainerEntity.DrawContextButton</c> 與 <c>LightContainerCapability.IsAllowed</c>
    /// 兩個入口都用 <c>IsGPosing</c> 鎖住),而離開 GPose 時 <c>OnGPoseStateChange(false)</c> 會
    /// <c>DestroyAllLights()</c> 清空 <c>_spawnedLights</c>。也就是說正常路徑上「不在 GPose」時
    /// 本來就沒有東西要更新;真的有燈沒清乾淨時,停止每幀去解參考遊戲可能已經收回的記憶體才是對的。
    /// <c>IsGPosing</c> 含 Brio 自己的 <c>FakeGPose</c>,所以假 GPose 模式不受影響。
    /// </para>
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if(_gPoseService.IsGPosing && framework.IsFrameworkUnloading == false)
        {
            foreach(var light in _spawnedLights.AsEnumerable().Where(x => x.IsValid))
            {
                if(light.GameLight->LightFlags == 0)
                    continue;

                UpdateLight(light.GameLight);
            }
        }
    }

    private void OnGPoseStateChange(bool newState)
    {
        if(newState is false)
        {
            DestroyAllLights();
        }
    }

    public void Dispose()
    {
        _toggleLightHook?.Dispose();

        _gPoseService.OnGPoseStateChange -= OnGPoseStateChange;
        _framework.Update -= OnFrameworkUpdate;

        DestroyAllLights();

        GC.SuppressFinalize(this);
    }
}

[StructLayout(LayoutKind.Explicit)]
public struct EventGPoseControllerEX
{
    [FieldOffset(0x000)] public EventGPoseController EventGPoseController;

    [FieldOffset(0x0E0)] public unsafe fixed ulong Lights[3];

    /// <summary>
    /// 目前的 GPose 光源控制器,取不到時為 <c>null</c>。
    /// EventFramework 是長生單例、EventSceneModule 與 EventGPoseController 都是內嵌欄位,
    /// 所以這個指標的壽命跟遊戲行程一樣長 —— 但 <c>Instance()</c> 在還沒建立前會是 null,
    /// 對 null 取欄位位址不會當場崩,回傳的指標一解參考才是 AVE,所以這裡先擋掉。
    /// </summary>
    public static unsafe EventGPoseControllerEX* Current
    {
        get
        {
            var eventFramework = EventFramework.Instance();
            if(eventFramework == null)
                return null;

            return (EventGPoseControllerEX*)&eventFramework->EventSceneModule.EventGPoseController;
        }
    }

    // 🔴 Lights 是 fixed ulong[3]。上游沒有邊界檢查,index >= 3 會讀到陣列外的位元組
    //    再當成指標解參考 —— 那是 AccessViolation,try/catch 攔不到。
    public const uint LightCount = 3;

    public unsafe GameLight* GetLight(uint index) => index < LightCount ? (GameLight*)Lights[index] : null;
}

public class LightData
{
    public Vector3 AbsolutePosition { get; set; }
    public Vector3 RelativePosition { get; set; }

    public Quaternion Rotation { get; set; }

    public LightType LightType { get; set; }

    public Vector3 Color { get; set; }
    public float Intensity { get; set; }
    public float Range { get; set; }
    public float Falloff { get; set; }
    public float LightAngle { get; set; }
    public float FalloffAngle { get; set; }
    public float CharacterShadowRange { get; set; }
    public float ShadowPlaneNear { get; set; }
    public float ShadowPlaneFar { get; set; }
}

public unsafe class Light : IGameLight, IDisposable
{
    private GameLight* _gameLight;

    /// <summary>
    /// 這盞光源的記憶體是<b>這個包裝</b>配出來的時候,存 <c>Marshal.AllocHGlobal</c> 的未對齊基底位址;
    /// 否則是 <see cref="nint.Zero"/>(遊戲配的 GPose 光源、或沿用別的 <see cref="Light"/> 已經擁有的指標)。
    /// <see cref="Destroy"/> 只有在這個值非零時才釋放記憶體 —— 見那支的註解。
    /// </summary>
    private readonly nint _allocationBase;

    private int _index;
    private int _entityIndex;

    public int Index => _index;
    public int EntityIndex => _entityIndex;

    /// <summary>
    /// 這個光源現在還能不能解參考。<b>兩種光源的存活判定不一樣,不要合併。</b>
    ///
    /// <para>
    /// <b>Brio 自己生的光源</b>(<see cref="IsGPoseLight"/> 為 false):記憶體是 LightingService.SpawnGameLight 裡
    /// <c>Marshal.AllocHGlobal</c> 配出來的,全外掛只有 <see cref="Destroy"/> 會釋放它,而且釋放的同一個
    /// 區塊裡就把 <c>_gameLight</c> 設回 null。沒有第三方能在我們背後把它收掉 ⇒ 判空就是正確的存活判定。
    /// </para>
    ///
    /// <para>
    /// 🔴 <b>GPose 光源</b>(<see cref="IsGPoseLight"/> 為 true):記憶體是<b>遊戲</b>配的,指標是從
    /// <c>EventGPoseController</c> 的 <c>Lights[3]</c> 抄下來的。Brio 不擁有它、也永遠不會把 <c>_gameLight</c> 設回 null
    /// (<see cref="Destroy"/> 對 GPose 光源整段跳過)⇒ 判空對這一族<b>完全沒有偵測力</b>:
    /// 使用者在 GPose 介面把燈關掉之後,這個欄位還留著已經失效的位址,而 <see cref="Position"/>、
    /// <see cref="Rotation"/> 與 LightingService.OnFrameworkUpdate 是<b>每幀</b>解參考的。
    /// AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
    /// </para>
    ///
    /// <para>
    /// 光源不在 IObjectTable 裡,所以 <c>LiveActorRef</c> 那一套用不上;但 GPose 光源有一個等價的可查詢容器 ——
    /// 就是它當初的來源 <c>Lights[3]</c> 本身。讀那個陣列只是讀 EventFramework 這個長生單例的記憶體,
    /// <b>不會解參考任何存下來的光源位址</b>,所以這個查詢本身永遠安全(與 IObjectTable.GetObjectAddress 同形狀)。
    /// 槽位裡還是同一個指標才算活著;被清掉或換成別的燈都回 false。
    /// </para>
    /// </summary>
    public bool IsValid
    {
        get
        {
            if(_gameLight == null)
                return false;

            // Brio 自己配的記憶體,只有 Destroy() 會釋放,而它會同時把欄位設回 null。
            if(IsGPoseLight == false)
                return true;

            var controller = EventGPoseControllerEX.Current;
            if(controller == null)
                return false;

            // GetLight 對 index >= LightCount 回 null,不會讀出陣列外的位元組。
            return controller->GetLight(GposeLightIndex) == _gameLight;
        }
    }

    public GameLight* GameLight => _gameLight;
    public IntPtr Address => (nint)GameLight;

    // 🔴 這兩個是每幀被讀的。光源已經沒了就不要解參考(GPose 光源由遊戲釋放,見 IsValid);
    //    回退成原點/單位四元數,而不是踩已釋放的記憶體 —— AVE 在 .NET Core 是 corrupted-state exception,攔不到。
    public Vector3 Position => IsValid ? GameLight->Transform.Position : Vector3.Zero;
    public Quaternion Rotation => IsValid ? GameLight->Transform.Rotation : Quaternion.Identity;

    public Vector3 SpawnPosition { get; set; }
    public Quaternion SpawnRotation { get; set; }
    public Vector3 SpawnScale { get; set; }

    public bool IsGismoVisible { get; set; } = false;
    public bool NeedsUpdate { get; set; }

    public bool IsGPoseLight { get; set; }
    public uint GposeLightIndex { get; set; }

    /// <summary>
    /// 包裝一盞<b>不是這個包裝配出來的</b>光源(遊戲的 GPose 光源,或別的 <see cref="Light"/> 已經擁有的指標)。
    /// 這樣建出來的包裝<b>不會釋放記憶體</b>。
    /// </summary>
    public Light(GameLight* gameLight, Vector3 position, Quaternion rotation, Vector3 scale)
        : this(gameLight, nint.Zero, position, rotation, scale)
    {
    }

    /// <summary>
    /// 包裝一盞 <c>LightingService.SpawnGameLight</c> 剛配出來的光源。
    /// <paramref name="allocationBase"/> 必須是那支交出來的<b>未對齊基底位址</b>,不是光源指標本身。
    /// </summary>
    public Light(GameLight* gameLight, nint allocationBase, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        _gameLight = gameLight;
        _allocationBase = allocationBase;

        SpawnPosition = position;
        SpawnRotation = rotation;
        SpawnScale = scale;
    }

    public void SetIndex(int index)
    {
        _index = index;
    }

    public void SetEntityIndex(int entityIndex)
    {
        _entityIndex = entityIndex;
    }

    /// <summary>
    /// 收掉這盞光源。<b>GPose 光源整段跳過</b> —— 那一族的記憶體是遊戲配的、也由遊戲釋放,
    /// Brio 動它就是釋放別人的堆積區塊。
    ///
    /// <para>
    /// 🔴 這裡以前是 <c>NativeHelpers.FreeMemory((nint)GameLight)</c>,也就是拿<b>對齊後</b>的指標
    /// 去呼叫 <c>Marshal.FreeHGlobal</c>。而 <c>NativeHelpers.AllocateAlignedMemory</c> 的位移
    /// (<c>alignment - (base % alignment)</c>)值域是 <c>1..alignment</c>、<b>永遠不是 0</b>,
    /// 所以那個指標一定落在配置區塊<b>中間</b>:每一次銷毀 Brio 自己的光源都是對 <c>base + 位移</c>
    /// 呼叫 <c>LocalFree</c> = 堆積損壞。而且當場不報錯,要等到之後某次不相干的配置才炸。
    /// 正解是把配置基底一路帶過來,交給 repo 裡本來就有、<c>IKService</c> 也用對了的
    /// <c>NativeHelpers.FreeAlignedMemory</c>。
    /// </para>
    ///
    /// <para>
    /// <c>_allocationBase</c> 是 <see cref="nint.Zero"/> 時代表這個包裝<b>不是配置者</b>
    /// (指標是別處交進來的),那就只做原生的解構、不碰配置器 —— 對不是自己配的位址呼叫
    /// <c>LocalFree</c> 跟上面那個 bug 是同一種傷害。
    /// </para>
    /// </summary>
    public void Destroy()
    {
        if(IsValid && IsGPoseLight is false)
        {
            var allocation = ((nint)GameLight, _allocationBase);

            GameLight->Destroy();

            if(_allocationBase != nint.Zero)
                NativeHelpers.FreeAlignedMemory(allocation);

            _gameLight = null;
        }
    }

    public virtual void Dispose()
    {
        Destroy();

        GC.SuppressFinalize(this);
    }
}

public unsafe interface IGameLight
{
    public int Index { get; }
    public int EntityIndex { get; }
    public bool IsValid { get; }
    public bool NeedsUpdate { get; set; }
    // 🔴 原本只判空。GPose 光源被遊戲收掉之後 GameLight 仍然不是 null(Brio 不擁有它、也不會把欄位設回 null),
    //    而這個屬性是在實體清單每幀畫按鈕時讀的 ⇒ 必須用 IsValid,它會去查 GPose 光源槽位。
    public bool IsVisible => IsValid && GameLight->LightFlags != 0;

    public Vector3 SpawnPosition { get; set; }
    public Quaternion SpawnRotation { get; set; }
    public Vector3 SpawnScale { get; set; }

    public GameLight* GameLight { get; }
    public IntPtr Address { get; }

    public Vector3 Position { get; }
    public Quaternion Rotation { get; }

    public bool IsGismoVisible { get; set; }
    public bool IsGPoseLight { get; set; }

    public uint GposeLightIndex { get; set; }

    public void Destroy();
    public void ToggleLight()
    {
        // 使用者按下按鈕的那一刻光源可能已經沒了(GPose 面板剛把它關掉);寫入前先確認。
        if(IsValid == false)
            return;

        GameLight->LightFlags = (byte)(GameLight->LightFlags == 0 ? 79 : 0);
    }
}

[StructLayout(LayoutKind.Explicit)]
public unsafe struct GameLightVirtualTable
{
    [FieldOffset(0)]
    public unsafe delegate* unmanaged<GameLight*, bool, void> Destructor;

    [FieldOffset(8)]
    public unsafe delegate* unmanaged<GameLight*, void> Cleanup;
}

[StructLayout(LayoutKind.Explicit, Size = 0xA0)]
public unsafe struct GameLight
{
    [FieldOffset(0x00)] public unsafe GameLightVirtualTable* VirtualTable;

    [FieldOffset(0x00)] public DrawObject DrawObject;
    [FieldOffset(0x50)] public StructsTransforms Transform;
    [FieldOffset(0x88)] public byte LightFlags;                      // This seems to be only useful for visibility? (0 = off, 79 = on)
    [FieldOffset(0x90)] public LightRenderObject* LightRenderObject; // GetObjectType() == 5


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Destroy()
    {
        VirtualTable->Cleanup((GameLight*)Unsafe.AsPointer(ref this));
        VirtualTable->Destructor((GameLight*)Unsafe.AsPointer(ref this), false);
    }
}

[StructLayout(LayoutKind.Explicit, Size = 0xA0)]
public unsafe struct LightRenderObject
{
    [FieldOffset(0x00)] public nint* VirtualTable;

    [FieldOffset(0x18)] public LightFlags LightFlags;
    [FieldOffset(0x1C)] public LightType EmissionType;
    [FieldOffset(0x20)] public StructsTransforms* Transform;
    [FieldOffset(0x28)] public Vector3 Color;
    [FieldOffset(0x34)] public float Intensity;
    [FieldOffset(0x40)] public Vector3 MaxRangeNegative;            // Gpose lights have "unlimited" (-10000) range
    [FieldOffset(0x50)] public Vector3 MaxRangePositive;            // Gpose lights have "unlimited" (10000) range
    [FieldOffset(0x60)] public float ShadowPlaneNear;
    [FieldOffset(0x64)] public float ShadowPlaneFar;
    [FieldOffset(0x68)] public FalloffType FalloffType;             // Type 1: 2 (Cubic), Type 2: 1 (Quadratic), Type 3: 0 (Linear)
    [FieldOffset(0x70)] public Vector2 Angle;
    [FieldOffset(0x80)] public float Falloff;
    [FieldOffset(0x84)] public float LightAngle;
    [FieldOffset(0x88)] public float FalloffAngle;
    [FieldOffset(0x8C)] public float Range;                         // Seems to be centered on the player
    [FieldOffset(0x90)] public float CharacterShadowRange;
}

[Flags]
public enum LightFlags
{
    Reflection = 1,
    Dynamic = 2,
    CharaShadow = 4,
    ObjectShadow = 8
}

public enum LightType : uint
{
    WorldLight = 1,
    AreaLight = 2,
    SpotLight = 3,
    FlatLight = 4
}

public enum FalloffType : uint
{
    Linear = 0,
    Quadratic = 1,
    Cubic = 2
}


//
// Did someone call for some tech debt?
// 

public enum LightGizmoCoordinateMode
{
    Local,
    World
}

public enum LightGizmoOperation
{
    Translate,
    Rotate,
    Universal
}

public static class LightExtensions
{
    public static ImGuizmoMode AsGizmoMode(this LightGizmoCoordinateMode mode) => mode switch
    {
        LightGizmoCoordinateMode.Local => ImGuizmoMode.Local,
        LightGizmoCoordinateMode.World => ImGuizmoMode.World,
        _ => ImGuizmoMode.Local
    };

    public static ImGuizmoOperation AsGizmoOperation(this LightGizmoOperation operation) => operation switch
    {
        LightGizmoOperation.Translate => ImGuizmoOperation.Translate,
        LightGizmoOperation.Rotate => ImGuizmoOperation.Rotate,
        LightGizmoOperation.Universal => ImGuizmoOperation.Translate | ImGuizmoOperation.Rotate,
        _ => ImGuizmoOperation.Universal
    };
}
