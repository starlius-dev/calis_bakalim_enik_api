using CalisBakalimEnik.Domain.Health;

namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// The starter exercise catalogue every account sees.
/// </summary>
/// <remarks>
/// Seeded as system rows (no owner, <c>IsSystem</c>), so they appear alongside a
/// user's own exercises without belonging to anyone. Names follow what is
/// actually said in a Turkish gym: the lifts that are known by their English
/// name keep it (Bench Press, Squat, Deadlift), the rest are Turkish. Muscle
/// tags are Turkish and lower-case because they are shown as chips.
///
/// Adding a line here is enough: the seeder inserts any name it does not find
/// and never touches rows it already created, so an edited catalogue reaches an
/// existing database on the next start. Renaming a line creates a new entry and
/// leaves the old one, which is deliberate: a plan that already uses the old
/// name keeps working.
/// </remarks>
public static class ExerciseCatalogue
{
    public sealed record Entry(string Name, ExerciseCategory Category, string[] Muscles);

    private const ExerciseCategory Strength = ExerciseCategory.Strength;
    private const ExerciseCategory Cardio = ExerciseCategory.Cardio;
    private const ExerciseCategory Mobility = ExerciseCategory.Mobility;

    public static readonly IReadOnlyList<Entry> Entries =
    [
        // ── göğüs ────────────────────────────────────────────────────────
        new("Bench Press", Strength, ["göğüs", "triceps", "ön omuz"]),
        new("Eğimli Dumbbell Press", Strength, ["üst göğüs", "ön omuz"]),
        new("Dumbbell Fly", Strength, ["göğüs"]),
        new("Şınav", Strength, ["göğüs", "triceps"]),
        new("Dips", Strength, ["triceps", "göğüs"]),

        // ── sırt ─────────────────────────────────────────────────────────
        new("Barfiks", Strength, ["sırt", "biceps"]),
        new("Lat Pulldown", Strength, ["sırt", "biceps"]),
        new("Barbell Row", Strength, ["sırt", "biceps"]),
        new("Tek Kol Dumbbell Row", Strength, ["sırt"]),
        new("Kablo Row", Strength, ["sırt"]),
        new("Deadlift", Strength, ["sırt", "arka bacak", "kalça"]),

        // ── bacak ────────────────────────────────────────────────────────
        new("Squat", Strength, ["ön bacak", "kalça"]),
        new("Front Squat", Strength, ["ön bacak", "core"]),
        new("Leg Press", Strength, ["ön bacak", "kalça"]),
        new("Hamle (Lunge)", Strength, ["ön bacak", "kalça"]),
        new("Romanian Deadlift", Strength, ["arka bacak", "kalça"]),
        new("Leg Curl", Strength, ["arka bacak"]),
        new("Leg Extension", Strength, ["ön bacak"]),
        new("Hip Thrust", Strength, ["kalça"]),
        new("Baldır Kaldırma", Strength, ["baldır"]),

        // ── omuz ─────────────────────────────────────────────────────────
        new("Omuz Press", Strength, ["omuz", "triceps"]),
        new("Yana Açış", Strength, ["yan omuz"]),
        new("Face Pull", Strength, ["arka omuz", "sırt"]),

        // ── kol ──────────────────────────────────────────────────────────
        new("Biceps Curl", Strength, ["biceps"]),
        new("Hammer Curl", Strength, ["biceps", "önkol"]),
        new("Triceps Pushdown", Strength, ["triceps"]),
        new("Skull Crusher", Strength, ["triceps"]),

        // ── karın ────────────────────────────────────────────────────────
        new("Plank", Strength, ["core"]),
        new("Mekik", Strength, ["karın"]),
        new("Bacak Kaldırma", Strength, ["alt karın"]),
        new("Russian Twist", Strength, ["karın", "yan karın"]),

        // ── kardiyo ──────────────────────────────────────────────────────
        new("Koşu", Cardio, ["kardiyo"]),
        new("Yürüyüş", Cardio, ["kardiyo"]),
        new("Bisiklet", Cardio, ["kardiyo", "bacak"]),
        new("Kürek Ergometresi", Cardio, ["kardiyo", "sırt"]),
        new("İp Atlama", Cardio, ["kardiyo", "baldır"]),
        new("Eliptik", Cardio, ["kardiyo"]),
        new("Yüzme", Cardio, ["kardiyo", "tüm vücut"]),

        // ── mobilite ─────────────────────────────────────────────────────
        new("Esneme", Mobility, ["tüm vücut"]),
        new("Kalça Açma", Mobility, ["kalça"]),
        new("Omuz Mobilitesi", Mobility, ["omuz"]),
        new("Yoga", Mobility, ["tüm vücut"]),
    ];
}
