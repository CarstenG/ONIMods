﻿using Klei.AI;
using static BetterPlantTending.BetterPlantTendingAttributes;

namespace BetterPlantTending
{
    public class TendedOxyfern : TendedPlant
    {
#pragma warning disable CS0649
        [MyCmpReq]
        private Oxyfern oxyfern;
#pragma warning restore CS0649

        protected override bool ApplyModifierOnEffectRemoved => true;

        protected override void OnPrefabInit()
        {
            base.OnPrefabInit();
            var attributes = this.GetAttributes();
            attributes.Add(OxyfernThroughput);
            attributes.Add(OxyfernThroughputBaseValue);
        }

        public override void ApplyModifier()
        {
            base.ApplyModifier();
            oxyfern.SetConsumptionRate();
        }
    }
}
