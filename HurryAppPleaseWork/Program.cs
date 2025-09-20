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

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
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
            .AsNoTracking()
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

public class RegisterRequest
{
    public IFormFile File { get; set; } = default!;
    public string Username { get; set; } = string.Empty;
}

public class RegisterFolderRequest
{
    public string FolderPath { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
}

internal record ScoreResult(UserRecord User, double Score, double Certainty, string MatchingTime, byte[] Image);

public record FingerprintList(Rect Rect, FingerprintTemplate Template);

public record UserFingerPrintList(int Id, UserRecord User, Mat Image, byte[] ImageMatrix, List<FingerprintList> Templates);


public class FingerPrintStore
{
    public List<UserFingerPrintList> items { get; set; } = [];
}

public record UserRecord(int Id, string Username);

public record UserRegisterResponse(int Id, string Username, string FullName, DateTime CreatedAt, byte[] Image, MinutiaRecord[] Minutias, DateTime? LastCheckIn = null);

public record PositionRecord(short X, short Y);

public record MinutiaRecord(PositionRecord Postion, float Direction, MinutiaType Type);
