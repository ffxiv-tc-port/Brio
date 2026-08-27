using Brio.Config;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System;

namespace Brio.IPC;

public class KtisisService : BrioIPC
{
    public override string Name => "Ktisis";

    public override bool IsAvailable => GetAPIVersion() is not (0, 0);

    public override bool AllowIntegration => true;

    public override int APIMajor => 1;

    public override int APIMinor => 0;

    // 對方外掛不存在時 InvokeFunc() 會擲 IpcNotReadyError,空條件運算子擋不到
    // (訂閱物件本身一定非 null,擲的是 InvokeFunc 內部)。
    public override (int Major, int Minor) GetAPIVersion()
    {
        try
        {
            if(_ktisisApiVersion is null || _ktisisApiVersion.HasFunction == false)
                return (0, 0);

            return _ktisisApiVersion.InvokeFunc();
        }
        catch(Exception)
        {
            return (0, 0);
        }
    }

    public override IDalamudPluginInterface GetPluginInterface()
        => _pluginInterface;

    //

    private readonly ConfigurationService _configurationService;
    private readonly IDalamudPluginInterface _pluginInterface;

    private readonly ICallGateSubscriber<(int, int)>? _ktisisApiVersion;

    //private readonly ICallGateSubscriber<IGameObject, Task<string?>> _ktisisLoadPose;
    //private readonly ICallGateSubscriber<IGameObject, string, Task<bool>> _ktisisSavePose;

    private readonly ICallGateSubscriber<bool>? _ktisisRefreshActors;
    private readonly ICallGateSubscriber<bool>? _ktisisIsPosing;


    public KtisisService(IDalamudPluginInterface pluginInterface, ConfigurationService configurationService)
    {
        _pluginInterface = pluginInterface;
        _configurationService = configurationService;

        _ktisisApiVersion = _pluginInterface.GetIpcSubscriber<(int, int)>("Ktisis.ApiVersion");
        _ktisisRefreshActors = _pluginInterface.GetIpcSubscriber<bool>("Ktisis.RefreshActors");
        _ktisisIsPosing = _pluginInterface.GetIpcSubscriber<bool>("Ktisis.IsPosing");
    }

    public bool IsPosing => ((_ktisisIsPosing?.HasFunction ?? false) && (_ktisisIsPosing?.InvokeFunc() ?? false));

    public void RefreshActors()
    {
        if(IsAvailable && !Disabled)
        {
            _ktisisRefreshActors?.InvokeFunc();
        }
    }

    public override void Dispose()
    {

    }
}
