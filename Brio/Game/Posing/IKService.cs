using Brio.Core;
using Brio.Game.Posing.Skeletons;
using Dalamud.Game;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;
using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Brio.Game.Posing;
public unsafe class IKService : IDisposable
{
    delegate* unmanaged<hkaCCDSolver*, int, float, void> _ccdSolverCtr;
    delegate* unmanaged<hkaCCDSolver*, byte*, hkArray<CCDIKConstraint>*, hkaPose*, byte*> _ccdSolverSolve;
    delegate* unmanaged<byte*, TwoJointIKSetup*, hkaPose*, byte*> _twoJointSolverSolve;

    private (nint Aligned, nint Unaligned) _solverAddr;
    private (nint Aligned, nint Unaligned) _ccdConstraintCtrAddr;
    private (nint Aligned, nint Unaligned) _twoJointSetupAddr;

    private static bool _tcWarningLogged;

    /// <summary>
    /// UI tooltip 與 log 共用的說明(繁中)。
    /// 三條特徵碼與三個結構大小都已對台服 7.20 客戶端離線驗證過(2026-09-06),
    /// 所以這裡不再宣稱「尚未驗證」—— 那句話已經是假的,留著只會白白嚇使用者。
    /// </summary>
    public const string TcWarning =
        "實驗功能:IK 解算是直接呼叫遊戲內建的 havok 解算器。\n" +
        "三條特徵碼在台服客戶端各自唯一命中,三個結構大小也已逐欄反組譯確認:\n" +
        "hkaCCDSolver 0x18、CCDIKConstraint 0x20(陣列步進 0x20)、TwoJointIKSetup 0x82。\n" +
        "仍屬實驗功能:若遊戲出現異常,請關閉 IK 並回報。";

    /// <summary>第一次啟用 IK 時印一次(Information 級,使用者的 LogLevel 1 收得到)。</summary>
    public static void LogTcWarningOnce()
    {
        if(_tcWarningLogged)
            return;
        _tcWarningLogged = true;

        Brio.Log.Information("[TC] Brio IK —— " + TcWarning.Replace("\n", " "));
    }

    public IKService(ISigScanner scanner)
    {
        _ccdSolverCtr = (delegate* unmanaged<hkaCCDSolver*, int, float, void>)NativeBinding.Scan(scanner, "E8 ?? ?? ?? ?? 48 8D 43 ?? 48 C7 43", "IK:CCD 解算器建構");
        _ccdSolverSolve = (delegate* unmanaged<hkaCCDSolver*, byte*, hkArray<CCDIKConstraint>*, hkaPose*, byte*>)NativeBinding.Scan(scanner, "E8 ?? ?? ?? ?? 8B 44 24 ?? 48 8B 5C 24 ?? 48 3B 5C 24", "IK:CCD 解算");
        _twoJointSolverSolve = (delegate* unmanaged<byte*, TwoJointIKSetup*, hkaPose*, byte*>)NativeBinding.Scan(scanner, "E8 ?? ?? ?? ?? 0F 28 55 ?? 41 0F 28 D8", "IK:雙關節解算");

        _solverAddr = NativeHelpers.AllocateAlignedMemory(sizeof(hkaCCDSolver), 16);
        _ccdConstraintCtrAddr = NativeHelpers.AllocateAlignedMemory(sizeof(CCDIKConstraint), 16);
        _twoJointSetupAddr = NativeHelpers.AllocateAlignedMemory(sizeof(TwoJointIKSetup), 16);

        TwoJointIKSetup* setup = (TwoJointIKSetup*)_twoJointSetupAddr.Aligned;
        *setup = new TwoJointIKSetup();
    }

    /// <summary>三個原生解算器都繫結成功時才可用。任一失效就整個 IK 停用(呼叫 null 函式指標是 AVE)。</summary>
    public bool IsAvailable => _ccdSolverCtr != null && _ccdSolverSolve != null && _twoJointSolverSolve != null;

    public void SolveIK(hkaPose* pose, BoneIKInfo ikInfo, Bone bone, Vector3 target)
    {
        if(IsAvailable == false)
            return;

        ikInfo.SolverOptions.Switch(
            ccd =>
            {
                var boneList = bone.GetBonesToDepth(ccd.Depth, true);
                if(boneList.Count <= 1)
                    return;

                var startBone = (short)boneList.Last().Index;
                var endBone = (short)boneList.First().Index;

                hkaCCDSolver* ccdSolver = (hkaCCDSolver*)_solverAddr.Aligned;
                _ccdSolverCtr(ccdSolver, ccd.Iterations, 1f);

                CCDIKConstraint* constraint = (CCDIKConstraint*)_ccdConstraintCtrAddr.Aligned;
                constraint->StartBone = startBone;
                constraint->EndBone = endBone;
                constraint->Target.X = target.X;
                constraint->Target.Y = target.Y;
                constraint->Target.Z = target.Z;

                var constraints = new hkArray<CCDIKConstraint>
                {
                    Length = 1,
                    CapacityAndFlags = 1,
                    Data = constraint
                };

                byte notSure = 0;
                _ccdSolverSolve(ccdSolver, &notSure, &constraints, pose);
            },
            twoJoint =>
            {
                var boneList = bone.GetBonesToDepth(twoJoint.FirstBone, true);

                // 🔴 三個索引都要判,而且是 <= 不是 <。
                // GetBonesToDepth(n) 最多回 n + 1 個,但碰到隱藏的父骨、部分骨骼邊界或根骨會提早停,
                // 所以 Count == FirstBone 是真的會發生的 —— 而下面直接拿 FirstBone 去索引。
                // 失敗形式是 ArgumentOutOfRangeException(不是崩潰),表現成那一幀整個姿勢套不上去。
                if(twoJoint.FirstBone < 0 || twoJoint.SecondBone < 0 || twoJoint.EndBone < 0)
                    return;

                if(boneList.Count <= twoJoint.FirstBone
                    || boneList.Count <= twoJoint.SecondBone
                    || boneList.Count <= twoJoint.EndBone)
                    return;

                TwoJointIKSetup* setup = (TwoJointIKSetup*)_twoJointSetupAddr.Aligned;
                setup->FirstJointIdx = (short)boneList[twoJoint.FirstBone].Index;
                setup->SecondJointIdx = (short)boneList[twoJoint.SecondBone].Index;
                setup->EndBoneIdx = (short)boneList[twoJoint.EndBone].Index;
                setup->EndTargetMS = new Vector4(target, 0);
                setup->HingeAxisLS = new Vector4(twoJoint.RotationAxis, 0);

                byte notSure = 0;
                _twoJointSolverSolve(&notSure, setup, pose);
            }
        );
    }

    public void Dispose()
    {
        NativeHelpers.FreeAlignedMemory(_solverAddr);
        NativeHelpers.FreeAlignedMemory(_ccdConstraintCtrAddr);
        // 建構子配了三塊,上游只釋放兩塊 —— 少釋放這一塊的話,每次載入/卸載外掛就漏掉
        // 一份 TwoJointIKSetup 的配置(含對齊補償),而且是永遠拿不回來的原生記憶體。
        NativeHelpers.FreeAlignedMemory(_twoJointSetupAddr);
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    private struct hkaCCDSolver
    {
        [FieldOffset(0x0)] public nint vtbl;
        [FieldOffset(0x10)] public uint Iterations;
        [FieldOffset(0x14)] public float Gain;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x2)]
    private struct CCDIKConstraint
    {
        [FieldOffset(0x0)] public short StartBone;
        [FieldOffset(0x2)] public short EndBone;
        [FieldOffset(0x10)] public hkVector4f Target;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x82)]
    private struct TwoJointIKSetup
    {
        [FieldOffset(0x00)] public short FirstJointIdx = -1;
        [FieldOffset(0x02)] public short SecondJointIdx = -1;
        [FieldOffset(0x04)] public short EndBoneIdx = -1;
        [FieldOffset(0x06)] public short FirstJointTwistIdx = -1;
        [FieldOffset(0x08)] public short SecondJointTwistIdx = -1;
        [FieldOffset(0x10)] public Vector4 HingeAxisLS = Vector4.Zero;
        [FieldOffset(0x20)] public float CosineMaxHingeAngle = -1f;
        [FieldOffset(0x24)] public float CosineMinHingeAngle = 1f;
        [FieldOffset(0x28)] public float FirstJointIkGain = 1f;
        [FieldOffset(0x2C)] public float SecondJointIkGain = 1f;
        [FieldOffset(0x30)] public float EndJointIkGain = 1f;
        [FieldOffset(0x40)] public Vector4 EndTargetMS = Vector4.Zero;
        [FieldOffset(0x50)] public Quaternion EndTargetRotationMS = Quaternion.Identity;
        [FieldOffset(0x60)] public Vector4 EndBoneOffsetLS = Vector4.Zero;
        [FieldOffset(0x70)] public Quaternion EndBoneRotationOffsetLS = Quaternion.Identity;
        [FieldOffset(0x80)] public bool EnforceEndPosition = true;
        [FieldOffset(0x81)] public bool EnforceEndRotation = false;

        public TwoJointIKSetup() { }
    }

}
