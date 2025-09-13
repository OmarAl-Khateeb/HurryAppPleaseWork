using HurryAppPleaseWork;
using HurryAppPleaseWork.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenCvSharp;
using Scalar.AspNetCore;
using SourceAFIS;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddOpenApi();

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

    // Process the fingerprint image
    var prob = FingerPrintMatcher.LoadImageAndStorePoints(tempPath, username);

    db.Results.Add(prob);
    await db.SaveChangesAsync();

    // Clean up temp file
    File.Delete(tempPath);

    return Results.Ok();
}).DisableAntiforgery();

app.MapPost("/match", async Task<IResult> (AppDbContext db, IFormFile file) =>
{
    if (file == null)
        return Results.BadRequest("File or username is missing.");

    // Save the uploaded file temporarily
    var tempPath = Path.GetTempFileName();
    await using (var stream = File.Create(tempPath))
    {
        await file.CopyToAsync(stream);
    }
    var test = FingerPrintMatcher.GenerateRectsAndTemplate(tempPath);

    var get = await db.Results.AsNoTracking().Select(x => new
    {
        Username = x.Username,
        Image = FingerPrintMatcher.MatFromBytes(x.ImageMatrix, ImreadModes.Unchanged),
        Templates = x.Templates
            .Select(t => new { Rect = t.Rect.ToRectangle(), Template = new FingerprintTemplate(t.Template) })
            .ToList()
    }).ToListAsync();

    var bestMatch = get
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

    if (bestMatch != null)
    {
        return Results.Ok(new
        {
            Username = bestMatch.User.Username,
            Score = bestMatch.BestOverlap.bestScore
        });
    }
    //db.Results.Add(prob);
    await db.SaveChangesAsync();

    // Clean up temp file
    File.Delete(tempPath);

    return Results.Ok();
}).DisableAntiforgery();

app.MapPost("/register-folder", async Task<IResult> (AppDbContext db, [FromBody] RegisterFolderRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.FolderPath) || string.IsNullOrWhiteSpace(request.Username))
        return Results.BadRequest("Folder path or username is missing.");

    if (!Directory.Exists(request.FolderPath))
        return Results.BadRequest("Specified folder does not exist.");

    var files = Directory.GetFiles(request.FolderPath, "*.bmp", SearchOption.TopDirectoryOnly);
    if (files.Length == 0)
        return Results.BadRequest("No .bmp files found in the folder.");

    var addedCount = 0;

    foreach (var file in files)
    {
        try
        {
            // Load and store points for this image
            var prob = FingerPrintMatcher.LoadImageAndStorePoints(file, request.Username);
            db.Results.Add(prob);
            addedCount++;
        }
        catch (Exception ex)
        {
            // Optionally log the error and continue with next file
            Console.WriteLine($"Failed to process {file}: {ex.Message}");
        }
    }

    await db.SaveChangesAsync();

    return Results.Ok(new { Message = $"{addedCount} images indexed for user '{request.Username}'." });
}).DisableAntiforgery();

app.MapOpenApi();
app.MapScalarApiReference();

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