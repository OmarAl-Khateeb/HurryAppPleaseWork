using HurryAppPleaseWork;
using HurryAppPleaseWork.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;
using Scalar.AspNetCore;
using SourceAFIS;
using SourceAFIS.Engine.Features;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Services.AddDbContext<AppDbContext>(o => 
    o.UseNpgsql(builder.Configuration.GetConnectionString("Database"))
    .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));

builder.Services.AddOpenApi();
builder.Services.AddCors();
builder.Services.AddSingleton<FingerPrintStore>();

var app = builder.Build();

app.UseHttpsRedirection();

app.MapPost("/register", async Task<Results<Ok<UserRegisterResponse>, BadRequest<string>>> (AppDbContext db, IFormFile file, [FromForm] string username, [FromForm] string FullName) =>
{
    if (file == null || string.IsNullOrWhiteSpace(username)) return TypedResults.BadRequest("File or username is missing.");

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    byte[] fileBytes = ms.ToArray();

    var image = Cv2.ImDecode(fileBytes, ImreadModes.Color);

    using Mat imagegray = FingerPrintMatcher.Clahe(image);

    fileBytes = FingerPrintMatcher.MatToBytes(imagegray);

    var template = new FingerprintTemplate(new FingerprintImage(fileBytes));

    var prob = FingerPrintMatcher.GetRectanglesAndTemplates(imagegray);

    var user = new User
    {
        Username = username,
        FullName = FullName,
        Results = [
            new ProbResult
            {
                ImageMatrix = fileBytes,
                Templates = prob.Select(x => new ProbRectTemplate { Rect = x.Rect, Template = x.Template.ToByteArray() }).ToArray()
            }
        ]
    };

    var minutias = template.Minutiae.Select(x => new MinutiaRecord(new PositionRecord(x.Position.X, x.Position.Y), x.Direction, x.Type)).ToArray();

    db.Users.Add(user);

    if (await db.SaveChangesAsync() > 0)
        return TypedResults.Ok(new UserRegisterResponse(user.Id, user.Username, user.FullName, user.CreatedAt, user.Results.Select(x => x.ImageMatrix).First(), minutias));

    return TypedResults.BadRequest<string>("Failed to Register");

}).DisableAntiforgery();

app.MapGet("/user/{id:int}", async Task<Results<Ok<UserRegisterResponse>, NotFound<string>>> (AppDbContext db, int id) =>
{
    var user = await db.Users
        .Where(u => u.Id == id)
        .Select(u => new
        {
            u.Id,
            u.Username,
            u.FullName,
            u.CreatedAt,
            ImageMatrix = u.Results.Select(r => r.ImageMatrix).FirstOrDefault(),
            LastCheckIn = u.CheckIns.OrderByDescending(x => x.CreatedAt).Select(x => x.CreatedAt).FirstOrDefault(),
        })
        .FirstOrDefaultAsync();

    if (user == null) return TypedResults.NotFound("User not found.");

    return TypedResults.Ok(new UserRegisterResponse(
        user.Id,
        user.Username,
        user.FullName,
        user.CreatedAt,
        user.ImageMatrix ?? [],
        new FingerprintTemplate(new FingerprintImage(user.ImageMatrix)).Minutiae.Select(x => new MinutiaRecord(new PositionRecord(x.Position.X, x.Position.Y), x.Direction, x.Type)).ToArray(),
        user.LastCheckIn
    ));
});

app.MapPut("/user/{id:int}", async Task<Results<Ok<UserListItem>, NotFound<string>>> (AppDbContext db, int id, UserUpdateRequest update) =>
{
    var user = await db.Users.FindAsync(id);
    if (user == null) return TypedResults.NotFound("User not found.");

    if (!string.IsNullOrWhiteSpace(update.Username))
        user.Username = update.Username;

    if (!string.IsNullOrWhiteSpace(update.FullName))
        user.FullName = update.FullName;

    db.Users.Update(user);

    await db.SaveChangesAsync();

    return TypedResults.Ok(new UserListItem(
        user.Id,
        user.Username,
        user.FullName,
        user.CreatedAt,
        user.CheckIns.Count,
        user.CheckIns.OrderByDescending(c => c.CreatedAt)
            .Select(c => c.CreatedAt)
            .FirstOrDefault()
    ));
});

app.MapDelete("/user/{id:int}", async Task<Results<NotFound<string>, NoContent>> (AppDbContext db, int id) =>
{
    var user = await db.Users.FindAsync(id);
    if (user == null) return TypedResults.NotFound("User not found.");

    await db.CheckIns.Where(x => x.UserId == id).ExecuteDeleteAsync();
    await db.ResultsTemplate.Where(x => x.ProbResult.UserId == id).ExecuteDeleteAsync();
    await db.Results.Where(x => x.UserId == id).ExecuteDeleteAsync();

    db.Users.Remove(user);

    await db.SaveChangesAsync();

    return TypedResults.NoContent();
});

app.MapGet("/user", async Task<Results<Ok<ListResponse<UserListItem>>, BadRequest>> (AppDbContext db, int page = 1, int pageSize = 20) =>
{
    var query = db.Users;
    var count = await query.CountAsync();
    var users = await query
        .OrderBy(u => u.Id)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(u => new UserListItem(
            u.Id,
            u.Username,
            u.FullName,
            u.CreatedAt,
            u.CheckIns.Count,
            u.CheckIns.OrderByDescending(c => c.CreatedAt).Select(c => c.CreatedAt).FirstOrDefault()
        ))
        .ToListAsync();

    return TypedResults.Ok(new ListResponse<UserListItem>(users, count));
});

app.MapGet("/checkin/{userId:int}", async Task<Results<Ok<ListResponse<CheckInItem>>, NotFound<string>>>
    (AppDbContext db, int userId, int page = 1, int pageSize = 20) =>
{
    var totalCount = await db.CheckIns.CountAsync(c => c.UserId == userId);

    if (totalCount == 0)
        return TypedResults.NotFound("No check-ins found for this user.");

    var checkIns = await db.CheckIns
        .Where(c => c.UserId == userId)
        .OrderByDescending(c => c.CreatedAt)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(c => new CheckInItem(
            c.Id,
            c.CreatedAt,
            new ProbResultRecord(c.ProbResult.Id, c.ProbResult.ImageMatrix),
            c.ResultScore,
            c.ImageMatrix
        ))
        .ToListAsync();

    return TypedResults.Ok(new ListResponse<CheckInItem>(checkIns, totalCount));
});

app.MapPost("/match", async Task<Results<Ok<ScoreResult>, BadRequest<string>>> (AppDbContext db, FingerPrintStore store, IFormFile file) =>
{
    if (file == null) return TypedResults.BadRequest("File or username is missing.");

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    byte[] fileBytes = ms.ToArray();

    using var image = Cv2.ImDecode(fileBytes, ImreadModes.Color);

    using Mat imagegray = FingerPrintMatcher.Clahe(image);

    fileBytes = FingerPrintMatcher.MatToBytes(imagegray);

    var templates = FingerPrintMatcher.GetRectanglesAndTemplates(imagegray);

    if (store.items.Count == 0)
    {
        var raw = await db.Results
            .Select(x => new
            {
                x.Id,
                User = new UserRecord(x.User.Id, x.User.Username),
                x.ImageMatrix,
                Templates = x.Templates.Select(t => new { t.Rect, t.Template }).ToList()
            })
            .ToListAsync();

        store.items = raw
            .AsParallel()
            .Select(x => new UserFingerPrintList(
                x.Id,
                x.User,
                FingerPrintMatcher.MatFromBytes(x.ImageMatrix, ImreadModes.Unchanged),
                x.ImageMatrix,
                x.Templates.Select(t => new FingerprintList(t.Rect, new FingerprintTemplate(t.Template))).ToList()
            ))
            .ToList();
    }

    var timestamp = Stopwatch.GetTimestamp();

    var bestMatch = store.items
        .AsParallel()
        .Select(user => new
        {
            User = user,
            Anchors = FingerPrintMatcher
                .FindTopAnchors(
                    templates,
                    user.Templates.Select(x => (rect: x.Rect, template: x.Template)).ToList()
                )
                .Where(a => a.score >= 20)
                .ToList()
        })
        .Where(x => x.Anchors.Count != 0)
        .Select(x => new
        {
            x.User,
            BestOverlap = FingerPrintMatcher.FindBestOverlap(x.Anchors, imagegray, x.User.Image, 500)//this can be optimized
        })
        .OrderByDescending(x => x.BestOverlap.bestScore)
        .FirstOrDefault();


    if (bestMatch != null)
    {
        var checkin = new CheckIn
        {
            ProbResultId = bestMatch.User.Id,
            ImageMatrix = fileBytes,
            UserId = bestMatch.User.User.Id,
            ResultScore = bestMatch.BestOverlap.bestScore
        };

        db.CheckIns.Add(checkin);

        await db.SaveChangesAsync();
    }

    if (bestMatch != null)
    {
        return TypedResults.Ok(new ScoreResult(
            bestMatch.User.User,
            bestMatch.BestOverlap.bestScore,
            ScoreToCertainty(bestMatch.BestOverlap.bestScore),
            $"{Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds}ms",
            bestMatch.User.ImageMatrix));
    }
    return TypedResults.BadRequest("well");
}).DisableAntiforgery();

app.MapGet("user/{userId:int}/fingerprint", async Task<Results<Ok<ListResponse<ProbResultRecord>>, NotFound<string>>>
    (AppDbContext db, int userId) =>
{
    var probResults = await db.Results
        .Where(p => p.UserId == userId)
        .Select(p => new ProbResultRecord(p.Id, p.ImageMatrix))
        .ToListAsync();

    if (!probResults.Any())
        return TypedResults.NotFound("No fingerprints found for this user.");

    return TypedResults.Ok(new ListResponse<ProbResultRecord>(probResults, probResults.Count));
});

app.MapPost("user/{userId:int}/fingerprint", async Task<Results<Created<ProbResultRecord>, NotFound<string>, BadRequest<string>>>
    (AppDbContext db, int userId, IFormFile file) =>
{
    if (file == null || file.Length == 0)
        return TypedResults.BadRequest("No file uploaded.");

    var user = await db.Users.FindAsync(userId);
    if (user == null)
        return TypedResults.NotFound("User not found.");

    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);
    var imageMatrix = ms.ToArray();

    var image = Cv2.ImDecode(imageMatrix, ImreadModes.Color);

    using Mat imagegray = FingerPrintMatcher.Clahe(image);

    imageMatrix = FingerPrintMatcher.MatToBytes(imagegray);

    var template = new FingerprintTemplate(new FingerprintImage(imageMatrix));

    var prob = FingerPrintMatcher.GetRectanglesAndTemplates(imagegray);

    var probResult = new ProbResult
    {
        UserId = userId,
        ImageMatrix = imageMatrix,
        Templates = prob.Select(x => new ProbRectTemplate { Rect = x.Rect, Template = x.Template.ToByteArray() }).ToArray()
    };

    db.Results.Add(probResult);
    await db.SaveChangesAsync();

    var response = new ProbResultRecord(probResult.Id, probResult.ImageMatrix);

    return TypedResults.Created($"/fingerprint/{probResult.Id}", response);
}).DisableAntiforgery();

app.MapDelete("/fingerprint/{id:int}", async Task<Results<NoContent, NotFound<string>>>
    (AppDbContext db, int id) =>
{
    var probResult = await db.Results.FindAsync(id);
    if (probResult == null) return TypedResults.NotFound("Fingerprint not found.");

    await db.ResultsTemplate.Where(x => x.ProbResultId == id).ExecuteDeleteAsync();

    db.Results.Remove(probResult);
    await db.SaveChangesAsync();

    return TypedResults.NoContent();
});


static double ScoreToCertainty(double score, double S0 = 80, double k = 0.02)
{
    // Logistic function
    double certainty = 1.0 / (1.0 + Math.Exp(-k * (score - S0)));
    return certainty * 100.0;
}

app.UseCors(x => x.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());

app.MapOpenApi();
app.MapScalarApiReference(options => options.Servers = []);


app.Run();

internal record ScoreResult(UserRecord User, double Score, double Certainty, string MatchingTime, byte[] Image);

public record FingerprintList(Rect Rect, FingerprintTemplate Template);

public record UserFingerPrintList(int Id, UserRecord User, Mat Image, byte[] ImageMatrix, List<FingerprintList> Templates);


public class FingerPrintStore
{
    public List<UserFingerPrintList> items { get; set; } = [];
}

public record UserRecord(int Id, string Username);

public record UserRegisterResponse(int Id, string Username, string FullName, DateTime CreatedAt, byte[] Image, MinutiaRecord[] Minutias, DateTime? LastCheckIn = null);
public record UserListItem(int Id, string Username, string FullName, DateTime CreatedAt, int CheckInCount, DateTime? LastCheckIn = null);

public record ListResponse<T>(List<T> Items, int totalCount);

public record PositionRecord(short X, short Y);

public record MinutiaRecord(PositionRecord Postion, float Direction, MinutiaType Type);

public record UserUpdateRequest(string? Username = null, string? FullName = null);

public record CheckInItem(int Id, DateTime CreatedAt, ProbResultRecord ProbResult, double ResultScore, byte[] Image);

public record ProbResultRecord(int Id, byte[] Image);
