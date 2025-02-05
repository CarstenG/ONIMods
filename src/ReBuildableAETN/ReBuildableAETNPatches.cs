﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Database;
using TUNING;
using UnityEngine;
using HarmonyLib;
using SanchozzONIMods.Lib;
using PeterHan.PLib.Core;
using PeterHan.PLib.Detours;
using PeterHan.PLib.Options;
using PeterHan.PLib.PatchManager;
using static ReBuildableAETN.MassiveHeatSinkCoreConfig;

namespace ReBuildableAETN
{
    public sealed class ReBuildableAETNPatches : KMod.UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            ReBuildableAETNOptions.Reload();
            base.OnLoad(harmony);
            PUtil.InitLibrary();
            new PPatchManager(harmony).RegisterPatchClass(GetType());
            if (DlcManager.IsExpansion1Active())
                new POptions().RegisterOptions(this, typeof(ReBuildableAETNSpaceOutOptions));
            else
                new POptions().RegisterOptions(this, typeof(ReBuildableAETNVanillaOptions));
        }

        [PLibMethod(RunAt.BeforeDbInit)]
        private static void Localize()
        {
            Utils.InitLocalization(typeof(STRINGS));
        }

        [PLibMethod(RunAt.AfterDbInit)]
        private static void AfterDbInit()
        {
            Utils.AddBuildingToPlanScreen("Utilities", MassiveHeatSinkConfig.ID);
            Utils.AddBuildingToTechnology("Catalytics", MassiveHeatSinkConfig.ID);
            GameTags.MaterialBuildingElements.Add(ID);
            // добавляем ядра -выдры- в космос в ванилле
            var chance = ReBuildableAETNOptions.Instance.VanillaPlanetChance;
            if (DlcManager.IsPureVanilla() && chance.Enabled)
            {
                var sdp = Db.Get().SpaceDestinationTypes;
                CloneArtifactDropRateTable(sdp.IcyDwarf, TIER_CORE, chance.IcyDwarfChance / 100f);
                CloneArtifactDropRateTable(sdp.IceGiant, TIER_CORE, chance.IceGiantChance / 100f);
            }
        }

        private static void CloneArtifactDropRateTable(SpaceDestinationType destination, ArtifactTier tier, float weight_percent)
        {
            var result = new ArtifactDropRate();
            float weight = destination.artifactDropTable.totalWeight * weight_percent;
            foreach (var rate in destination.artifactDropTable.rates)
            {
                if (rate.first == DECOR.SPACEARTIFACT.TIER_NONE)
                    result.AddItem(rate.first, rate.second - weight);
                else
                    result.AddItem(rate.first, rate.second);
            }
            result.AddItem(tier, weight);
            destination.artifactDropTable = result;
        }

        // добавляем ядра для постройки аэтна
        [HarmonyPatch(typeof(MassiveHeatSinkConfig), nameof(MassiveHeatSinkConfig.CreateBuildingDef))]
        internal static class MassiveHeatSinkConfig_CreateBuildingDef
        {
            private static void Postfix(ref BuildingDef __result)
            {
                __result.ViewMode = OverlayModes.GasConduits.ID;
                __result.MaterialCategory = MATERIALS.REFINED_METALS.AddItem(ID).ToArray();
                __result.Mass = __result.Mass.AddItem(2).ToArray();
                if (ReBuildableAETNOptions.Instance.AddLogicPort)
                    __result.LogicInputPorts = LogicOperationalController.CreateSingleInputPortList(new CellOffset(1, 0));
            }
        }

        [HarmonyPatch(typeof(MassiveHeatSinkConfig), nameof(MassiveHeatSinkConfig.DoPostConfigureComplete))]
        internal static class MassiveHeatSinkConfig_DoPostConfigureComplete
        {
            private static void Postfix(GameObject go)
            {
                // требование навыка для постройки
                var constructable = go.GetComponent<Building>().Def.BuildingUnderConstruction.GetComponent<Constructable>();
                constructable.requiredSkillPerk = Db.Get().SkillPerks.CanDemolish.Id;
                // требование навыка для разрушения
                var deconstructable = go.GetComponent<Deconstructable>();
                deconstructable.requiredSkillPerk = Db.Get().SkillPerks.CanDemolish.Id;
                deconstructable.allowDeconstruction = false;
                go.AddOrGet<MassiveHeatSinkRebuildable>();
                if (ReBuildableAETNOptions.Instance.AddLogicPort)
                    go.AddOrGet<LogicOperationalController>();
                go.UpdateComponentRequirement<Demolishable>(false);
            }
        }

        // скрываем требование навыка пока разрушение не назначено
        [HarmonyPatch(typeof(Deconstructable), "OnSpawn")]
        internal static class Deconstructable_OnSpawn
        {
            private static void Prefix(ref bool ___shouldShowSkillPerkStatusItem)
            {
                ___shouldShowSkillPerkStatusItem = false;
            }
        }

        [HarmonyPatch]
        internal static class Deconstructable_Queue_Cancel_Deconstruction
        {
            private static readonly DetouredMethod<Action<Workable, object>> UpdateStatusItem =
                typeof(Workable).DetourLazy<Action<Workable, object>>("UpdateStatusItem");

            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return typeof(Deconstructable).GetMethodSafe("QueueDeconstruction", false, PPatchTools.AnyArguments);
                yield return typeof(Deconstructable).GetMethodSafe("CancelDeconstruction", false, PPatchTools.AnyArguments);
            }

            private static void Postfix(Deconstructable __instance, ref bool ___shouldShowSkillPerkStatusItem, bool ___isMarkedForDeconstruction)
            {
                ___shouldShowSkillPerkStatusItem = ___isMarkedForDeconstruction;
                UpdateStatusItem.Invoke(__instance, null);
            }
        }

        // на длц - чтобы нельзя было строить аетн из замурованной версии едра
        // придется влезть во все постройки и запретить тэг замурованного артифакта
        [HarmonyPatch(typeof(Constructable), "OnSpawn")]
        internal static class Constructable_OnSpawn
        {
            private static bool Prepare()
            {
                return DlcManager.IsExpansion1Active();
            }

            private static void InjectForbiddenTag(FetchList2 fetchList, Tag tag, Tag[] required_tags, Tag[] forbidden_tags, float amount, FetchOrder2.OperationalRequirement operationalRequirement)
            {
                forbidden_tags = (forbidden_tags ?? new Tag[0]).AddItem(GameTags.CharmedArtifact).ToArray();
                fetchList.Add(tag, required_tags, forbidden_tags, amount, operationalRequirement);
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase method)
            {
                string methodName = method.DeclaringType.FullName + "." + method.Name;
                var fetchList_Add = typeof(FetchList2).GetMethodSafe(nameof(FetchList2.Add), false, typeof(Tag), typeof(Tag[]), typeof(Tag[]), typeof(float), typeof(FetchOrder2.OperationalRequirement));
                var injectForbiddenTag = typeof(Constructable_OnSpawn).GetMethodSafe(nameof(InjectForbiddenTag), true, PPatchTools.AnyArguments);

                if (fetchList_Add != null && injectForbiddenTag != null)
                {
                    instructions = PPatchTools.ReplaceMethodCall(instructions, fetchList_Add, injectForbiddenTag);
#if DEBUG
                        PUtil.LogDebug($"'{methodName}' Transpiler injected");
#endif
                }
                else
                {
                    PUtil.LogWarning($"Could not apply Transpiler to the '{methodName}'");
                }
                return instructions;
            }
        }

        // добавляем ядра в посылку
        [HarmonyPatch(typeof(Immigration), "ConfigureCarePackages")]
        internal static class Immigration_ConfigureCarePackages
        {
            private static bool Prepare()
            {
                return ReBuildableAETNOptions.Instance.CarePackage.Enabled;
            }

            private static bool Condition(Tag tag)
            {
                return (GameClock.Instance.GetCycle() >= ReBuildableAETNOptions.Instance.CarePackage.MinCycle)
                    && (!ReBuildableAETNOptions.Instance.CarePackage.RequireDiscovered
                        || DiscoveredResources.Instance.IsDiscovered(tag));
            }

            private static void Postfix(ref CarePackageInfo[] ___carePackages)
            {
                var core = new CarePackageInfo(ID, 1, () => Condition(ID));
                ___carePackages = ___carePackages.AddItem(core).ToArray();
            }
        }

        // добавляем ядра во всякий хлам:
        // стол директора, добавляем возможность обыскать
        // сетлокер и сырая воркабле должны быть добавлены в гамеобъект раньше любой другой воркабле, 
        // иначе хрень получается, поэтому транспилером
        [HarmonyPatch(typeof(PropFacilityDeskConfig), nameof(PropFacilityDeskConfig.CreatePrefab))]
        internal static class PropFacilityDeskConfig_CreatePrefab
        {
            private static Demolishable InjectSetLocker(GameObject go)
            {
                var workable = go.AddOrGet<Workable>();
                workable.synchronizeAnims = false;
                workable.resetProgressOnStop = true;
                var setLocker = go.AddOrGet<SetLocker>();
                setLocker.machineSound = "VendingMachine_LP";
                setLocker.overrideAnim = "anim_break_kanim";
                setLocker.dropOffset = new Vector2I(1, 1);
                go.AddOrGet<LoopingSounds>();
                go.AddOrGet<MassiveHeatSinkCoreSpawner>().chance =
                    ReBuildableAETNOptions.Instance.GravitasPOIChance.RarePOIChance / 100f;
                return go.AddOrGet<Demolishable>();
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase method)
            {
                string methodName = method.DeclaringType.FullName + "." + method.Name;
                var AddOrGetDemolishable = typeof(EntityTemplateExtensions).GetMethodSafe(nameof(EntityTemplateExtensions.AddOrGet), true, typeof(GameObject))?.MakeGenericMethod(typeof(Demolishable));
                var injectSetLocker = typeof(PropFacilityDeskConfig_CreatePrefab).GetMethodSafe(nameof(InjectSetLocker), true, PPatchTools.AnyArguments);

                if (AddOrGetDemolishable != null && injectSetLocker != null)
                {
                    instructions = PPatchTools.ReplaceMethodCall(instructions, AddOrGetDemolishable, injectSetLocker);
#if DEBUG
                        PUtil.LogDebug($"'{methodName}' Transpiler injected");
#endif
                }
                else
                {
                    PUtil.LogWarning($"Could not apply Transpiler to the '{methodName}'");
                }
                return instructions;
            }
        }

        [HarmonyPatch(typeof(PropFacilityDeskConfig), nameof(PropFacilityDeskConfig.OnPrefabInit))]
        internal static class PropFacilityDeskConfig_OnPrefabInit
        {
            private static void Postfix(GameObject inst)
            {
                IEnumerable<string> x = new string[] { FieldRationConfig.ID };
                for (int i = 0; i < 25; i++)
                {
                    x = x.AddItem(ResearchDatabankConfig.ID);
                }
                var component = inst.GetComponent<SetLocker>();
                component.possible_contents_ids = new string[][] { x.ToArray() };
                component.ChooseContents();
            }
        }

        // спутник
        [HarmonyPatch(typeof(PropSurfaceSatellite3Config), nameof(PropSurfaceSatellite3Config.CreatePrefab))]
        internal static class PropSurfaceSatellite3Config_CreatePrefab
        {
            private static void Postfix(GameObject __result)
            {
                __result.AddOrGet<MassiveHeatSinkCoreSpawner>().chance =
                    ReBuildableAETNOptions.Instance.GravitasPOIChance.RarePOIChance / 100f;
            }
        }

        // шкафчик и торг-о-мат
        [HarmonyPatch]
        internal static class SetLockerConfig_VendingMachineConfig_CreatePrefab
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return typeof(SetLockerConfig).GetMethodSafe(nameof(SetLockerConfig.CreatePrefab), false, PPatchTools.AnyArguments);
                yield return typeof(VendingMachineConfig).GetMethodSafe(nameof(VendingMachineConfig.CreatePrefab), false, PPatchTools.AnyArguments);
            }

            private static void Postfix(GameObject __result)
            {
                __result.AddOrGet<MassiveHeatSinkCoreSpawner>().chance =
                    ReBuildableAETNOptions.Instance.GravitasPOIChance.LockerPOIChance / 100f;
            }
        }

        // добавляем ядра в космос в длц:
        // нужно избежать записи id ядра в сейф, иначе при отключении мода будет плохо

        // выборы следующего для сбора артифакта в космических пои.
        // пусть будет условное значение "" означает ядро (некрасиво, но что поделать)
        [HarmonyPatch(typeof(ArtifactPOIStates.Instance), nameof(ArtifactPOIStates.Instance.PickNewArtifactToHarvest))]
        internal static class ArtifactPOIStates_Instance_PickNewArtifactToHarvest
        {
            private static bool Prepare()
            {
                return DlcManager.IsExpansion1Active() && ReBuildableAETNOptions.Instance.SpaceOutPOIChance.Enabled;
            }

            private static bool Prefix(ArtifactPOIStates.Instance __instance, int ___numHarvests)
            {
                // пропускаем пои с явно прописанным стартовым артифактом
                if (___numHarvests <= 0 && !string.IsNullOrEmpty(__instance.configuration.GetArtifactID()))
                {
                    return true;
                }
                if (UnityEngine.Random.Range(0f, 100f) < ReBuildableAETNOptions.Instance.SpaceOutPOIChance.SpacePOIChance)
                {
                    __instance.artifactToHarvest = string.Empty;
                    return false;
                }
                return true;
            }
#if DEBUG
            private static void Postfix(ArtifactPOIStates.Instance __instance)
            {
                Debug.Log("PickNewArtifactToHarvest: " + __instance.artifactToHarvest);
            }
#endif
        }

        // передача ранее выбранного артифакта
        [HarmonyPatch(typeof(ArtifactPOIStates.Instance), nameof(ArtifactPOIStates.Instance.GetArtifactToHarvest))]
        internal static class ArtifactPOIStates_Instance_GetArtifactToHarvest
        {
            private static bool Prepare()
            {
                return DlcManager.IsExpansion1Active() && ReBuildableAETNOptions.Instance.SpaceOutPOIChance.Enabled;
            }

            private static bool Prefix(ArtifactPOIStates.Instance __instance, ref string __result)
            {
                if (string.IsNullOrEmpty(__instance.artifactToHarvest))
                {
                    __result = ID;
                    return false;
                }
                return true;
            }
#if DEBUG
            private static void Postfix(string __result)
            {
                Debug.Log("GetArtifactToHarvest: " + __result);
            }
#endif
        }

        // запись о проанализированном артифакте на станции анализа
        [HarmonyPatch(typeof(ArtifactSelector), nameof(ArtifactSelector.RecordArtifactAnalyzed))]
        internal static class ArtifactSelector_RecordArtifactAnalyzed
        {
            private static bool Prefix(string id, ref bool __result)
            {
                if (id == ID)
                {
                    __result = false;
                    return false;
                }
                return true;
            }
        }
    }
}

