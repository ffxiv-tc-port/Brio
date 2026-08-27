using Brio.Capabilities.Camera;
using Brio.Core;
using Brio.Entities;
using Brio.Entities.Camera;
using Brio.Game.Cutscene;
using Brio.Game.GPose;
using Brio.Input;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

using BrioRenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using BrioSceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;

namespace Brio.Game.Camera;

public unsafe class CameraService : IDisposable
{
    private readonly EntityManager _entityManager;
    private readonly GPoseService _gPoseService;
    private readonly CutsceneManager _cutsceneManager;
    private readonly VirtualCameraManager _virtualCameraService;

    private delegate nint CameraCollisionDelegate(BrioCamera* a1, Vector3* a2, Vector3* a3, float a4, nint a5, float a6);
    private readonly Hook<CameraCollisionDelegate>? _cameraCollisionHook;

    private delegate nint CameraUpdateDelegate(BrioCamera* camera);
    private readonly Hook<CameraUpdateDelegate>? _cameraUpdateHook;

    private delegate nint CameraSceneUpdate(BrioSceneCamera* gsc);
    private readonly Hook<CameraSceneUpdate>? _cameraSceneUpdateHook;

    private delegate Matrix4x4* ProjectionMatrix(IntPtr ptr, float fov, float aspect, float nearPlane, float farPlane, float a6, float a7);
    private static Hook<ProjectionMatrix>? _projectionHook;

    private delegate void CameraMatrixLoadDelegate(BrioRenderCamera* camera, nint a1);
    private readonly CameraMatrixLoadDelegate? _cameraMatrixLoad;

    public CameraService(EntityManager entityManager, VirtualCameraManager virtualCameraService, CutsceneManager cutsceneManager, GPoseService gPoseService, ISigScanner scanner, IGameInteropProvider hooking)
    {
        _entityManager = entityManager;
        _gPoseService = gPoseService;
        _cutsceneManager = cutsceneManager;
        _virtualCameraService = virtualCameraService;

        var cameraProjection = "E8 ?? ?? ?? ?? EB ?? F3 0F ?? ?? ?? ?? ?? ?? F3 0F ?? ?? ?? ?? E8 ?? ?? ?? ?? 0F ?? ?? ?? 48 ?? ?? ??";
        _projectionHook = NativeBinding.ScanHook<ProjectionMatrix>(scanner, hooking, cameraProjection, ProjectionDetour, "攝影機投影矩陣 ProjectionMatrix");

        var cameraCollisionAddr = "E8 ?? ?? ?? ?? 4C 8D 45 ?? 89 83";
        _cameraCollisionHook = NativeBinding.ScanHook<CameraCollisionDelegate>(scanner, hooking, cameraCollisionAddr, CameraCollisionDetour, "攝影機碰撞 CameraCollision");

        var cameraUpdateAddr = "40 55 53 57 48 8D 6C 24 A0 48 81 EC ?? ?? ?? ?? 48 8B 1D";
        _cameraUpdateHook = NativeBinding.ScanHook<CameraUpdateDelegate>(scanner, hooking, cameraUpdateAddr, CameraUpdateDetour, "攝影機更新 CameraUpdate");

        var cameraSceneUpdateAddr = "48 ?? ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? F6 81 F0 ?? ?? ?? ?? 48 8B ??";
        _cameraSceneUpdateHook = NativeBinding.ScanHook<CameraSceneUpdate>(scanner, hooking, cameraSceneUpdateAddr, CameraSceneUpdateDetour, "場景攝影機更新 CameraSceneUpdate");

        var cameraMatrixLoadAddr = NativeBinding.Scan(scanner, "E8 ?? ?? ?? ?? 48 8B 93 90 02 ?? ?? 48 8D 4C 24 40", "攝影機矩陣載入 CameraMatrixLoad");
        _cameraMatrixLoad = NativeBinding.GetDelegate<CameraMatrixLoadDelegate>(cameraMatrixLoadAddr, "攝影機矩陣載入 CameraMatrixLoad");
    }

    private nint CameraUpdateDetour(BrioCamera* camera)
    {
        var result = _cameraUpdateHook!.Original(camera);

        if(_gPoseService.IsGPosing)
        {
            if(_virtualCameraService.CurrentCamera is not null)
            {
                Vector3 currentPos = camera->Camera.CameraBase.SceneCamera.Object.Position;
                var newPos = _virtualCameraService.CurrentCamera.PositionOffset + currentPos;
                camera->Camera.CameraBase.SceneCamera.Object.Position = newPos;

                Vector3 currentLookAt = camera->Camera.CameraBase.SceneCamera.LookAtVector;
                camera->Camera.CameraBase.SceneCamera.LookAtVector = currentLookAt + (newPos - currentPos);
            }
        }

        return result;
    }

    private unsafe Matrix4x4* ProjectionDetour(IntPtr ptr, float fov, float aspect, float nearPlane, float farPlane, float a6, float a7)
    {
        if(_gPoseService.IsGPosing && _cutsceneManager.VirtualCamera.IsActiveCamera && _cutsceneManager.CameraSettings.EnableFOV)
            fov = _cutsceneManager.VirtualCamera.FoV;

        return _projectionHook!.Original(ptr, fov, aspect, nearPlane, farPlane, a6, a7);
    }

    private nint CameraSceneUpdateDetour(BrioSceneCamera* gsc)
    {
        var exec = _cameraSceneUpdateHook!.Original(gsc);

        if(_gPoseService.IsGPosing == false)
            return exec;

        // ha ha ha, this is bad

        if(InputManagerService.ActionKeysPressedLastFrame(InputAction.Interface_StopCutscene))
            _cutsceneManager.StopPlayback();
        if(InputManagerService.ActionKeysPressedLastFrame(InputAction.Interface_StartAllActorsAnimations))
            _cutsceneManager.StartAllActors();
        if(InputManagerService.ActionKeysPressedLastFrame(InputAction.Interface_StopAllActorsAnimations))
            _cutsceneManager.StopAllActors();

        if(_virtualCameraService.CurrentCamera is not null && _virtualCameraService.CurrentCamera.IsFreeCamera)
        {
            gsc->ViewMatrix = _virtualCameraService.UpdateMatrix();

            _cameraMatrixLoad?.Invoke(GetCurrentCamera()->Camera.CameraBase.SceneCamera.RenderCamera, (nint)(&gsc->ViewMatrix));
        }
        else if(_cutsceneManager.VirtualCamera.IsActiveCamera)
        {
            var camMatrix = _cutsceneManager.UpdateCamera();

            if(camMatrix is null)
                return exec;

            gsc->ViewMatrix = camMatrix.Value;

            _cameraMatrixLoad?.Invoke(GetCurrentCamera()->Camera.CameraBase.SceneCamera.RenderCamera, (nint)(&gsc->ViewMatrix));
        }

        return exec;
    }

    private nint CameraCollisionDetour(BrioCamera* camera, Vector3* a2, Vector3* a3, float a4, nint a5, float a6)
    {
        if(_gPoseService.IsGPosing)
        {
            if(_entityManager.TryGetEntity<CameraContainerEntity>("cameras", out var cameraEntity))
            {
                if(cameraEntity.TryGetCapability<CameraContainerCapability>(out var cameraCapability))
                {
                    if(cameraCapability.CurrentCamera is not null && cameraCapability.IsAllowed && cameraCapability.CurrentCamera.IsFreeCamera == false)
                        if(cameraCapability.CurrentCamera.DisableCollision && cameraCapability.CurrentCamera.IsActiveCamera)
                        {
                            camera->Collide = new Vector2(camera->Camera.MaxDistance);
                            return 0;
                        }
                }
            }
        }

        return _cameraCollisionHook!.Original(camera, a2, a3, a4, a5, a6);
    }

    public BrioCamera* GetCurrentCamera()
    {
        return (BrioCamera*)CameraManager.Instance()->GetActiveCamera();
    }

    public void Dispose()
    {
        _cameraCollisionHook?.Dispose();
        _cameraUpdateHook?.Dispose();
        _cameraSceneUpdateHook?.Dispose();
        _projectionHook?.Dispose();
    }
}
