namespace Autopot
{
    /// <summary>Which vitals trigger Key 1 / Key 2 in simplified autopot logic.</summary>
    public enum LogicMode
    {
        ManaAndLife,
        LifeOnly,
        EnergyShield,
        HybridLifeEs,
        ManaOnly,
        ManaAndEs,
        LifeEsMana,
    }

    internal static class LogicModeLabels
    {
        public static string Display(LogicMode mode) => DisplayEn(mode);

        private static string DisplayEn(LogicMode mode) => mode switch
        {
            LogicMode.ManaAndLife => "Mana + Life",
            LogicMode.LifeOnly => "Life Only",
            LogicMode.EnergyShield => "Energy Shield (CI/LL)",
            LogicMode.HybridLifeEs => "Hybrid (Life & ES)",
            LogicMode.ManaOnly => "Mana",
            LogicMode.ManaAndEs => "Mana + ES",
            LogicMode.LifeEsMana => "Life + ES + Mana",
            _ => mode.ToString(),
        };

        public static readonly LogicMode[] All =
        {
            LogicMode.ManaAndLife,
            LogicMode.LifeOnly,
            LogicMode.EnergyShield,
            LogicMode.HybridLifeEs,
            LogicMode.ManaOnly,
            LogicMode.ManaAndEs,
            LogicMode.LifeEsMana,
        };
    }
}
