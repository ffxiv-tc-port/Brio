using Brio.Capabilities.Actor;
using Brio.Capabilities.Posing;
using Brio.Config;
using Brio.Entities.Core;
using Brio.Game.Actor;
using Brio.Game.Actor.Extensions;
using Brio.UI.Controls;
using Brio.UI.Controls.Stateless;
using Brio.UI.Theming;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Plugin.Services;
using Microsoft.Extensions.DependencyInjection;
using System;

namespace Brio.Entities.Actor
{
    public class ActorEntity(IGameObject gameObject, IServiceProvider provider) : Entity(new EntityId(gameObject), provider)
    {
        public readonly IGameObject GameObject = gameObject;

        private readonly ConfigurationService _configService = provider.GetRequiredService<ConfigurationService>();
        private readonly IObjectTable _objects = provider.GetRequiredService<IObjectTable>();

        // 建構當下(物件必定還活著)抄下來的物件表索引。只當作存活檢查的快路徑起點,不當身分用。
        private readonly int _objectTableIndex = gameObject.ObjectIndex;

        private string _lastKnownName = "";
        private FontAwesomeIcon _lastKnownIcon = FontAwesomeIcon.Question;

        /// <summary>
        /// GameObject 是 <c>IObjectTable.CreateObjectReference</c> 產生的獨立包裝(見 EntityActorManager),
        /// 不會被物件表槽位共用實例就地改寫;但它的 Address 是建構當下凍結的:角色消失而
        /// ObjectMonitorService 還沒把本實體拆下時,任何解參(Name / ObjectKind / ObjectIndex)都是懸空讀,
        /// 而 AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
        /// 這個檢查只讀物件表本身的指標陣列(GetObjectAddress),完全不解參已存的位址,所以永遠安全。
        /// </summary>
        public bool IsGameObjectAlive
        {
            get
            {
                var address = GameObject.Address;
                if(address == nint.Zero)
                    return false;

                // 快路徑:角色通常留在同一個槽位。
                if(_objects.GetObjectAddress(_objectTableIndex) == address)
                    return true;

                // 慢路徑:槽位真的換過就整張表比一次指標(只讀指標陣列,不建立任何包裝物件)。
                for(var i = 0; i < _objects.Length; i++)
                {
                    if(_objects.GetObjectAddress(i) == address)
                        return true;
                }

                return false;
            }
        }

        public string RawName = "";
        public override string FriendlyName
        {
            get
            {
                // 已經不在物件表裡就不要解參,退回最後一次成功讀到的名字(沒有就顯示 ??? 而不是空白或錯的名字)。
                if(IsGameObjectAlive == false)
                {
                    if(string.IsNullOrEmpty(_lastKnownName) == false)
                        return _lastKnownName;

                    return string.IsNullOrEmpty(RawName) ? "???" : RawName;
                }

                if(string.IsNullOrEmpty(RawName))
                {
                    _lastKnownName = _configService.Configuration.Interface.CensorActorNames ? GameObject.GetCensoredName() : GameObject.GetFriendlyName();
                    return _lastKnownName;
                }

                _lastKnownName = GameObject.GetAsCustomName(RawName);
                return _lastKnownName;
            }
            set
            {
                RawName = value;
                _lastKnownName = "";
            }
        }
        public override FontAwesomeIcon Icon
        {
            get
            {
                if(IsProp)
                    return FontAwesomeIcon.Cube;

                if(IsGameObjectAlive == false)
                    return _lastKnownIcon;

                _lastKnownIcon = GameObject.GetFriendlyIcon();
                return _lastKnownIcon;
            }
        }

        public unsafe override bool IsVisible => true;

        public override EntityFlags Flags => EntityFlags.AllowDoubleClick | EntityFlags.HasContextButton | EntityFlags.DefaultOpen;

        public override int ContextButtonCount => 1;

        public bool IsProp => ActorType == ActorType.Prop;

        public ActorType ActorType => GetActorType();

        private ActorType GetActorType()
        {
            if(SpawnFlag.HasFlag(SpawnFlags.IsEffect))
                return ActorType.Effect;
            if(SpawnFlag.HasFlag(SpawnFlags.IsProp))
                return ActorType.Prop;

            return ActorType.BrioActor;
        }

        public override void OnDoubleClick()
        {
            var aac = GetCapability<ActorAppearanceCapability>();
            RenameActorModal.Open(aac.Actor);
        }

        public override void DrawContextButton()
        {
            var aac = GetCapability<ActorAppearanceCapability>();

            using(ImRaii.PushColor(ImGuiCol.Button, ThemeManager.CurrentTheme.Accent.AccentColor, aac.IsHidden))
            {
                string toolTip = aac.IsHidden ? $"Show {aac.Actor.FriendlyName}" : $"Hide {aac.Actor.FriendlyName}";
                if(ImBrio.FontIconButtonRight($"###{Id}_hideActor", aac.IsHidden ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, 1f, toolTip, bordered: false))
                {
                    aac.ToggleHide();
                }
            }
        }

        public override void OnAttached()
        {
            AddCapability(ActivatorUtilities.CreateInstance<ActorLifetimeCapability>(_serviceProvider, this));
            AddCapability(ActivatorUtilities.CreateInstance<ActorAppearanceCapability>(_serviceProvider, this));

            if(ActorType is ActorType.BrioActor)
                AddCapability(ActorDynamicPoseCapability.CreateIfEligible(_serviceProvider, this));

            AddCapability(ActivatorUtilities.CreateInstance<SkeletonPosingCapability>(_serviceProvider, this));
            AddCapability(ActivatorUtilities.CreateInstance<ModelPosingCapability>(_serviceProvider, this));
            AddCapability(ActivatorUtilities.CreateInstance<PosingCapability>(_serviceProvider, this));

            AddCapability(ActionTimelineCapability.CreateIfEligible(_serviceProvider, this));

            if(ActorType is not ActorType.Prop)
            {
                if(ActorType is not ActorType.Effect)
                {
                    AddCapability(CompanionCapability.CreateIfEligible(_serviceProvider, this));
                }

                AddCapability(StatusEffectCapability.CreateIfEligible(_serviceProvider, this));
            }

            AddCapability(ActivatorUtilities.CreateInstance<ActorDebugCapability>(_serviceProvider, this));
        }
    }
}
