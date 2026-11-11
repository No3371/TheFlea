namespace TheFlea
{
    // The more credits change hands with the trader, the more he brings and takes on each visit.
    // The total lives in the save, so a rollback restores it along with the trades behind it.
    internal static class TraderLevel
    {
        private const string CreditsKey = "trader_credits";

        private static long Credits()
        {
            long.TryParse(ModHelper.GetModData(ModEntry.ModId, CreditsKey), out long credits);
            return credits;
        }

        // He takes as many items as he brings offers.
        public static int Offers() => Levels.At(Credits()).Offers;

        public static int Items() => Offers();

        public static void Add(long amount)
        {
            if (amount > 0)
            {
                ModHelper.SetModData(ModEntry.ModId, CreditsKey, (Credits() + amount).ToString());
            }
        }

        // The trader's announcement if this amount would take the player to a new level, otherwise null.
        public static string LevelUpLineAfter(long amount)
        {
            int? offers = Levels.UpAfter(Credits(), amount);
            return offers != null ? ModEntry.Line("visit_level_up", offers.Value, offers.Value) : null;
        }
    }
}
