using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace RealisticPolicies
{
    [FileLocation("ModsSettings/" + Mod.kName + "/" + Mod.kName)]
    [SettingsUIGroupOrder(kLitigationGroup)]
    [SettingsUIShowGroupName(kLitigationGroup)]
    public sealed class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kLitigationGroup = "Litigation";

        /// <summary>Damages multiplier with punitive damages on (Korean statutes moved from 3× to 5× of the loss, 2023–2025).</summary>
        public const float kPunitiveMultiplier = 5f;

        public Setting(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        /// <summary>Lost suits pay punitive damages: five times the plaintiffs' losses (still capped at twice the annual income).</summary>
        [SettingsUISection(kSection, kLitigationGroup)]
        public bool PunitiveDamages { get; set; }

        public override void SetDefaults()
        {
            PunitiveDamages = false;
        }
    }
}
