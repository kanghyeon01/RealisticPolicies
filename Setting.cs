using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace RealisticPolicies
{
    /// <summary>Kind of local government; names the defendants of a suit (docs/PLAN.md §7 "자치단체 종류").</summary>
    public enum GovernmentType
    {
        City,
        County,
        State,
    }

    /// <summary>How the city answers a new suit (docs/PLAN.md §7 "기본 대응 방식").</summary>
    public enum DefaultSuitResponse
    {
        AskEachTime,
        AlwaysLawyer,
        AlwaysOfficials,
    }

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

        [SettingsUISection(kSection, kLitigationGroup)]
        public GovernmentType Government { get; set; }

        [SettingsUISection(kSection, kLitigationGroup)]
        public DefaultSuitResponse DefaultResponse { get; set; }

        public override void SetDefaults()
        {
            PunitiveDamages = false;
            Government = GovernmentType.City;
            DefaultResponse = DefaultSuitResponse.AskEachTime;
        }
    }
}
