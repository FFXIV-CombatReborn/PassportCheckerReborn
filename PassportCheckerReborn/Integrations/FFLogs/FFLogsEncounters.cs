using System;
using System.Collections.Generic;
using System.Linq;

namespace PassportCheckerReborn.Integrations.FFLogs;

internal static class FFLogsEncounters
{
    private const int DifficultyNormal = 100;
    private const int DifficultySavage = 101;

    private static readonly Dictionary<string, (int EncounterId, int Difficulty)> Encounters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAC Light-heavyweight M1 (Savage)"] = (93, DifficultySavage),
        ["AAC Light-heavyweight M2 (Savage)"] = (94, DifficultySavage),
        ["AAC Light-heavyweight M3 (Savage)"] = (95, DifficultySavage),
        ["AAC Light-heavyweight M4 (Savage)"] = (96, DifficultySavage),

        ["AAC Cruiserweight M1 (Savage)"] = (97, DifficultySavage),
        ["AAC Cruiserweight M2 (Savage)"] = (98, DifficultySavage),
        ["AAC Cruiserweight M3 (Savage)"] = (99, DifficultySavage),
        ["AAC Cruiserweight M4 (Savage)"] = (100, DifficultySavage),

        ["AAC Heavyweight M1 (Savage)"] = (101, DifficultySavage),
        ["AAC Heavyweight M2 (Savage)"] = (102, DifficultySavage),
        ["AAC Heavyweight M3 (Savage)"] = (103, DifficultySavage),
        ["AAC Heavyweight M4 (Savage) P1"] = (104, DifficultySavage),
        ["AAC Heavyweight M4 (Savage) P2"] = (105, DifficultySavage),

        ["The Cloud of Darkness (Chaotic)"] = (2061, DifficultyNormal),

        ["Tsukuyomi's Pain (Unreal)"] = (3012, DifficultyNormal),
        ["Shinryu's Domain (Unreal)"] = (3013, DifficultyNormal),

        ["Worqor Lar Dor (Extreme)"] = (1071, DifficultyNormal),
        ["Everkeep (Extreme)"] = (1072, DifficultyNormal),
        ["The Minstrel's Ballad: Sphene's Burden"] = (1078, DifficultyNormal),
        ["Recollection (Extreme)"] = (1080, DifficultyNormal),
        ["The Minstrel's Ballad: Necron's Embrace"] = (1081, DifficultyNormal),
        ["The Windward Wilds (Extreme)"] = (1082, DifficultyNormal),
        ["Hell on Rails (Extreme)"] = (1083, DifficultyNormal),
        ["The Unmaking (Extreme)"] = (1084, DifficultyNormal),

        ["The Unending Coil of Bahamut (Ultimate)"] = (1073, DifficultyNormal),
        ["The Weapon's Refrain (Ultimate)"] = (1074, DifficultyNormal),
        ["The Epic of Alexander (Ultimate)"] = (1075, DifficultyNormal),
        ["Dragonsong's Reprise (Ultimate)"] = (1076, DifficultyNormal),
        ["The Omega Protocol (Ultimate)"] = (1077, DifficultyNormal),
        ["Futures Rewritten (Ultimate)"] = (1079, DifficultyNormal),
        ["Dancing Mad (Ultimate)"] = (1085, DifficultyNormal),
    };

    private static readonly Dictionary<string, (int Phase1, int Phase2)> MultiPartEncounters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AAC Heavyweight M4 (Savage)"] = (104, 105),
    };

    private static readonly Dictionary<int, int> DifficultyByEncounter =
        Encounters.Values.ToDictionary(encounter => encounter.EncounterId, encounter => encounter.Difficulty);

    public static IEnumerable<string> SupportedDutyNames => Encounters.Keys;

    public static (int Primary, int? Secondary)? GetEncounterIdsForDuty(string? dutyName)
    {
        if (string.IsNullOrWhiteSpace(dutyName))
        {
            return null;
        }

        if (MultiPartEncounters.TryGetValue(dutyName, out var parts))
        {
            return (parts.Phase1, parts.Phase2);
        }

        return Encounters.TryGetValue(dutyName, out var encounter) ? (encounter.EncounterId, null) : null;
    }

    public static bool TryGetDifficulty(int encounterId, out int difficulty)
    {
        return DifficultyByEncounter.TryGetValue(encounterId, out difficulty);
    }
}
