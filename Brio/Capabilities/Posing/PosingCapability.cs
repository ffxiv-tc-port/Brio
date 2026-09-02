using Brio.Capabilities.Actor;
using Brio.Config;
using Brio.Core;
using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Files;
using Brio.Game.Input;
using Brio.Game.Posing;
using Brio.Game.Posing.Skeletons;
using Brio.Resources;
using Brio.UI.Widgets.Posing;
using Brio.UI.Windows.Specialized;
using Dalamud.Plugin.Services;
using OneOf;
using OneOf.Types;
using System;
using System.Collections.Generic;

namespace Brio.Capabilities.Posing;

public class PosingCapability : ActorCharacterCapability
{
    public PosingSelectionType Selected { get; set; } = new None();
    public PosingSelectionType Hover { get; set; } = new None();
    public PosingSelectionType LastHover { get; set; } = new None();

    public SkeletonPosingCapability SkeletonPosing => Entity.GetCapability<SkeletonPosingCapability>();
    public ModelPosingCapability ModelPosing => Entity.GetCapability<ModelPosingCapability>();

    public PosingService PosingService => _posingService;

    public ConfigurationService ConfigurationService => _configurationService;

    public bool HasOverride
    {
        get
        {
            if(Entity.TryGetCapability<SkeletonPosingCapability>(out var skeletonPosing))
                if(skeletonPosing.PoseInfo.IsOverridden)
                    return true;

            if(Entity.TryGetCapability<ModelPosingCapability>(out var modelPosing))
                if(modelPosing.HasOverride)
                    return true;

            return false;
        }
    }

    public bool CanResetBone(Bone? bone) => ModelPosing.HasOverride == false || !(bone is not null && !SkeletonPosing.PoseInfo.GetPoseInfo(bone).HasStacks);

    public bool CanUndo => _undoStack.Count is not 0 and not 1 || _groupedUndoService.CanUndo;
    public bool CanRedo => _redoStack.Count > 0 || _groupedUndoService.CanRedo;
    public bool HasIKApplied => SkeletonPosing.PoseInfo.HasIKStacks;

    private Stack<PoseStack> _undoStack = [];
    private Stack<PoseStack> _redoStack = [];

    public bool OverlayOpen
    {
        get => _overlayWindow.IsOpen;
        set
        {
            _overlayWindow.IsOpen = value;
            if(value == false)
                _gameInputService.AllowEscape = true;
        }
    }

    public bool TransformWindowOpen
    {
        get => _overlayTransformWindow.IsOpen;
        set => _overlayTransformWindow.IsOpen = value;
    }

    private readonly PosingOverlayWindow _overlayWindow;
    private readonly PosingService _posingService;
    private readonly ConfigurationService _configurationService;
    private readonly PosingTransformWindow _overlayTransformWindow;
    private readonly IFramework _framework;
    private readonly GameInputService _gameInputService;
    private readonly HistoryService _groupedUndoService;
    private readonly EntityManager _entityManager;

    public PosingCapability(
        ActorEntity parent,
        PosingOverlayWindow window,
        HistoryService groupedUndoService,
        PosingService posingService,
        EntityManager entityManager,
        ConfigurationService configurationService,
        PosingTransformWindow overlayTransformWindow,
        IFramework framework,
        GameInputService gameInputService)
        : base(parent)
    {
        Widget = new PosingWidget(this);

        _overlayWindow = window;
        _posingService = posingService;
        _configurationService = configurationService;
        _overlayTransformWindow = overlayTransformWindow;
        _entityManager = entityManager;
        _framework = framework;
        _groupedUndoService = groupedUndoService;
        _gameInputService = gameInputService;
    }

    public void ClearSelection() => Selected = PosingSelectionType.None;

    public void LoadResourcesPose(string resourcesPath, bool freezeOnLoad = false, bool asBody = false)
    {
        var option = _posingService.SceneImporterOptions;
        TransformComponents? tfc = null;
        if(asBody)
        {
            option = _posingService.BodyOptions;
            tfc = TransformComponents.Rotation;
        }

        ImportPose(JsonSerializer.Deserialize<PoseFile>(ResourceProvider.Instance.GetRawResourceString(resourcesPath)), option, freezeOnLoad: freezeOnLoad, asBody: asBody, transformComponents: tfc);
    }

    public void ImportPose(string path, PoseImporterOptions? options = null)
    {
        try
        {
            if(path.EndsWith(".cmp"))
            {
                ImportPose(ResourceProvider.Instance.GetFileDocument<CMToolPoseFile>(path), options);
                return;
            }

            ImportPose(ResourceProvider.Instance.GetFileDocument<PoseFile>(path), options);
        }
        catch
        {
            Brio.NotifyError("Invalid pose file.");
        }
    }

    public void ImportPose(OneOf<PoseFile, CMToolPoseFile> rawPoseFile, PoseImporterOptions? options = null, bool asExpression = false, bool asScene = false, bool asIPCpose = false, bool asBody = false,
        bool freezeOnLoad = false, bool asProp = false, TransformComponents? transformComponents = null, bool? applyModelTransformOverride = null)
    {
        if(Actor.TryGetCapability<ActionTimelineCapability>(out var actionTimeline))
        {
            Brio.Log.Verbose($"Importing Pose... {asExpression} {asScene} {asIPCpose} {asBody} {freezeOnLoad}");

            actionTimeline.StopSpeedAndResetTimeline(() =>
            {
                ImportPose_Internal(rawPoseFile, options, reset: false, reconcile: false, asExpression: asExpression, asScene: asScene,
                    asIPCpose: asIPCpose, asBody: asBody, asProp: asProp, transformComponents: transformComponents, applyModelTransformOverride: applyModelTransformOverride);

            }, !(ConfigurationService.Instance.Configuration.Posing.FreezeActorOnPoseImport || freezeOnLoad));
        }
        else
        {
            Brio.Log.Warning($"Actor did not have ActionTimelineCapability while Importing a Pose... {asExpression} {asScene} {asIPCpose} {asBody} {freezeOnLoad}");
        }
    }

    // TODO change this boolean hell into flags after Scenes are added
    PoseFile? tempPose;
    internal void ImportPose_Internal(OneOf<PoseFile, CMToolPoseFile> rawPoseFile, PoseImporterOptions? options = null, bool generateSnapshot = true, bool reset = true, bool reconcile = true,
        bool asExpression = false, bool expressionPhase2 = false, bool asScene = false, bool asIPCpose = false, bool asBody = false, bool asProp = false,
        TransformComponents? transformComponents = null, bool? applyModelTransformOverride = null)
    {
        // 🔴 本函式的三個入口全部是「延後好幾幀之後」才跑的:
        //    ① ImportPose 把它包成 postStopAction 交給 ActionTimelineCapability.StopSpeedAndResetTimeline,
        //       那支要等 await RunOnTick(delayTicks: 4) 回來才 Invoke ⇒ 一定跨 4 幀以上。
        //    ② Snapshot 的表情路徑(本身由 delayTicks: 4 的回呼叫進來)。
        //    ③ Reconcile 的 delayTicks: 2 回呼。
        //    而 ModelPosing.ImportModelPose 會讀寫 Transform → ModelTransformService.GetTransform /
        //    SetTransform(GameObject) → go.Native() 解參與寫入;asExpression 路徑的 GeneratePoseFile
        //    也會讀 Transform。GameObject.Address 是建構當下凍結的,角色在那幾幀之內消失就是懸空位址,
        //    AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
        //    IsGameObjectAlive 只讀物件表自己的指標陣列(GetObjectAddress),不解參任何存下來的位址。
        //    角色還活著時與原本逐字相同;不在了就整批不做,而不是崩潰。
        if(Actor.IsGameObjectAlive == false)
            return;

        var poseFile = rawPoseFile.Match(
                poseFile => poseFile,
                cmToolPoseFile => cmToolPoseFile.Upgrade()
            );

        if(poseFile.Bones.Count == 0 && poseFile.MainHand.Count == 0 && poseFile.OffHand.Count == 0)
        {
            Brio.NotifyError("Invalid pose file.");
            Brio.Log.Info($"Invalid pose file. {reconcile} {reset} {generateSnapshot} {asExpression} {expressionPhase2} {asScene} {asIPCpose} {asBody}");
            return;
        }

        poseFile.SanitizeBoneNames();

        bool applyModelTransform = false;
        if(asExpression)
        {
            Brio.Log.Debug("Loading as Expression");

            options = _posingService.ExpressionOptions;
            tempPose = GeneratePoseFile();
        }
        else if(asBody)
        {
            options = _posingService.BodyOptions;
        }
        else if(asScene)
        {
            options = _posingService.SceneImporterOptions;

            applyModelTransform |= ConfigurationService.Instance.Configuration.Import.ApplyModelTransform;
        }
        else if(asIPCpose)
        {
            options = _posingService.DefaultIPCImporterOptions;
        }
        else
        {
            options ??= _posingService.DefaultImporterOptions;
        }

        if(asScene == false)
        {
            applyModelTransform |= options.ApplyModelTransform;

            if(transformComponents.HasValue)
            {
                options.TransformComponents = transformComponents.Value;
            }

            if(applyModelTransformOverride.HasValue)
            {
                applyModelTransform = applyModelTransformOverride.Value;
            }
        }

        if(applyModelTransform && reset)
            ModelPosing.ResetTransform();

        SkeletonPosing.ImportSkeletonPose(poseFile, options, expressionPhase2);

        if(asExpression == false)
            ModelPosing.ImportModelPose(poseFile, options, asScene, applyModelTransform);
       
        if(expressionPhase2)
        {
            var bone = SkeletonPosing.GetBone("j_kao", PoseInfoSlot.Character);
            if(bone != null)
            {
                var poseInfo = SkeletonPosing.PoseInfo.GetPoseInfo(bone);
                if(poseInfo.HasStacks)
                    poseInfo.RemoveLastStack();
            }
        }

        if(generateSnapshot)
            _framework.RunOnTick(() =>
            {
                // 🔴 這是 4 幀之後才跑的。Snapshot 會讀 ModelPosing.OriginalTransform 與 ModelPosing.Transform,
                //    兩者都會走 ModelTransformService.GetTransform(GameObject) → go.Native() 解參,
                //    而 GameObject.Address 是建構當下凍結的 ⇒ 角色在這 4 幀之內消失就是懸空讀取,
                //    AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
                //    ⚠️ 閘門只加在這個「延後」的呼叫點:十幾個同步呼叫 Snapshot 的 UI 位置完全不受影響,
                //    場景載入的成功路徑也與原本逐字相同(那時角色必定還在物件表裡)。
                if(Actor.IsGameObjectAlive == false)
                    return;

                Snapshot(reset, reconcile, asExpression: asExpression);
            }, delayTicks: 4);
    }

    public PoseFile ExportPose()
    {
        return GeneratePoseFile();
    }
    public void ExportSavePose(string path)
    {
        var poseFile = ExportPose();
        ResourceProvider.Instance.SaveFileDocument(path, poseFile);
    }

    public void Snapshot(bool reset = true, bool reconcile = true, bool asExpression = false)
    {
        var undoStackSize = _configurationService.Configuration.Posing.UndoStackSize;
        if(undoStackSize <= 0)
        {
            _undoStack.Clear();
            _redoStack.Clear();
            return;
        }

        _redoStack.Clear();

        if(asExpression == true)
        {
            ImportPose_Internal(tempPose!, new PoseImporterOptions(new BoneFilter(_posingService), TransformComponents.All, false),
            generateSnapshot: true, expressionPhase2: true);

            return;
        }

        if(_undoStack.Count == 0)
            _undoStack.Push(new PoseStack(new PoseInfo(), ModelPosing.OriginalTransform));

        _undoStack.Push(new PoseStack(SkeletonPosing.PoseInfo.Clone(), ModelPosing.Transform));
        _undoStack = _undoStack.Trim(undoStackSize + 1);

        //var bone = SkeletonPosing.GetBone("j_kao", PoseInfoSlot.Character);
        //if(bone != null)
        //{
        //    var face = SkeletonPosing.PoseInfo.GetPoseInfo(bone);
        //    var parent = face.Parent;
        //    if(parent.IsOverridden)
        //    {
        //        face.Apply(bone.LastTransform, bone.LastRawTransform, TransformComponents.All, TransformComponents.Rotation, BoneIKInfo.Disabled, PoseMirrorMode.None, true);
        //        face.ClearStacks();
        //        Reconcile(false);
        //    }
        //}

        if(reconcile)
            Reconcile(reset);
    }

    public void Redo()
    {
        if(_entityManager.SelectedEntityIds.Count > 1)
        {
            _groupedUndoService.Redo();
            return;
        }

        if(_redoStack.TryPop(out var redoStack))
        {
            _undoStack.Push(redoStack);
            SkeletonPosing.PoseInfo = redoStack.Info.Clone();
            ModelPosing.Transform = redoStack.ModelTransform;
        }
    }

    public void Undo()
    {
        if(_entityManager.SelectedEntityIds.Count > 1)
        {
            _groupedUndoService.Undo();
            return;
        }

        if(_undoStack.TryPop(out var undoStack))
            _redoStack.Push(undoStack);

        if(_undoStack.TryPeek(out var applicable))
        {
            SkeletonPosing.PoseInfo = applicable.Info.Clone();
            ModelPosing.Transform = applicable.ModelTransform;
        }
    }

    public void Reset(bool generateSnapshot = true, bool reset = true, bool clearHistStack = true)
    {
        if(Actor.IsProp == false)
            SkeletonPosing.ResetPose();
        ModelPosing.ResetTransform();

        if(clearHistStack)
            _redoStack.Clear();

        if(generateSnapshot)
            Snapshot(reset);
    }

    private void Reconcile(bool reset = true, bool generateSnapshot = true)
    {
        _framework.RunOnTick(() =>
        {
            // 🔴 這是 2 幀之後才跑的,而且三個動作都會解參 GameObject:
            //    GeneratePoseFile → ModelPosing.ExportModelPose → Transform(getter)
            //    Reset → ModelPosing.ResetTransform → ModelTransformService.SetTransform(GameObject, ...)
            //    ImportPose_Internal → ModelPosing.ImportModelPose → Transform(getter/setter)
            //    GameObject.Address 是建構當下凍結的,角色在這 2 幀之內消失就是懸空位址,
            //    AccessViolationException 在 .NET Core 是 corrupted-state exception,try/catch 攔不到。
            //    IsGameObjectAlive 只讀物件表自己的指標陣列(GetObjectAddress),不解參任何存下來的位址。
            if(Actor.IsGameObjectAlive == false)
                return;

            var all = new PoseImporterOptions(new BoneFilter(_posingService), TransformComponents.All, true);
            var poseFile = GeneratePoseFile();
            if(reset)
            {
                Reset(generateSnapshot, false);
            }
            ImportPose_Internal(poseFile, options: all, generateSnapshot: false);
        }, delayTicks: 2);
    }

    public PoseFile GeneratePoseFile()
    {
        var poseFile = new PoseFile();
        SkeletonPosing.ExportSkeletonPose(poseFile);
        ModelPosing.ExportModelPose(poseFile);
        return poseFile;
    }

    public BonePoseInfoId? IsSelectedBone()
    {
        Bone? realBone = null;
        return Selected.Match<BonePoseInfoId?>(
            bone =>
            {
                realBone = SkeletonPosing.GetBone(bone);
                if(realBone != null && realBone.Skeleton.IsValid)
                    return bone;
                return null;
            },
            _ => null,
            _ => null
        );
    }

    public static void FlipBone(Bone bone, BonePoseInfo poseInfo)
    {
        var newBoneTransform = bone.LastTransform;

        // Convert to Euler (like the Gizmo)
        var boneRotationEuler = bone.LastTransform.Rotation.ToEuler();
        boneRotationEuler.X = 180 - boneRotationEuler.X;
        boneRotationEuler.Y = -boneRotationEuler.Y;
        var newBoneRotation = boneRotationEuler.ToQuaternion();

        newBoneTransform.Rotation = newBoneRotation;

        poseInfo.Apply(newBoneTransform, bone.LastRawTransform, TransformComponents.All, TransformComponents.All, poseInfo.DefaultIK, poseInfo.MirrorMode, true);
    }

    public void FlipBoneModel()
    {
        BonePoseInfoId? selectedIsBone = IsSelectedBone();
        // Bone Flip
        if(selectedIsBone.HasValue)
        {
            // Get current bone rotation data
            var bone = SkeletonPosing.GetBone(selectedIsBone.Value);
            if(bone != null)
            {
                var poseInfo = SkeletonPosing.PoseInfo.GetPoseInfo(bone);
                FlipBone(bone, poseInfo);

                // record change for undo
                Snapshot(reset: false);
            }
        }
        else
        {
            // Model Flip (TODO: Implement)
        }
    }

    public void ResetSelectedBone()
    {
        BonePoseInfoId? selectedIsBone = IsSelectedBone();
        if(selectedIsBone.HasValue)
        {
            ResetBoneStacks(selectedIsBone);
        }
        else if(ModelPosing.HasOverride)
        {
            ResetTransform();
        }
    }

    public void ResetBoneStacks(BonePoseInfoId? boneid)
    {
        if(boneid == null)
            return;

        var bone = SkeletonPosing.GetBone(boneid.Value);
        if(bone != null)
        {
            var poseInfo = SkeletonPosing.PoseInfo.GetPoseInfo(bone);
            if(poseInfo.HasStacks)
            {
                poseInfo.ClearStacks();
                Snapshot(reset: false);
            }
        }
    }

    public void ResetTransform()
    {
        ModelPosing.ResetTransform();
        Snapshot(reset: false);
    }

    public record struct PoseStack(PoseInfo Info, Transform ModelTransform);
}

public enum ExpressionPhase
{
    None, One, Two, Three
}
