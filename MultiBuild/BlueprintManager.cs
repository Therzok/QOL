﻿using BepInEx;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace com.brokenmass.plugin.DSP.MultiBuild
{

    public class BlueprintManager : BaseUnityPlugin
    {
        public static BlueprintData data = new BlueprintData();

        private static Dictionary<int, BuildPreview> previews = new Dictionary<int, BuildPreview>();
        private static Dictionary<int, Vector3> positions = new Dictionary<int, Vector3>();
        private static Dictionary<int, Pose> poses = new Dictionary<int, Pose>();
        private static Dictionary<int, int> objIds = new Dictionary<int, int>();

        public static void Reset()
        {
            data = new BlueprintData();

            previews.Clear();
            positions.Clear();
            poses.Clear();
            poses.Clear();
        }

        public static Vector3[] GetMovesBetweenPoints(Vector3 from, Vector3 to, Quaternion inverseFromRotation)
        {
            if (from == to)
            {
                return new Vector3[0];
            }

            int path = 0;

            var snappedPointCount = GameMain.data.mainPlayer.planetData.aux.SnapLineNonAlloc(from, to, ref path, _snaps);
            Vector3 lastSnap = from;
            Vector3[] snapMoves = new Vector3[snappedPointCount];
            for (int s = 0; s < snappedPointCount; s++)
            {
                // note: reverse rotation of the delta so that rotation works
                Vector3 snapMove = inverseFromRotation * (_snaps[s] - lastSnap);
                snapMoves[s] = snapMove;
                lastSnap = _snaps[s];
            }

            return snapMoves;
        }

        public static Vector3 GetPointFromMoves(Vector3 from, Vector3[] moves, Quaternion fromRotation)
        {
            var targetPos = from;

            // Note: rotates each move relative to the rotation of the from
            for (int i = 0; i < moves.Length; i++)
                targetPos = GameMain.data.mainPlayer.planetData.aux.Snap(targetPos + fromRotation * moves[i], true, false);

            return targetPos;
        }

        public static Queue<InserterPosition> currentPositionCache;
        public static Queue<InserterPosition> nextPositionCache;

        private static int[] _nearObjectIds = new int[4096];
        private static Vector3[] _snaps = new Vector3[1000];

        private static void SwapPositionCache()
        {
            currentPositionCache = nextPositionCache;
            nextPositionCache = new Queue<InserterPosition>();
        }

        private static InserterPosition GetPositions(InserterCopy copiedInserter, bool useCache = true)
        {
            var actionBuild = GameMain.data.mainPlayer.controller.actionBuild;
            Vector3 absoluteBuildingPos;
            Quaternion absoluteBuildingRot;

            // When using AdvancedBuildDestruct mod, all buildPreviews are positioned 'absolutely' on the planet surface.
            // In 'normal' mode the buildPreviews are relative to __instance.previewPose.
            // This means that in 'normal' mode the (only) buildPreview is always positioned at {0,0,0}

            var buildPreview = previews[copiedInserter.referenceBuildingId];

            absoluteBuildingPos = poses[copiedInserter.referenceBuildingId].position;
            absoluteBuildingRot = poses[copiedInserter.referenceBuildingId].rotation;

            InserterPosition position = null;
            /*            if (useCache && currentPositionCache.Count > 0)
                        {
                            position = currentPositionCache.Dequeue();
                        }

                        bool isCacheValid = position != null &&
                            position.copiedInserter == copiedInserter &&
                            position.absoluteBuildingPos == absoluteBuildingPos &&
                            position.absoluteBuildingRot == absoluteBuildingRot;

                        if (isCacheValid)
                        {
                            nextPositionCache.Enqueue(position);
                            return position;
                        }
            */

            var posDelta = copiedInserter.posDelta;
            var pos2Delta = copiedInserter.pos2Delta;

            Vector3 absoluteInserterPos = absoluteBuildingPos + absoluteBuildingRot * copiedInserter.posDelta;
            Vector3 absoluteInserterPos2 = absoluteBuildingPos + absoluteBuildingRot * copiedInserter.pos2Delta;

            Quaternion absoluteInserterRot = absoluteBuildingRot * copiedInserter.rot;
            Quaternion absoluteInserterRot2 = absoluteBuildingRot * copiedInserter.rot2;

            int startSlot = copiedInserter.startSlot;
            int endSlot = copiedInserter.endSlot;

            short pickOffset = copiedInserter.pickOffset;
            short insertOffset = copiedInserter.insertOffset;

            var referenceId = copiedInserter.referenceBuildingId;
            var referenceObjId = objIds[referenceId];

            var otherId = 0;
            var otherObjId = 0;

            if (previews.ContainsKey(copiedInserter.pickTarget) && previews.ContainsKey(copiedInserter.insertTarget))
            {
                // cool we copied both source and target of the inserters

                otherId = copiedInserter.pickTarget == copiedInserter.referenceBuildingId ? copiedInserter.insertTarget : copiedInserter.pickTarget;
                otherObjId = objIds[otherId];
            }
            else
            {
                // Find the other entity at the target location
                var nearcdLogic = actionBuild.nearcdLogic;
                var factory = actionBuild.factory;
                // Find the desired belt/building position
                // As delta doesn't work over distance, re-trace the Grid Snapped steps from the original
                // to find the target belt/building for this inserters other connection
                var testPos = GetPointFromMoves(absoluteBuildingPos, copiedInserter.movesFromReference, absoluteBuildingRot);

                // find building nearby
                int found = nearcdLogic.GetBuildingsInAreaNonAlloc(testPos, 0.2f, _nearObjectIds);

                // find nearest building
                float maxDistance = 0.2f;
                for (int x = 0; x < found; x++)
                {
                    var id = _nearObjectIds[x];
                    float distance;
                    ItemProto proto;
                    if (id == 0 || id == buildPreview.objId)
                    {
                        continue;
                    }
                    else if (id > 0)
                    {
                        EntityData entityData = factory.entityPool[id];
                        proto = LDB.items.Select((int)entityData.protoId);
                        distance = Vector3.Distance(entityData.pos, testPos);
                    }
                    else
                    {
                        PrebuildData prebuildData = factory.prebuildPool[-id];
                        proto = LDB.items.Select((int)prebuildData.protoId);
                        if (proto.prefabDesc.isBelt)
                        {
                            // ignore unbuilt belts
                            continue;
                        }
                        distance = Vector3.Distance(prebuildData.pos, testPos);
                    }

                    // ignore entitites that ore not (built) belts or don't have inserterPoses
                    if ((proto.prefabDesc.isBelt == copiedInserter.otherIsBelt || proto.prefabDesc.insertPoses.Length > 0) && distance < maxDistance)
                    {
                        otherId = id;
                        maxDistance = distance;
                    }
                }
            }
            if (otherObjId != 0)
            {
                if (copiedInserter.incoming)
                {
                    InserterPoses.CalculatePose(actionBuild, otherObjId, referenceObjId);
                }
                else
                {
                    InserterPoses.CalculatePose(actionBuild, referenceObjId, otherObjId);
                }

                bool hasNearbyPose = false;
                if (actionBuild.posePairs.Count > 0)
                {
                    float minDistance = 1000f;
                    PlayerAction_Build.PosePair bestFit = new PlayerAction_Build.PosePair();

                    for (int j = 0; j < actionBuild.posePairs.Count; ++j)
                    {
                        var posePair = actionBuild.posePairs[j];
                        if (
                            (copiedInserter.incoming && copiedInserter.endSlot != posePair.endSlot) ||
                            (!copiedInserter.incoming && copiedInserter.startSlot != posePair.startSlot)
                            )
                        {
                            continue;
                        }
                        float startDistance = Vector3.Distance(posePair.startPose.position, absoluteInserterPos);
                        float endDistance = Vector3.Distance(posePair.endPose.position, absoluteInserterPos2);
                        float poseDistance = startDistance + endDistance;

                        if (poseDistance < minDistance)
                        {
                            minDistance = poseDistance;
                            bestFit = posePair;
                            hasNearbyPose = true;
                        }
                    }
                    if (hasNearbyPose)
                    {
                        // if we were able to calculate a close enough sensible pose
                        // use that instead of the (visually) imprecise default

                        absoluteInserterPos = bestFit.startPose.position;
                        absoluteInserterPos2 = bestFit.endPose.position;

                        absoluteInserterRot = bestFit.startPose.rotation;
                        absoluteInserterRot2 = bestFit.endPose.rotation * Quaternion.Euler(0.0f, 180f, 0.0f);

                        pickOffset = (short)bestFit.startOffset;
                        insertOffset = (short)bestFit.endOffset;

                        startSlot = bestFit.startSlot;
                        endSlot = bestFit.endSlot;

                        posDelta = Quaternion.Inverse(absoluteBuildingRot) * (absoluteInserterPos - absoluteBuildingPos);
                        pos2Delta = Quaternion.Inverse(absoluteBuildingRot) * (absoluteInserterPos2 - absoluteBuildingPos);
                    }
                }
            }

            position = new InserterPosition()
            {
                copiedInserter = copiedInserter,
                absoluteBuildingPos = absoluteBuildingPos,
                absoluteBuildingRot = absoluteBuildingRot,

                posDelta = posDelta,
                pos2Delta = pos2Delta,
                absoluteInserterPos = absoluteInserterPos,
                absoluteInserterPos2 = absoluteInserterPos2,

                absoluteInserterRot = absoluteInserterRot,
                absoluteInserterRot2 = absoluteInserterRot2,

                pickOffset = pickOffset,
                insertOffset = insertOffset,

                startSlot = startSlot,
                endSlot = endSlot,
            };

            position.inputObjId = copiedInserter.incoming ? otherObjId : referenceObjId;
            position.inputOriginalId = copiedInserter.incoming ? otherId : referenceId;

            position.outputObjId = copiedInserter.incoming ? referenceObjId : otherObjId;
            position.outputOriginalId = copiedInserter.incoming ? referenceId : otherId;

            /*if (useCache)
            {
                nextPositionCache.Enqueue(position);
            }*/
            return position;
        }

        public static BeltCopy copyBelt(int sourceEntityId)
        {
            if (data.copiedBelts.ContainsKey(sourceEntityId))
            {
                return data.copiedBelts[sourceEntityId];
            }

            var factory = GameMain.data.localPlanet.factory;
            var planetAux = GameMain.data.mainPlayer.planetData.aux;
            var actionBuild = GameMain.data.mainPlayer.controller.actionBuild;

            var sourceEntity = factory.entityPool[sourceEntityId];

            var sourceEntityProto = LDB.items.Select(sourceEntity.protoId);

            if (!sourceEntityProto.prefabDesc.isBelt)
            {
                return null;
            }
            var belt = factory.cargoTraffic.beltPool[sourceEntity.beltId];

            var sourcePos = sourceEntity.pos;
            var sourceRot = sourceEntity.rot;

            var copiedBelt = new BeltCopy()
            {
                originalId = sourceEntityId,
                protoId = sourceEntityProto.ID,
                itemProto = sourceEntityProto,
                originalPos = sourcePos,
                originalRot = sourceRot,

                backInputId = factory.cargoTraffic.beltPool[belt.backInputId].entityId,
                leftInputId = factory.cargoTraffic.beltPool[belt.leftInputId].entityId,
                rightInputId = factory.cargoTraffic.beltPool[belt.rightInputId].entityId,
                outputId = factory.cargoTraffic.beltPool[belt.outputId].entityId,
            };

            if (data.referencePos == Vector3.zero)
            {
                data.referencePos = sourcePos;
                data.inverseReferenceRot = Quaternion.Inverse(sourceRot);
            }
            else
            {
                copiedBelt.cursorRelativePos = data.inverseReferenceRot * (copiedBelt.originalPos - data.referencePos);
                copiedBelt.movesFromReference = GetMovesBetweenPoints(data.referencePos, copiedBelt.originalPos, data.inverseReferenceRot);
            }

            data.copiedBelts.Add(copiedBelt.originalId, copiedBelt);
            return copiedBelt;
        }

        public static BuildingCopy copyAssembler(int sourceEntityId)
        {
            if (data.copiedBuildings.ContainsKey(sourceEntityId))
            {
                return data.copiedBuildings[sourceEntityId];
            }
            var factory = GameMain.data.localPlanet.factory;
            var actionBuild = GameMain.data.mainPlayer.controller.actionBuild;

            var sourceEntity = factory.entityPool[sourceEntityId];

            var sourceEntityProto = LDB.items.Select(sourceEntity.protoId);

            if (sourceEntityProto.prefabDesc.isBelt || sourceEntityProto.prefabDesc.isInserter)
            {
                return null;
            }

            var sourcePos = sourceEntity.pos;
            var sourceRot = sourceEntity.rot;

            var copiedBuilding = new BuildingCopy()
            {
                originalId = sourceEntityId,
                protoId = sourceEntityProto.ID,
                itemProto = sourceEntityProto,
                originalPos = sourcePos,
                originalRot = sourceRot,
            };

            if (!sourceEntityProto.prefabDesc.isAssembler)
            {
                copiedBuilding.recipeId = factory.factorySystem.assemblerPool[sourceEntity.assemblerId].recipeId;
            }

            if (data.referencePos == Vector3.zero)
            {
                data.referencePos = sourcePos;
                data.inverseReferenceRot = Quaternion.Inverse(sourceRot);
            }
            else
            {
                copiedBuilding.cursorRelativePos = data.inverseReferenceRot * (copiedBuilding.originalPos - data.referencePos);
                copiedBuilding.movesFromReference = GetMovesBetweenPoints(data.referencePos, copiedBuilding.originalPos, data.inverseReferenceRot);
            }

            data.copiedBuildings.Add(copiedBuilding.originalId, copiedBuilding);

            // Ignore building without inserter slots
            if (sourceEntityProto.prefabDesc.insertPoses.Length > 0)
            {
                // Find connected inserters
                var inserterPool = factory.factorySystem.inserterPool;
                var entityPool = factory.entityPool;
                var prebuildPool = factory.prebuildPool;

                for (int i = 1; i < factory.factorySystem.inserterCursor; i++)
                {
                    if (inserterPool[i].id != i) continue;

                    var inserter = inserterPool[i];
                    var inserterEntity = entityPool[inserter.entityId];

                    if (data.copiedInserters.ContainsKey(inserter.entityId)) continue;

                    var pickTarget = inserter.pickTarget;
                    var insertTarget = inserter.insertTarget;

                    if (pickTarget == sourceEntityId || insertTarget == sourceEntityId)
                    {
                        ItemProto itemProto = LDB.items.Select(inserterEntity.protoId);

                        bool incoming = insertTarget == sourceEntityId;
                        var otherId = incoming ? pickTarget : insertTarget; // The belt or other building this inserter is attached to
                        Vector3 otherPos;
                        ItemProto otherProto;

                        if (otherId > 0)
                        {
                            otherPos = entityPool[otherId].pos;
                            otherProto = LDB.items.Select((int)entityPool[otherId].protoId);
                        }
                        else
                        {
                            otherPos = prebuildPool[-otherId].pos;
                            otherProto = LDB.items.Select((int)entityPool[-otherId].protoId);
                        }

                        // Store the Grid-Snapped moves from assembler to belt/other
                        Vector3[] snapMoves = GetMovesBetweenPoints(sourcePos, otherPos, Quaternion.Inverse(sourceRot));

                        bool otherIsBelt = otherProto != null && otherProto.prefabDesc.isBelt;

                        // Cache info for this inserter
                        InserterCopy copiedInserter = new InserterCopy
                        {
                            itemProto = itemProto,
                            protoId = itemProto.ID,
                            originalId = inserter.entityId,

                            pickTarget = pickTarget,
                            insertTarget = insertTarget,

                            referenceBuildingId = copiedBuilding.originalId,

                            incoming = incoming,

                            // rotations + deltas relative to the source building's rotation
                            rot = Quaternion.Inverse(sourceRot) * inserterEntity.rot,
                            rot2 = Quaternion.Inverse(sourceRot) * inserter.rot2,
                            posDelta = Quaternion.Inverse(sourceRot) * (inserterEntity.pos - sourcePos), // Delta from copied building to inserter pos
                            pos2Delta = Quaternion.Inverse(sourceRot) * (inserter.pos2 - sourcePos), // Delta from copied building to inserter pos2

                            // store to restore inserter speed
                            refCount = Mathf.RoundToInt((float)(inserter.stt - 0.499f) / itemProto.prefabDesc.inserterSTT),

                            // not important?
                            pickOffset = inserter.pickOffset,
                            insertOffset = inserter.insertOffset,

                            // needed for pose?
                            t1 = inserter.t1,
                            t2 = inserter.t2,

                            filterId = inserter.filter,

                            movesFromReference = snapMoves,

                            startSlot = -1,
                            endSlot = -1,

                            otherIsBelt = otherIsBelt
                        };

                        // compute the start and end slot that the cached inserter uses
                        InserterPoses.CalculatePose(actionBuild, pickTarget, insertTarget);

                        if (actionBuild.posePairs.Count > 0)
                        {
                            float minDistance = 1000f;
                            for (int j = 0; j < actionBuild.posePairs.Count; ++j)
                            {
                                var posePair = actionBuild.posePairs[j];
                                float startDistance = Vector3.Distance(posePair.startPose.position, inserterEntity.pos);
                                float endDistance = Vector3.Distance(posePair.endPose.position, inserter.pos2);
                                float poseDistance = startDistance + endDistance;

                                if (poseDistance < minDistance)
                                {
                                    minDistance = poseDistance;
                                    copiedInserter.startSlot = posePair.startSlot;
                                    copiedInserter.endSlot = posePair.endSlot;
                                }
                            }
                        }

                        Debug.Log(copiedInserter.originalId);
                        data.copiedInserters.Add(copiedInserter.originalId, copiedInserter);
                    }
                }
            }

            return copiedBuilding;
        }

        public static List<BuildPreview> toBuildPreviews(Vector3 targetPos, float yaw, out List<Vector3> absolutePositions, int idMultiplier = 1)
        {
            previews.Clear();
            positions.Clear();
            objIds.Clear();
            poses.Clear();
            InserterPoses.ResetBuildPreviewsData();

            var inversePreviewRot = Quaternion.Inverse(Maths.SphericalRotation(targetPos, yaw));

            var absoluteTargetRot = Maths.SphericalRotation(targetPos, yaw);

            foreach (var building in data.copiedBuildings.Values)
            {
                var absoluteBuildingPos = GetPointFromMoves(targetPos, building.movesFromReference, absoluteTargetRot);
                var absoluteBuildingRot = Maths.SphericalRotation(absoluteBuildingPos, yaw + building.cursorRelativeYaw);

                BuildPreview bp = BuildPreview.CreateSingle(building.itemProto, building.itemProto.prefabDesc, true);
                bp.ResetInfos();
                bp.desc = building.itemProto.prefabDesc;
                bp.item = building.itemProto;
                bp.recipeId = building.recipeId;
                bp.lpos = absoluteBuildingPos;
                bp.lrot = absoluteBuildingRot;

                var pose = new Pose(absoluteBuildingPos, absoluteBuildingRot);

                var objId = InserterPoses.addOverride(pose, building.itemProto);

                positions.Add(building.originalId, absoluteBuildingPos);
                previews.Add(building.originalId, bp);
                objIds.Add(building.originalId, objId);
                poses.Add(building.originalId, pose);
            }
            var tempBeltsPreviews = new Dictionary<int, BuildPreview>(data.copiedBelts.Count);
            foreach (var belt in data.copiedBelts.Values)
            {
                var absoluteBeltPos = GetPointFromMoves(targetPos, belt.movesFromReference, absoluteTargetRot);
                var absoluteBuildingRot = Maths.SphericalRotation(absoluteBeltPos, yaw);

                BuildPreview bp = BuildPreview.CreateSingle(belt.itemProto, belt.itemProto.prefabDesc, true);
                bp.ResetInfos();
                //bp.isConnNode = true;
                bp.desc = belt.itemProto.prefabDesc;
                bp.item = belt.itemProto;

                bp.lpos = absoluteBeltPos;
                bp.lrot = absoluteBuildingRot;
                bp.outputToSlot = -1;
                bp.outputFromSlot = 0;
                bp.outputOffset = 0;
                bp.inputFromSlot = -1;
                bp.inputToSlot = 1;
                bp.inputOffset = 0;

                var pose = new Pose(absoluteBeltPos, absoluteBuildingRot);

                var objId = InserterPoses.addOverride(pose, belt.itemProto);

                positions.Add(belt.originalId, absoluteBeltPos);
                previews.Add(belt.originalId, bp);
                objIds.Add(belt.originalId, objId);
                poses.Add(belt.originalId, pose);
            }
            foreach (var belt in data.copiedBelts.Values)
            {
                var preview = previews[belt.originalId];

                //Debug.Log($"{belt.outputId} - {copiedBelts.ContainsKey(belt.outputId)}");
                if (belt.outputId != 0 && data.copiedBelts.ContainsKey(belt.outputId))
                {
                    preview.output = previews[belt.outputId];

                    if (data.copiedBelts[belt.outputId].backInputId == belt.originalId)
                    {
                        preview.outputToSlot = 1;
                    }
                    if (data.copiedBelts[belt.outputId].leftInputId == belt.originalId)
                    {
                        preview.outputToSlot = 2;
                    }
                    if (data.copiedBelts[belt.outputId].rightInputId == belt.originalId)
                    {
                        preview.outputToSlot = 3;
                    }
                }
            }

            foreach (var copiedInserter in data.copiedInserters.Values)
            {
                var positionData = GetPositions(copiedInserter);

                var bp = BuildPreview.CreateSingle(LDB.items.Select(copiedInserter.itemProto.ID), copiedInserter.itemProto.prefabDesc, true);
                bp.ResetInfos();

                var buildPreview = previews[copiedInserter.referenceBuildingId];

                bp.lrot = buildPreview.lrot * copiedInserter.rot;
                bp.lrot2 = buildPreview.lrot * copiedInserter.rot2;

                bp.lpos = buildPreview.lpos + buildPreview.lrot * positionData.posDelta;
                bp.lpos2 = buildPreview.lpos + buildPreview.lrot * positionData.pos2Delta;

                if (data.copiedBuildings.ContainsKey(positionData.inputOriginalId))
                {
                    bp.input = previews[positionData.inputOriginalId];
                }
                else
                {
                    bp.inputObjId = positionData.inputObjId;
                }

                if (data.copiedBuildings.ContainsKey(positionData.outputOriginalId))
                {
                    bp.output = previews[positionData.outputOriginalId];
                }
                else
                {
                    bp.outputObjId = positionData.outputObjId;
                }

                previews.Add(copiedInserter.originalId, bp);
            }

            absolutePositions = positions.Values.ToList();
            return previews.Values.ToList();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ConnGizmoRenderer), "Update")]
        public static void ConnGizmoRenderer_Update(ConnGizmoRenderer __instance)
        {
            foreach (var preview in BlueprintManager.previews.Values)
            {
                if (!preview.desc.isBelt)
                {
                    continue;
                }

                ConnGizmoObj item = default(ConnGizmoObj);
                item.pos = preview.lpos;
                item.rot = Quaternion.FromToRotation(Vector3.up, preview.lpos.normalized);
                item.color = 3u;
                item.size = 1f;

                if (preview.condition != EBuildCondition.Ok)
                {
                    item.color = 0u;
                }

                __instance.objs_1.Add(item);

                if (preview.output != null)
                {
                    Vector3 vector2 = preview.output.lpos - preview.lpos;
                    item.rot = Quaternion.LookRotation(vector2.normalized, preview.lpos.normalized);
                    item.size = vector2.magnitude;
                    __instance.objs_2.Add(item);
                }
            }

            __instance.cbuffer_0.SetData<ConnGizmoObj>(__instance.objs_0);
            __instance.cbuffer_1.SetData<ConnGizmoObj>(__instance.objs_1, 0, 0, (__instance.objs_1.Count >= __instance.cbuffer_1.count) ? __instance.cbuffer_1.count : __instance.objs_1.Count);
            __instance.cbuffer_2.SetData<ConnGizmoObj>(__instance.objs_2, 0, 0, (__instance.objs_2.Count >= __instance.cbuffer_2.count) ? __instance.cbuffer_2.count : __instance.objs_2.Count);
            __instance.cbuffer_3.SetData<ConnGizmoObj>(__instance.objs_3, 0, 0, (__instance.objs_3.Count >= __instance.cbuffer_3.count) ? __instance.cbuffer_3.count : __instance.objs_3.Count);
            __instance.cbuffer_4.SetData<ConnGizmoObj>(__instance.objs_4, 0, 0, (__instance.objs_4.Count >= __instance.cbuffer_4.count) ? __instance.cbuffer_4.count : __instance.objs_4.Count);
        }
    }
}