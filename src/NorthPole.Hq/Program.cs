using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Anthropic;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NorthPole.Contracts;
using NorthPole.Hq;
using NorthPole.Hq.Mail;
using NorthPole.Hq.Qr;
using NorthPole.Hq.Santa;
using NorthPole.Hq.Wishes;
using NorthPole.Messaging;

const long MaxPhotoBytes = 6 * 1024 * 1024;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<HqOptions>(builder.Configuration.GetSection("NorthPole"));
var hq = builder.Configuration.GetSection("NorthPole").Get<HqOptions>() ?? new();

// ---- Messaging: embedded MQTT broker + link to the signal box ------------------------------------
builder.Services.AddNorthPoleMessaging(
    link =>
    {
        link.Host = hq.Mqtt.EmbeddedBroker ? "localhost" : hq.Mqtt.Host;
        link.Port = hq.Mqtt.Port;
        link.ClientId = "hq";
    },
    hq.Mqtt.EmbeddedBroker ? broker => broker.Port = hq.Mqtt.Port : null);

// ---- Santa: Claude (or the offline fallback), Piper voice ---------------------------------------
builder.Services.AddSingleton<OfflineSantaBrain>();
if (hq.Santa.Brain == BrainMode.Claude)
{
    builder.Services.AddSingleton(_ => string.IsNullOrWhiteSpace(hq.Santa.ApiKey)
        ? new AnthropicClient() // reads ANTHROPIC_API_KEY
        : new AnthropicClient { ApiKey = hq.Santa.ApiKey });
    builder.Services.AddSingleton<ISantaBrain, ClaudeSantaBrain>();
}
else
{
    builder.Services.AddSingleton<ISantaBrain>(sp => sp.GetRequiredService<OfflineSantaBrain>());
}
builder.Services.AddHttpClient<SantaVoice>(http => http.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton<StationSpeaker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StationSpeaker>());

// ---- Wishes, family, QR cards, the mail train ----------------------------------------------------
builder.Services.AddSingleton<WishStore>();
builder.Services.AddSingleton<FamilyService>();
builder.Services.AddSingleton<QrCards>();
builder.Services.AddSingleton<MailTrainClient>();
builder.Services.AddSingleton<StandbySettings>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StandbySettings>());
builder.Services.AddSingleton<WishPipeline>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WishPipeline>());
builder.Services.AddSingleton<PiPower>();

// ---- Parents' dashboard: PIN → cookie, and a rate limit so small fingers can't brute-force it ----
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "elves";
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.ExpireTimeSpan = TimeSpan.FromDays(60);
        o.Events.OnRedirectToLogin = ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("pin", w =>
    {
        w.PermitLimit = 5;
        w.Window = TimeSpan.FromMinutes(5);
        w.QueueLimit = 0;
    });
});
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxPhotoBytes + 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var converter in NorthPoleJson.Options.Converters) o.SerializerOptions.Converters.Add(converter);
});

var app = builder.Build();

if (hq.Elves.Pin == "1225")
    app.Logger.LogWarning("The elves' PIN is still 1225. Change NorthPole:Elves:Pin before the kids guess it!");
if (hq.Family.Count == 0)
    app.Logger.LogWarning("No children configured. Add them under NorthPole:Family in appsettings.json");

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// =================================================================================================
// Kids: scan the QR card → /W/{code} → the wish page
// =================================================================================================

app.MapGet("/w/{code}", (string code, FamilyService family, IWebHostEnvironment env) =>
    family.FindByCode(code) is null
        ? Results.Redirect("/")
        : Results.File(Path.Combine(env.WebRootPath, "wish.html"), "text/html; charset=utf-8"));

var kid = app.MapGroup("/api/kid/{code}");

kid.MapGet("", (string code, FamilyService family, WishStore store, IOptions<HqOptions> o) =>
{
    if (family.FindByCode(code) is not { } child) return Results.NotFound();
    var today = Calendar.Today;
    var wishes = store.Query(ws => ws.Where(w => w.ChildId == child.Id).OrderByDescending(w => w.CreatedAt).Select(KidWishView.From));
    var sentToday = wishes.Count(w => w.Day == today);
    var allowed = o.Value.WishesPerDay + family.ExtraWishesToday(child.Id, today);
    var latest = wishes.FirstOrDefault();

    return Results.Ok(new KidHome(
        child.Name,
        Calendar.DayLabel(today),
        Calendar.SleepsToChristmas(today),
        sentToday < allowed && latest is not { IsFinal: false },
        latest,
        wishes.Skip(1).Take(10).ToList()));
});

kid.MapPost("/wish", async (string code, HttpRequest request, FamilyService family, WishStore store,
    WishPipeline pipeline, IOptions<HqOptions> o, CancellationToken ct) =>
{
    if (family.FindByCode(code) is not { } child) return Results.NotFound();

    var form = await request.ReadFormAsync(ct);
    var text = (form["text"].ToString() ?? "").Trim();
    if (text.Length is 0 or > 1000)
        return Results.BadRequest(new { error = "Write your wish first (up to 1000 letters)." });

    var today = Calendar.Today;
    var allowed = o.Value.WishesPerDay + family.ExtraWishesToday(child.Id, today);
    var sentToday = store.Query(ws => ws.Where(w => w.ChildId == child.Id && w.Day == today)).Count;
    if (sentToday >= allowed)
        return Results.Conflict(new { error = "You've already sent today's wish. Santa will be waiting for tomorrow's!" });

    var wish = new Wish { ChildId = child.Id, ChildName = child.Name, Day = today, Text = text };
    wish.AddEvent("mailbox", "Your letter is in the mailbox!");

    if (form.Files.GetFile("photo") is { Length: > 0 } photo)
    {
        if (photo.Length > MaxPhotoBytes || photo.ContentType is not ("image/jpeg" or "image/png"))
            return Results.BadRequest(new { error = "That picture is too big for the mail train. Try a smaller one." });
        var ext = photo.ContentType == "image/png" ? ".png" : ".jpg";
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(o.Value.DataDirectory), "photos")).FullName;
        await using var file = File.Create(Path.Combine(dir, wish.Id + ext));
        await photo.CopyToAsync(file, ct);
        wish.PhotoFile = wish.Id + ext;
    }

    store.Add(wish);
    pipeline.Enqueue(wish.Id);
    return Results.Ok(new { wishId = wish.Id });
});

// Live journey of one letter (Server-Sent Events): the page re-renders on every step.
app.MapGet("/api/wish/{id:guid}/events", IResult (Guid id, WishStore store, CancellationToken ct) =>
    store.Read(id, w => w) is null
        ? Results.NotFound()
        : TypedResults.ServerSentEvents(store.WatchAsync(id, KidWishView.From, v => v.IsFinal, ct), eventType: "wish"));

app.MapGet("/api/wish/{id:guid}/voice", (Guid id, WishStore store, SantaVoice voice) =>
{
    var file = store.Read(id, w => w.Status == WishStatus.Answered ? voice.PathOf(w.VoiceFile) : null);
    return file is not null && File.Exists(file) ? Results.File(file, "audio/wav") : Results.NotFound();
});

// =================================================================================================
// Elves (parents): PIN-protected dashboard, QR cards, LEGO mosaic plans, train controls
// =================================================================================================

app.MapGet("/elves", () => Results.Redirect("/elves.html"));
app.MapGet("/cards", () => Results.Redirect("/cards.html"));

app.MapPost("/api/elves/login", async (LoginRequest login, HttpContext ctx, IOptions<HqOptions> o) =>
{
    var ok = CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(login.Pin ?? ""), Encoding.UTF8.GetBytes(o.Value.Elves.Pin));
    if (!ok) return Results.Unauthorized();

    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "elf")], CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(identity));
    return Results.Ok();
}).RequireRateLimiting("pin");

var elves = app.MapGroup("/api/elves").RequireAuthorization();

elves.MapGet("/wishes", (WishStore store) =>
    store.Query(ws => ws.OrderByDescending(w => w.CreatedAt).Select(ElfWishView.From)));

elves.MapGet("/wishes/{id:guid}/photo", (Guid id, WishStore store, IOptions<HqOptions> o) =>
{
    var file = store.Read(id, w => w.PhotoFile);
    var path = file is null ? null : Path.Combine(Path.GetFullPath(o.Value.DataDirectory), "photos", file);
    return path is not null && File.Exists(path)
        ? Results.File(path, path.EndsWith(".png") ? "image/png" : "image/jpeg")
        : Results.NotFound();
});

elves.MapPost("/wishes/{id:guid}/gifts/{index:int}", (Guid id, int index, BoughtRequest body, WishStore store) =>
    store.Update(id, w =>
    {
        if (index >= 0 && index < w.Bought.Count) w.Bought[index] = body.Bought;
    }) ? Results.Ok() : Results.NotFound());

elves.MapPost("/wishes/{id:guid}/replay", (Guid id, WishStore store, SantaVoice voice) =>
{
    var played = store.Read(id, w => w.VoiceFile is null ? null : new { w.VoiceFile, w.ChildName });
    if (played is null) return Results.NotFound();
    _ = voice.PlayOnStationAsync(played.VoiceFile, $"{played.ChildName}'s letter", CancellationToken.None);
    return Results.Ok();
});

// Santa's speakers: whether their sound card is plugged in and since when, its volume, and what Santa is saying, with
// the Pi's clock for the timer.
elves.MapGet("/speaker", (StationSpeaker speaker) => new { state = speaker.State, now = DateTimeOffset.Now });

// The dashboard's volume slider, 0 to 100: set on the card now, and again whenever it's plugged back in.
elves.MapPost("/speaker/volume", async (int percent, StationSpeaker speaker, CancellationToken ct) =>
{
    if (percent is < 0 or > 100) return Results.BadRequest("percent must be 0 to 100");
    return await speaker.SetVolumeAsync(percent, ct)
        ? Results.Ok()
        : Results.Problem("Saved, but the speakers' sound card isn't there to set it on: it's set when they're plugged in.", statusCode: 503);
});

// The standby slider: minutes with nothing happening before the train sleeps. 0 never.
elves.MapPost("/train/standby", async (int minutes, StandbySettings standby) =>
{
    if (minutes is < 0 or > StandbySettings.MostMinutes) return Results.BadRequest($"minutes must be 0 to {StandbySettings.MostMinutes}");
    await standby.SetTrainAsync(minutes);
    return Results.Ok();
});

// The dashboard's speaker check: Santa says the elf's words, or his usual line, on his speakers; the answer says where,
// or why he couldn't.
elves.MapPost("/voice/test", async ([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] VoiceTestRequest? request,
    SantaVoice voice, CancellationToken ct) =>
{
    var test = await voice.TestStationAsync(request?.Text, ct);
    return test.Where is { } speaker ? Results.Ok(new { speaker }) : Results.Problem(test.Problem, statusCode: 503);
});

elves.MapPost("/children/{childId}/extra-wish", (string childId, FamilyService family) =>
{
    if (family.FindById(childId) is null) return Results.NotFound();
    family.GrantExtraWish(childId, Calendar.Today);
    return Results.Ok();
});

elves.MapPost("/children/{childId}/new-code", (string childId, FamilyService family) =>
    family.FindById(childId) is null ? Results.NotFound() : Results.Ok(new { code = family.RegenerateCode(childId) }));

elves.MapGet("/cards", (FamilyService family, QrCards cards) =>
    family.Children.Select(c => cards.Build(c, family.CodeFor(c.Id))).Append(cards.BuildDriver()));

elves.MapGet("/train", TrainStatus);
elves.MapPost("/train/{command}", SendTrainCommand);

// The danger zone's health line: the Pi's temperature and whether it has had to slow down, from vcgencmd.
elves.MapGet("/pi", (CancellationToken ct) => PiHealth.ReadAsync(ct));

// The danger zone: reboots or shuts down the whole Pi, with HQ, the signal box and Piper on it. 202 while it's on its
// way; the page then waits for HQ to come back, or says when the Pi is off.
elves.MapPost("/pi/{action}", async (PowerAction action, PiPower power, CancellationToken ct) =>
    !Enum.IsDefined(action)
        ? Results.NotFound()
        : await power.RequestAsync(action, ct) is { } why
            ? Results.Problem(why, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Accepted());

// =================================================================================================
// The train driver (/driver, from its own QR card): the train's status, Connect and Disconnect, and the driver's
// controls, with no PIN, so anyone on the home Wi-Fi can drive. Mail runs stay with the elves: no Test run here, and
// the signal box still caps the speed, won't drive during a run, and brakes if HQ goes quiet. Stop still works, as a
// brake should.
// =================================================================================================

TrainCommandKind[] driverCommands =
    [TrainCommandKind.Drive, TrainCommandKind.Stop, TrainCommandKind.LightsOn, TrainCommandKind.LightsOff,
     TrainCommandKind.Connect, TrainCommandKind.Disconnect];

app.MapGet("/driver", (IWebHostEnvironment env) =>
    Results.File(Path.Combine(env.WebRootPath, "driver.html"), "text/html; charset=utf-8"));
var driver = app.MapGroup("/api/driver");
driver.MapGet("/train", TrainStatus);
driver.MapPost("/train/{command}", (TrainCommandKind command, int? speed, MailTrainClient train) =>
    driverCommands.Contains(command)
        ? SendTrainCommand(command, speed, train)
        : Task.FromResult(Results.StatusCode(StatusCodes.Status403Forbidden)));

elves.MapGet("/summary", (WishStore store, FamilyService family) =>
{
    var spend = store.Query(ws => ws.Where(w => w.Usage is not null).Select(w => w.Usage!.EstimatedCostUsd)).Sum();
    var count = store.Query(ws => ws.Select(w => w.Id)).Count;
    return new
    {
        wishes = count,
        claudeSpendUsd = Math.Round(spend, 4),
        children = family.Children.Select(c => new { c.Id, c.Name }),
    };
});

app.Run();

// now: the Pi's clock, so a page can time the hub's connection and spot an old state whatever the phone's clock says.
// idleMinutes: the train's standby as saved here; the state says what the signal box is using.
static object TrainStatus(MailTrainClient train, MqttLink mqtt, StandbySettings standby) =>
    new
    {
        mqttConnected = mqtt.IsConnected, online = train.TrainLooksOnline, state = train.State, now = DateTimeOffset.Now,
        idleMinutes = standby.TrainIdleMinutes,
    };

// The driver sends Drive with ?speed=-100..100 (below 0 in reverse); the signal box caps it at its MaxSpeed.
static async Task<IResult> SendTrainCommand(TrainCommandKind command, int? speed, MailTrainClient train) =>
    command == TrainCommandKind.Drive && speed is not (>= -100 and <= 100)
        ? Results.BadRequest("Drive needs a speed from -100 to 100")
        : await train.SendCommandAsync(command, command == TrainCommandKind.Drive ? speed : null)
            ? Results.Ok()
            : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

record LoginRequest(string? Pin);
record BoughtRequest(bool Bought);
record VoiceTestRequest(string? Text);
