using HurryAppPleaseWork;
using HurryAppPleaseWork.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;
using Scalar.AspNetCore;
using SourceAFIS;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddOpenApi();

builder.Services.AddSingleton<FingerPrintStore>();

var app = builder.Build();

app.UseHttpsRedirection();
app.MapPost("/register", async Task<IResult> (AppDbContext db, IFormFile file, [FromForm] string username) =>
{
    if (file == null || string.IsNullOrWhiteSpace(username))
        return Results.BadRequest("File or username is missing.");

    // Save the uploaded file temporarily
    var tempPath = Path.GetTempFileName();
    await using (var stream = File.Create(tempPath))
    {
        await file.CopyToAsync(stream);
    }


    var src = File.ReadAllBytes(tempPath);
    var template = new FingerprintTemplate(new FingerprintImage(src));
    var minitua = FingerPrintTemplateAccessor.GetMinutiae(template);
    // Process the fingerprint image
    var prob = FingerPrintMatcher.LoadImageAndStorePoints(tempPath, username);

    db.Results.Add(prob);
    await db.SaveChangesAsync();

    // Clean up temp file
    File.Delete(tempPath);

    return Results.Ok();
}).DisableAntiforgery();

app.MapPost("/match", async Task<Results<Ok<ScoreResult>, BadRequest<string>>> (AppDbContext db, FingerPrintStore store, IFormFile file) =>
{
    if (file == null)
        return TypedResults.BadRequest("File or username is missing.");

    // Save the uploaded file temporarily
    string tempPath = Path.GetTempFileName();
    await using (var stream = File.Create(tempPath))
    {
        await file.CopyToAsync(stream);
    }
    var test = FingerPrintMatcher.GenerateRectsAndTemplate(tempPath);

    if (store.items.Count == 0)
    {
        var raw = await db.Results
            .AsNoTracking()
            .Select(x => new
            {
                x.Username,
                x.ImageMatrix,
                Templates = x.Templates.Select(t => new { t.Rect, t.Template }).ToList()
            })
            .ToListAsync();

        store.items = raw
            .AsParallel()
            .Select(x => new UserFingerPrintList(
                x.Username,
                FingerPrintMatcher.MatFromBytes(x.ImageMatrix, ImreadModes.Unchanged),
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
                    test.templates,
                    user.Templates.Select(x => (rect: x.Rect, template: x.Template)).ToList()
                )
                .Where(a => a.score >= 20)
                .ToList()
        })
        .Where(x => x.Anchors.Any()) // skip users with no anchors
        .Select(x => new
        {
            x.User,
            BestOverlap = FingerPrintMatcher.FindBestOverlap(x.Anchors, test.mat, x.User.Image, 500)
        })
        .OrderByDescending(x => x.BestOverlap.bestScore)
        .FirstOrDefault();

    File.Delete(tempPath);

    if (bestMatch != null)
    {
        return TypedResults.Ok(new ScoreResult(
            bestMatch.User.Username,
            bestMatch.BestOverlap.bestScore,
            ScoreToCertainty(bestMatch.BestOverlap.bestScore),
            $"{Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds}ms"));
    }
    return TypedResults.BadRequest("well");
}).DisableAntiforgery();

static double ScoreToCertainty(double score, double S0 = 80, double k = 0.02)
{
    // Logistic function
    double certainty = 1.0 / (1.0 + Math.Exp(-k * (score - S0)));
    return certainty * 100.0;
}

//app.MapPost("/register-folder", async Task<IResult> (AppDbContext db, [FromBody] RegisterFolderRequest request) =>
//{
//    if (string.IsNullOrWhiteSpace(request.FolderPath) || string.IsNullOrWhiteSpace(request.Username))
//        return Results.BadRequest("Folder path or username is missing.");

//    if (!Directory.Exists(request.FolderPath))
//        return Results.BadRequest("Specified folder does not exist.");

//    var files = Directory.GetFiles(request.FolderPath, "*.bmp", SearchOption.TopDirectoryOnly);
//    if (files.Length == 0)
//        return Results.BadRequest("No .bmp files found in the folder.");

//    var addedCount = 0;

//    foreach (var file in files)
//    {
//        try
//        {
//            // Load and store points for this image
//            var prob = FingerPrintMatcher.LoadImageAndStorePoints(file, request.Username);
//            db.Results.Add(prob);
//            addedCount++;
//        }
//        catch (Exception ex)
//        {
//            // Optionally log the error and continue with next file
//            Console.WriteLine($"Failed to process {file}: {ex.Message}");
//        }
//    }

//    await db.SaveChangesAsync();

//    return Results.Ok(new { Message = $"{addedCount} images indexed for user '{request.Username}'." });
//}).DisableAntiforgery();

//app.MapPost("/register-folder", async Task<IResult> (AppDbContext db, [FromBody] RegisterFolderRequest request) =>
//{
//    if (string.IsNullOrWhiteSpace(request.FolderPath))
//        return Results.BadRequest("Folder path or username is missing.");

//    if (!Directory.Exists(request.FolderPath))
//        return Results.BadRequest("Specified folder does not exist.");

//    var files = Directory.GetFiles(request.FolderPath, "*.png", SearchOption.TopDirectoryOnly);
//    if (files.Length == 0)
//        return Results.BadRequest("No .bmp files found in the folder.");

//    // Regex pattern to extract Pxxx_fingerprint_y
//    string pattern = @"P\d{3}_fingerprint_\d+(?=_easy)";

//    // Group files by match
//    var groupedFiles = files
//        .Select(f => new { File = f, Match = Regex.Match(Path.GetFileName(f), pattern) })
//        .Where(x => x.Match.Success)
//        .GroupBy(x => x.Match.Value);

//    var addedCount = 0;

//    foreach (var group in groupedFiles)
//    {
//        Console.WriteLine($"Processing group: {group.Key} ({group.Count()} files)");

//        foreach (var fileEntry in group)
//        {
//            try
//            {
//                var prob = FingerPrintMatcher.LoadImageAndStorePoints(fileEntry.File, group.Key);
//                db.Results.Add(prob);
//                addedCount++;
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Failed to process {fileEntry.File}: {ex.Message}");
//            }
//        }
//    }

//    await db.SaveChangesAsync();

//    return Results.Ok(new { Message = $"{addedCount} images indexed for user '{request.Username}'." });
//}).DisableAntiforgery();


//app.MapPost("/upload-folder", async Task<IResult> (AppDbContext db, [FromForm] IFormFile[] files, [FromForm] string username) =>
//{
//    if (string.IsNullOrWhiteSpace(username))
//        return Results.BadRequest("Username is missing.");

//    if (files == null || files.Length == 0)
//        return Results.BadRequest("No files uploaded.");

//    var addedCount = 0;

//    foreach (var file in files)
//    {
//        if (!file.FileName.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
//            continue;

//        // Save the uploaded file to a local path
//        var uploadPath = Path.Combine("Uploads", username);
//        Directory.CreateDirectory(uploadPath); // ensure folder exists

//        var filePath = Path.Combine(uploadPath, file.FileName);
//        try
//        {
//            await using var stream = File.Create(filePath);
//            await file.CopyToAsync(stream);

//            // Process file using its local path
//            var prob = FingerPrintMatcher.LoadImageAndStorePoints(filePath, username);
//            db.Results.Add(prob);
//            addedCount++;
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Failed to process {file.FileName}: {ex.Message}");
//        }
//    }

//    await db.SaveChangesAsync();

//    return Results.Ok(new { Message = $"{addedCount} images indexed for user '{username}'." });
//}).DisableAntiforgery();

app.UseCors(x => x.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());

app.MapOpenApi();
app.MapScalarApiReference(options => options.Servers = []);


app.Run();

//[JsonSerializable(typeof(RegisterRequest))]
//[JsonSerializable(typeof(RegisterFolderRequest))]
//[JsonSerializable(typeof(RegisterRequest))]
//[JsonSerializable(typeof(ScoreResult))]
//[JsonSerializable(typeof(IFormFile))]
//internal partial class SerializationContext : JsonSerializerContext { }



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

internal record ScoreResult(string Username, double Score, double Certainty, string MatchingTime);

public record FingerprintList(Rect Rect, FingerprintTemplate Template);

public record UserFingerPrintList(string Username, Mat Image, List<FingerprintList> Templates);


public class FingerPrintStore
{
    public List<UserFingerPrintList> items { get; set; } = [];
}