using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Health;

public sealed record FoodRequest(
    string Name,
    string ServingDesc,
    decimal? ServingGrams,
    decimal Kcal,
    decimal? ProteinG,
    decimal? CarbG,
    decimal? FatG);

public sealed record FoodResponse(
    Guid Id, string Name, string ServingDesc, decimal? ServingGrams,
    decimal Kcal, decimal? ProteinG, decimal? CarbG, decimal? FatG, bool IsSystem);

public sealed record AddMealItemRequest(
    string MealType, DateOnly? OnDate, Guid FoodId, decimal Quantity);

public sealed record MealItemResponse(
    Guid Id, Guid FoodId, string FoodName, decimal Quantity,
    decimal Kcal, decimal? ProteinG, decimal? CarbG, decimal? FatG);

public sealed record MealResponse(
    Guid Id, DateOnly OnDate, string MealType,
    decimal Kcal, decimal ProteinG, decimal CarbG, decimal FatG,
    IReadOnlyList<MealItemResponse> Items);

public sealed record NutritionDayResponse(
    DateOnly OnDate,
    decimal Kcal,
    decimal ProteinG,
    decimal CarbG,
    decimal FatG,
    int? KcalTarget,
    short? ProteinTargetG,
    short? CarbTargetG,
    short? FatTargetG,
    IReadOnlyList<MealResponse> Meals);

public sealed record GoalRequest(
    int? KcalTarget,
    short? ProteinTargetG,
    short? CarbTargetG,
    short? FatTargetG,
    short? WorkoutsPerWeek,
    decimal? WeightTargetKg);

public sealed record MeasurementRequest(DateOnly? OnDate, decimal? WeightKg);

public sealed record MeasurementResponse(Guid Id, DateOnly OnDate, decimal? WeightKg);

/// <summary>
/// Beslenme ve kalori, the food catalogue behind it, goals and weight.
/// </summary>
public static class NutritionEndpoints
{
    public static IEndpointRouteBuilder MapNutritionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var foods = app.MapGroup("/api/v1/foods").WithTags("Health").RequireAuthorization();
        foods.MapGet("/", SearchFoodsAsync);
        foods.MapPost("/", CreateFoodAsync);

        var meals = app.MapGroup("/api/v1/meals").WithTags("Health").RequireAuthorization();
        meals.MapGet("/", DayAsync);
        meals.MapPost("/items", AddItemAsync);
        meals.MapDelete("/items/{id:guid}", RemoveItemAsync);

        var health = app.MapGroup("/api/v1/health").WithTags("Health").RequireAuthorization();
        health.MapGet("/goals", GetGoalsAsync);
        health.MapPut("/goals", SaveGoalsAsync);
        health.MapGet("/measurements", ListMeasurementsAsync);
        health.MapPut("/measurements", SaveMeasurementAsync);

        return app;
    }

    // ── foods ────────────────────────────────────────────────────────────

    /// <summary>
    /// The catalogue: this user's foods plus any system ones.
    /// </summary>
    /// <remarks>
    /// Every row here was typed by someone — no external catalogue is imported,
    /// by decision. Foods are not an OwnedEntity (a system row has no owner),
    /// so the scoping is explicit.
    /// </remarks>
    private static async Task<IResult> SearchFoodsAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct, string? q = null)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var query = db.Foods.Where(f => f.OwnerId == userId || f.OwnerId == null);

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(f => EF.Functions.ILike(f.Name, $"%{q}%"));

        var foods = await query
            .OrderBy(f => f.Name)
            .Take(50)
            .Select(f => new FoodResponse(
                f.Id, f.Name, f.ServingDesc, f.ServingGrams,
                f.Kcal, f.ProteinG, f.CarbG, f.FatG, f.IsSystem))
            .ToListAsync(ct);

        return Results.Ok(foods);
    }

    private static async Task<IResult> CreateFoodAsync(
        FoodRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
            return Invalid("name", "Yemek adı boş olamaz.");

        if (string.IsNullOrWhiteSpace(request.ServingDesc))
            return Invalid("servingDesc", "Porsiyon açıklaması gerekli.");

        if (request.Kcal < 0) return Invalid("kcal", "Kalori negatif olamaz.");

        var food = new Food
        {
            OwnerId = userId,
            Name = request.Name.Trim(),
            ServingDesc = request.ServingDesc.Trim(),
            ServingGrams = request.ServingGrams,
            Kcal = request.Kcal,
            ProteinG = request.ProteinG,
            CarbG = request.CarbG,
            FatG = request.FatG,
            IsSystem = false,
        };

        db.Foods.Add(food);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/foods/{food.Id}", new FoodResponse(
            food.Id, food.Name, food.ServingDesc, food.ServingGrams,
            food.Kcal, food.ProteinG, food.CarbG, food.FatG, false));
    }

    // ── the day ──────────────────────────────────────────────────────────

    private static async Task<IResult> DayAsync(
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct,
        DateOnly? onDate = null)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var date = onDate ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        var meals = await db.Meals
            .Where(m => m.OnDate == date)
            .OrderBy(m => m.MealType)
            .ToListAsync(ct);

        var mealIds = meals.Select(m => m.Id).ToList();

        var items = await db.MealItems
            .Where(i => mealIds.Contains(i.MealId))
            .Join(
                db.Foods,
                i => i.FoodId,
                f => f.Id,
                (i, f) => new { i.MealId, Item = new MealItemResponse(
                    i.Id, i.FoodId, f.Name, i.Quantity,
                    i.Kcal, i.ProteinG, i.CarbG, i.FatG) })
            .ToListAsync(ct);

        var goal = await db.HealthGoals.FirstOrDefaultAsync(g => g.OwnerId == userId, ct);

        var described = meals.Select(m =>
        {
            var own = items.Where(i => i.MealId == m.Id).Select(i => i.Item).ToList();

            return new MealResponse(
                m.Id,
                m.OnDate,
                m.MealType.ToString(),
                own.Sum(i => i.Kcal),
                own.Sum(i => i.ProteinG ?? 0),
                own.Sum(i => i.CarbG ?? 0),
                own.Sum(i => i.FatG ?? 0),
                own);
        }).ToList();

        return Results.Ok(new NutritionDayResponse(
            date,
            described.Sum(m => m.Kcal),
            described.Sum(m => m.ProteinG),
            described.Sum(m => m.CarbG),
            described.Sum(m => m.FatG),
            goal?.KcalTarget,
            goal?.ProteinTargetG,
            goal?.CarbTargetG,
            goal?.FatTargetG,
            described));
    }

    /// <summary>
    /// Adds a food to a meal, creating the meal if this is its first item.
    /// </summary>
    /// <remarks>
    /// The macros are SNAPSHOTTED here. If the food's catalogue values are
    /// corrected next week, today's log must not silently change — a nutrition
    /// diary that rewrites history is worse than useless.
    /// </remarks>
    private static async Task<IResult> AddItemAsync(
        AddMealItemRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        if (!Enum.TryParse<MealType>(request.MealType, ignoreCase: true, out var type)
            || !Enum.IsDefined(type))
        {
            return Invalid("mealType", "Breakfast, Snack, Lunch veya Dinner olmalı.");
        }

        if (request.Quantity <= 0)
            return Invalid("quantity", "Miktar sıfırdan büyük olmalı.");

        var food = await db.Foods.FirstOrDefaultAsync(
            f => f.Id == request.FoodId
                 && (f.OwnerId == userId || f.OwnerId == null), ct);

        if (food is null) return Invalid("foodId", "Yemek bulunamadı.");

        var date = request.OnDate ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        var meal = await db.Meals.FirstOrDefaultAsync(
            m => m.OnDate == date && m.MealType == type, ct);

        if (meal is null)
        {
            meal = new Meal { OnDate = date, MealType = type };
            db.Meals.Add(meal);
        }

        var item = new MealItem
        {
            MealId = meal.Id,
            FoodId = food.Id,
            Quantity = request.Quantity,
            Kcal = food.Kcal * request.Quantity,
            ProteinG = food.ProteinG * request.Quantity,
            CarbG = food.CarbG * request.Quantity,
            FatG = food.FatG * request.Quantity,
        };

        db.MealItems.Add(item);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/v1/meals/items/{item.Id}",
            new MealItemResponse(
                item.Id, food.Id, food.Name, item.Quantity,
                item.Kcal, item.ProteinG, item.CarbG, item.FatG));
    }

    private static async Task<IResult> RemoveItemAsync(
        Guid id, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        // Meal items carry no owner of their own, so they are reached through
        // the meal — which does, and is filtered.
        var item = await db.MealItems
            .Where(i => i.Id == id)
            .Join(db.Meals, i => i.MealId, m => m.Id, (i, m) => i)
            .FirstOrDefaultAsync(ct);

        if (item is null) return Results.NotFound();

        db.MealItems.Remove(item);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ── goals and weight ─────────────────────────────────────────────────

    private static async Task<IResult> GetGoalsAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var goal = await db.HealthGoals.FirstOrDefaultAsync(g => g.OwnerId == userId, ct);

        return Results.Ok(new GoalRequest(
            goal?.KcalTarget,
            goal?.ProteinTargetG,
            goal?.CarbTargetG,
            goal?.FatTargetG,
            goal?.WorkoutsPerWeek,
            goal?.WeightTargetKg));
    }

    private static async Task<IResult> SaveGoalsAsync(
        GoalRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var goal = await db.HealthGoals.FirstOrDefaultAsync(g => g.OwnerId == userId, ct);

        if (goal is null)
        {
            goal = new HealthGoal { OwnerId = userId.Value };
            db.HealthGoals.Add(goal);
        }

        goal.KcalTarget = request.KcalTarget;
        goal.ProteinTargetG = request.ProteinTargetG;
        goal.CarbTargetG = request.CarbTargetG;
        goal.FatTargetG = request.FatTargetG;
        goal.WorkoutsPerWeek = request.WorkoutsPerWeek;
        goal.WeightTargetKg = request.WeightTargetKg;
        goal.UpdatedAt = clock.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListMeasurementsAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct, int take = 90)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var rows = await db.BodyMeasurements
            .OrderByDescending(m => m.OnDate)
            .Take(Math.Clamp(take, 1, 365))
            .Select(m => new MeasurementResponse(m.Id, m.OnDate, m.WeightKg))
            .ToListAsync(ct);

        return Results.Ok(rows);
    }

    /// <summary>
    /// Records weight for a day. Weighing yourself twice corrects the entry
    /// rather than creating a second truth for the same morning.
    /// </summary>
    private static async Task<IResult> SaveMeasurementAsync(
        MeasurementRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        if (request.WeightKg is <= 0 or > 500)
            return Invalid("weightKg", "Geçerli bir kilo gir.");

        var date = request.OnDate ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        var measurement = await db.BodyMeasurements
            .FirstOrDefaultAsync(m => m.OnDate == date, ct);

        if (measurement is null)
        {
            measurement = new BodyMeasurement { OnDate = date };
            db.BodyMeasurements.Add(measurement);
        }

        measurement.WeightKg = request.WeightKg;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new MeasurementResponse(
            measurement.Id, measurement.OnDate, measurement.WeightKg));
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message],
        });
}
