using System;

namespace Spark
{
    public class CombatStats
    {
        public int kills { get; set; } = 0;
        public int assists { get; set; } = 0;
        public int deaths { get; set; } = 0;
        public float damage { get; set; } = 0;
        public float damage_taken { get; set; } = 0;
        public float damage_taken_raw { get; set; } = 0;
        public int eliminations { get; set; } = 0;
        public int objective_eliminations { get; set; } = 0;
        public float objective_time { get; set; } = 0;
        public float objective_damage { get; set; } = 0;
        public int hill_captures { get; set; } = 0;
        public int hill_defends { get; set; } = 0;

        public static CombatStats operator +(CombatStats a, CombatStats b)
        {
            if (a == null) return b;
            if (b == null) return a;

            return new CombatStats
            {
                kills = a.kills + b.kills,
                assists = a.assists + b.assists,
                deaths = a.deaths + b.deaths,
                damage = a.damage + b.damage,
                damage_taken = a.damage_taken + b.damage_taken,
                damage_taken_raw = a.damage_taken_raw + b.damage_taken_raw,
                eliminations = a.eliminations + b.eliminations,
                objective_eliminations = a.objective_eliminations + b.objective_eliminations,
                objective_time = a.objective_time + b.objective_time,
                objective_damage = a.objective_damage + b.objective_damage,
                hill_captures = a.hill_captures + b.hill_captures,
                hill_defends = a.hill_defends + b.hill_defends
            };
        }
        
        public static CombatStats operator -(CombatStats a, CombatStats b)
        {
            if (a == null) return new CombatStats();
            if (b == null) return a;

            return new CombatStats
            {
                kills = Math.Max(0, a.kills - b.kills),
                assists = Math.Max(0, a.assists - b.assists),
                deaths = Math.Max(0, a.deaths - b.deaths),
                damage = Math.Max(0, a.damage - b.damage),
                damage_taken = Math.Max(0, a.damage_taken - b.damage_taken),
                damage_taken_raw = Math.Max(0, a.damage_taken_raw - b.damage_taken_raw),
                eliminations = Math.Max(0, a.eliminations - b.eliminations),
                objective_eliminations = Math.Max(0, a.objective_eliminations - b.objective_eliminations),
                objective_time = Math.Max(0, a.objective_time - b.objective_time),
                objective_damage = Math.Max(0, a.objective_damage - b.objective_damage),
                hill_captures = Math.Max(0, a.hill_captures - b.hill_captures),
                hill_defends = Math.Max(0, a.hill_defends - b.hill_defends)
            };
        }
    }
}
