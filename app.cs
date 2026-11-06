#:sdk Microsoft.NET.Sdk.Web

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using static System.Globalization.CultureInfo;

const int UpcomingDays = 14;
const int CalendarPastDays = 7, CalendarDays = 28;

if (args is ["--check"]) { await SelfCheck(); return; }

var app = WebApplication.Create(args);
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
var ljubljana = TimeZoneInfo.FindSystemTimeZoneById("Europe/Ljubljana");
var cache = new MemoryCache(new MemoryCacheOptions());

// Started from an icon (open.sh), so quit after 30 idle minutes instead of running until reboot
var lastVisit = DateTime.UtcNow;
app.Use((context, next) => { lastVisit = DateTime.UtcNow; return next(context); });
_ = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while (await timer.WaitForNextTickAsync())
        if (DateTime.UtcNow - lastVisit > TimeSpan.FromMinutes(30)) app.Lifetime.StopApplication();
});

app.MapGet("/", async (string? datum, string? scena, string? zanr) =>
{
    var today = Day(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ljubljana));
    var day = ParseDay(datum, today);
    try
    {
        var events = await Cached(day, async () =>
            await LoadEvents(day, day.AddDays(UpcomingDays), (await Cached("genres", LoadGenres))!, FetchDay));
        return Results.Text(Render(events!, day, today, scena, zanr), "text/html");
    }
    catch (Exception e) when (IsFetchError(e))
    {
        app.Logger.LogError(e, "Feed fetch failed");
        return Results.Text("Kulturnik ni dosegljiv.", statusCode: 502);
    }
});

// Cached so switching filters is instant: fetching the feeds takes several seconds
Task<T?> Cached<T>(object key, Func<Task<T>> load) => cache.GetOrCreateAsync(key, entry =>
{
    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
    return load();
});

// For a local Calendar subscription test; GitHub publishes the same file via --ics
app.MapGet("/underground.ics", async () => Results.Text((await Cached("ics", CalendarFile))!, "text/calendar"));

async Task<XDocument> FetchDay(DateOnly when) => XDocument.Parse(await http.GetStringAsync(
    $"https://dogodki.kulturnik.si/?what=glasba&when={when:O}&where=Osrednja%20Slovenija&format=rss"));

async Task<string> CalendarFile()
{
    var today = Day(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, ljubljana));
    var (from, to) = (today.AddDays(-CalendarPastDays), today.AddDays(CalendarDays));
    var events = await LoadEvents(from, to, (await Cached("genres", LoadGenres))!, FetchDay);
    return Ics(events, Scenes.All[0], from, to, DateTimeOffset.UtcNow);
}

// Genres only drive one filter, so the page still works without them
async Task<Dictionary<string, string>> LoadGenres()
{
    try
    {
        return ParseGenres(XDocument.Parse(await http.GetStringAsync("https://www.sigic.si/koledar-dogodkov.rss?view_all=1")));
    }
    catch (Exception e) when (IsFetchError(e))
    {
        app.Logger.LogWarning(e, "Genre feed failed");
        return [];
    }
}

// GitHub Actions runs this hourly and publishes the file for the iCloud subscription.
// A failed download throws, so a broken run never replaces the published calendar.
if (args is ["--ics", var path])
{
    await File.WriteAllTextAsync(path, await CalendarFile());
    return;
}

// Fixed port: macOS AirPlay Receiver occupies the default 5000
app.Run("http://localhost:5080");

static DateOnly Day(DateTimeOffset d) => DateOnly.FromDateTime(d.DateTime);

// Feed dates are JS Date.toString(): "Thu Nov 05 2026 19:00:00 GMT+0100 (Central European Standard Time)"
static DateTimeOffset? ParseDate(string? s) =>
    DateTimeOffset.TryParseExact(s?.Split(" (")[0], "ddd MMM dd yyyy HH:mm:ss 'GMT'zzz", InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

static bool IsFetchError(Exception e) => e is HttpRequestException or TaskCanceledException or XmlException;

// Invalid dates fall back to today; the year bound keeps AddDays in range
static DateOnly ParseDay(string? datum, DateOnly today) =>
    DateOnly.TryParseExact(datum, "yyyy-MM-dd", InvariantCulture, DateTimeStyles.None, out var d) && d.Year is > 2000 and < 2100 ? d : today;

static string SigicId(string? guid) => Regex.Match(guid ?? "", @"sigic_id=(event-\d+)").Groups[1].Value;

// Kulturnik has no genres, but most of its events come from SIGIC's calendar, which does
static Dictionary<string, string> ParseGenres(XDocument feed)
{
    Dictionary<string, string> genres = [];
    foreach (var item in feed.Descendants("item"))
        if (SigicId((string?)item.Element("guid")) is { Length: > 0 } id && (string?)item.Element("category") is { Length: > 0 } category)
            genres[id] = category.Split(" / ")[0];
    return genres;
}

static IEnumerable<Event> ParseFeed(XDocument feed, Dictionary<string, string> genres)
{
    XNamespace ical = "http://www.w3.org/2002/12/cal/icaltzd#";
    XNamespace dc = "http://purl.org/dc/elements/1.1/";
    foreach (var item in feed.Descendants("item"))
        if (ParseDate((string?)item.Element(ical + "dtstart")) is { } start)
        {
            var title = (string?)item.Element("title") ?? "";
            yield return new(
                (string?)item.Element(dc + "identifier") ?? $"{start:O} {title}",
                title,
                (string?)item.Element("link"),
                (string?)item.Element(ical + "location") ?? "",
                (string?)item.Element("enclosure")?.Attribute("url"),
                genres.GetValueOrDefault(SigicId((string?)item.Element("guid"))),
                start,
                ParseDate((string?)item.Element(ical + "dtend")) ?? start);
        }
}

// One feed stops after about 100 upcoming events, however few days that spans, so walk forward
// ponytail: 12 fetches cap, roughly 1200 events; a range is cut short if it ever holds more
static async Task<List<Event>> LoadEvents(DateOnly from, DateOnly to, Dictionary<string, string> genres, Func<DateOnly, Task<XDocument>> fetch)
{
    var when = from;
    List<Event> events = [];
    for (var i = 0; i < 12; i++)
    {
        events.AddRange(ParseFeed(await fetch(when), genres));
        var last = events.Select(e => Day(e.Start)).DefaultIfEmpty(when).Max();
        if (last > to || last <= when) break;
        when = last;
    }
    return Merge(events);
}

// Feeds overlap and repeat running events. Kulturnik also lists some concerts once per source,
// under different titles: same start, same venue up to the first comma, and at least half the
// words of the shorter title in common. The copy with a genre wins and keeps its id (calendar UID).
static List<Event> Merge(IEnumerable<Event> events)
{
    List<List<Event>> groups = [];
    foreach (var e in events.Distinct())
        if (groups.Find(g => SameEvent(g[0], e)) is { } group) group.Add(e);
        else groups.Add([e]);
    return [.. groups.Select(g =>
    {
        var best = g.OrderBy(e => e.Genre is null).ThenBy(e => e.Image is null).ThenBy(e => e.Id, StringComparer.Ordinal).First();
        return best with { Url = best.Url ?? g.Find(e => e.Url is not null)?.Url, Image = best.Image ?? g.Find(e => e.Image is not null)?.Image };
    })];
}

static bool SameEvent(Event a, Event b)
{
    static string Venue(Event e) => e.Location.Split(',')[0].Trim();
    static HashSet<string> Words(string s) => [.. Regex.Matches(s.ToLowerInvariant(), @"\p{L}{3,}").Select(m => m.Value)];
    if (a.Start != b.Start) return false;
    var (va, vb) = (Venue(a), Venue(b));
    if (va is "" || vb is "" || !(va.StartsWith(vb, StringComparison.OrdinalIgnoreCase) || vb.StartsWith(va, StringComparison.OrdinalIgnoreCase))) return false;
    var (wa, wb) = (Words(a.Title), Words(b.Title));
    var shared = wa.Intersect(wb).Count();
    return shared > 0 && 2 * shared >= Math.Min(wa.Count, wb.Count);
}

static string Render(IEnumerable<Event> events, DateOnly day, DateOnly today, string? scena = null, string? zanr = null)
{
    var end = day.AddDays(UpcomingDays);
    var datum = day == today ? null : day.ToString("O");
    var inRange = events.Where(e => Day(e.End) >= day && Day(e.Start) <= end).ToList();
    var genres = inRange.Where(e => e.Genre is not null).GroupBy(e => e.Genre!).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
    var scene = Scenes.All.FirstOrDefault(s => s.Slug == scena);
    var genre = genres.FirstOrDefault(g => g == zanr);
    var all = inRange.Where(e => (scene?.Has(e) ?? true) && (genre is null || e.Genre == genre)).OrderBy(e => e.Start).ToList();
    var onDay = all.Where(e => Day(e.Start) == day).Select(Timed);
    var running = all.Where(e => Day(e.Start) < day && Day(e.End) >= day).OrderBy(e => e.End)
        .Select(e => Card("do " + e.End.ToString("d. M.", InvariantCulture), e));
    var upcoming = string.Concat(all.Where(e => Day(e.Start) > day && Day(e.Start) <= end)
        .GroupBy(e => Day(e.Start))
        .Select(g => $"<h3>{g.Key.ToString("dddd, d. MMMM", GetCultureInfo("sl"))}</h3>{List(g.Select(Timed))}"));

    return $$"""
        <!doctype html>
        <html lang="sl">
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Glasba · Osrednja Slovenija</title>
        <style>
          :root {
            color-scheme: light dark;
            font: 16px/1.4 system-ui;
            --bg: light-dark(#f5f3ef, #131316);
            --card: light-dark(#fff, #1f1f24);
            --muted: light-dark(#5f5f66, #a8a8b2);
          }
          body { max-width: 80rem; margin: 0 auto; padding: 2.5rem 1.25rem 4rem; background: var(--bg); }
          h1 { margin: 0; font-size: clamp(1.9rem, 5vw, 3rem); letter-spacing: -.02em; }
          nav { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem; margin-top: 1.25rem; }
          form { display: contents; }
          nav a, nav input {
            padding: .35rem .9rem;
            border: 0;
            border-radius: 99rem;
            background: var(--card);
            color: inherit;
            font: inherit;
            text-decoration: none;
            box-shadow: 0 1px 3px #0003;
          }
          nav a[aria-current] { background: CanvasText; color: Canvas; }
          nav + nav { margin-top: .6rem; }
          h2 { margin: 2.5rem 0 1rem; font-size: 1.5rem; }
          h3 { margin: 1.75rem 0 .75rem; font-size: 1rem; color: var(--muted); }
          h2::first-letter, h3::first-letter { text-transform: uppercase; }
          ul {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(min(100%, 15rem), 1fr));
            gap: 1rem;
            margin: 0;
            padding: 0;
            list-style: none;
          }
          li {
            position: relative;
            overflow: hidden;
            border-radius: .85rem;
            background: var(--card);
            box-shadow: 0 1px 3px #0003;
          }
          li:hover { box-shadow: 0 8px 24px #0004; }
          li:has(a:focus-visible) { outline: 3px solid CanvasText; outline-offset: 2px; }
          @media (prefers-reduced-motion: no-preference) {
            li { transition: transform .15s, box-shadow .15s; }
            li:hover { transform: translateY(-3px); }
          }
          .media {
            position: relative;
            aspect-ratio: 16 / 9;
            background: linear-gradient(135deg, hsl(var(--hue) 65% 42%), hsl(calc(var(--hue) + 50) 70% 24%));
          }
          .media::before {
            content: "♪";
            position: absolute;
            inset: 0;
            display: grid;
            place-items: center;
            font-size: 3rem;
            color: #fff6;
          }
          .media img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
          .badge {
            position: absolute;
            top: .6rem;
            left: .6rem;
            padding: .1rem .55rem;
            border-radius: 99rem;
            background: #000b;
            color: #fff;
            font-size: .85rem;
            font-variant-numeric: tabular-nums;
          }
          .badge:empty { display: none; }
          li strong { display: block; padding: .75rem .9rem .2rem; line-height: 1.25; }
          li a { color: inherit; text-decoration: none; }
          li a::after { content: ""; position: absolute; inset: 0; }
          li small { display: block; padding: 0 .9rem .9rem; color: var(--muted); }
          footer { margin-top: 3rem; color: var(--muted); }
        </style>
        <h1>Glasba · Osrednja Slovenija</h1>
        <nav aria-label="Datum">
          {{Pill("Danes", null, scene?.Slug, genre, day == today)}}
          <form>
            <input type="date" name="datum" value="{{day:O}}" aria-label="Datum" onchange="this.value && this.form.submit()">
            {{Hidden("scena", scene?.Slug)}}{{Hidden("zanr", genre)}}
          </form>
        </nav>
        <nav aria-label="Prizorišče">{{Pill("Vsa prizorišča", datum, null, genre, scene is null)}}{{string.Concat(Scenes.All.Select(s => Pill(s.Label, datum, s.Slug, genre, s == scene)))}}</nav>
        <nav aria-label="Žanr">{{Pill("Vsi žanri", datum, scene?.Slug, null, genre is null)}}{{string.Concat(genres.Select(g => Pill(g, datum, scene?.Slug, g, g == genre)))}}</nav>
        {{(running.Any() ? "<h2>V teku</h2>" + List(running) : "")}}
        <h2>{{day.ToString(day == today ? "'Danes', d. M. yyyy" : "dddd, d. M. yyyy", GetCultureInfo("sl"))}}</h2>
        {{List(onDay)}}
        <h2>Napovedano, do {{end.ToString("d. M.", InvariantCulture)}}</h2>
        {{(upcoming is "" ? List([]) : upcoming)}}
        <footer><small>Vir: <a href="https://dogodki.kulturnik.si/?what=glasba&amp;where=Osrednja%20Slovenija">Kulturnik</a></small></footer>
        """;
}

static string List(IEnumerable<string> rows) =>
    rows.Any() ? $"<ul>{string.Concat(rows)}</ul>" : "<p>Ni dogodkov.</p>";

// iCalendar (RFC 5545). Times in UTC, so no VTIMEZONE block is needed.
static string Ics(IEnumerable<Event> events, Scene scene, DateOnly from, DateOnly to, DateTimeOffset now)
{
    static string Utc(DateTimeOffset d) => d.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", InvariantCulture);
    static string Date(DateOnly d) => d.ToString("yyyyMMdd", InvariantCulture);

    List<string> lines =
    [
        "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//kulturnik-glasba//SL", "CALSCALE:GREGORIAN", "METHOD:PUBLISH",
        $"X-WR-CALNAME:{IcsText($"Glasba · {scene.Label}")}", "X-WR-TIMEZONE:Europe/Ljubljana",
        "REFRESH-INTERVAL;VALUE=DURATION:PT1H", "X-PUBLISHED-TTL:PT1H",
    ];
    foreach (var e in events.Where(e => scene.Has(e) && Day(e.End) >= from && Day(e.Start) <= to).DistinctBy(e => e.Id).OrderBy(e => e.Start))
    {
        lines.AddRange(["BEGIN:VEVENT", $"UID:{IcsText(e.Id)}", $"DTSTAMP:{Utc(now)}"]);
        // Midnight means no set time (see Timed), so those become all-day events. DTEND is exclusive and left out when the feed has none.
        if (e.Start.TimeOfDay == default)
        {
            lines.Add($"DTSTART;VALUE=DATE:{Date(Day(e.Start))}");
            if (e.End > e.Start) lines.Add($"DTEND;VALUE=DATE:{Date(e.End.TimeOfDay == default ? Day(e.End) : Day(e.End).AddDays(1))}");
        }
        else
        {
            lines.Add($"DTSTART:{Utc(e.Start)}");
            if (e.End > e.Start) lines.Add($"DTEND:{Utc(e.End)}");
        }
        lines.Add($"SUMMARY:{IcsText(e.Title)}");
        lines.Add($"LOCATION:{IcsText(e.Location)}");
        if (e.Genre is not null) lines.Add($"CATEGORIES:{IcsText(e.Genre)}");
        if (HttpUrl(e.Url) is { } url) lines.Add($"URL:{url.AbsoluteUri}");
        lines.Add($"DESCRIPTION:{IcsText(string.Join("\n", new[] { e.Genre, HttpUrl(e.Url)?.AbsoluteUri }.OfType<string>()))}");
        lines.Add("END:VEVENT");
    }
    lines.Add("END:VCALENDAR");
    return string.Concat(lines.Select(IcsFold));
}

static string IcsText(string s) =>
    s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "");

// Lines over 75 bytes continue on the next line after a space, never splitting a UTF-8 character
static string IcsFold(string line)
{
    var folded = new StringBuilder();
    var bytes = 0;
    foreach (var rune in line.EnumerateRunes())
    {
        if (bytes + rune.Utf8SequenceLength > 75) { folded.Append("\r\n "); bytes = 1; }
        folded.Append(rune.ToString());
        bytes += rune.Utf8SequenceLength;
    }
    return folded.Append("\r\n").ToString();
}

static string Hidden(string name, string? value) =>
    value is null ? "" : $"""<input type="hidden" name="{name}" value="{WebUtility.HtmlEncode(value)}">""";

static string Pill(string label, string? datum, string? scena, string? zanr, bool active)
{
    var href = QueryHelpers.AddQueryString("/", new Dictionary<string, string?> { ["datum"] = datum, ["scena"] = scena, ["zanr"] = zanr });
    return $"""<a href="{WebUtility.HtmlEncode(href)}"{(active ? " aria-current=\"page\"" : "")}>{WebUtility.HtmlEncode(label)}</a>""";
}

// Feed uses midnight for events with no set time, so those get no badge
static string Timed(Event e) => Card(e.Start.TimeOfDay == default ? "" : e.Start.ToString("HH:mm", InvariantCulture), e);

// Feed aggregates third-party sources: use only http(s) URLs
static Uri? HttpUrl(string? s) =>
    Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u : null;

static string Card(string badge, Event e)
{
    var title = WebUtility.HtmlEncode(e.Title);
    var heading = HttpUrl(e.Url) is { } url
        ? $"""<a href="{WebUtility.HtmlEncode(url.AbsoluteUri)}" target="_blank" rel="noopener">{title}</a>"""
        : title;
    // Site's resizer: originals can exceed 1 MB. Broken images drop away to reveal the gradient.
    var image = HttpUrl(e.Image) is { } src
        ? $"""<img src="https://image.kulturnik.si/resize/magic/?url={Uri.EscapeDataString(src.AbsoluteUri)}&amp;size=640x640" alt="" loading="lazy" onerror="this.remove()">"""
        : "";
    var hue = e.Location.Sum(c => c) % 360; // Same venue, same color
    var details = e.Genre is null ? e.Location : $"{e.Location} · {e.Genre}";
    return $"""<li><div class="media" style="--hue:{hue}">{image}<span class="badge">{badge}</span></div><strong>{heading}</strong><small>{WebUtility.HtmlEncode(details)}</small></li>""";
}

// dotnet run app.cs -- --check
static async Task SelfCheck()
{
    const string tz = "GMT+0100 (Central European Standard Time)";
    var today = new DateOnly(2026, 11, 5);
    var first = XDocument.Parse($"""
        <rss xmlns:ical="http://www.w3.org/2002/12/cal/icaltzd#"><channel>
        <item><title>Danes &lt;b&gt;</title><link>javascript:alert(1)</link><enclosure url="javascript:alert(2)"/><ical:location>Orto</ical:location><ical:dtstart>Thu Nov 05 2026 19:30:00 {tz}</ical:dtstart></item>
        <item><title>Festival</title><link>https://x.si/?a=1&amp;b=2</link><ical:dtstart>Wed Nov 04 2026 00:00:00 {tz}</ical:dtstart><ical:dtend>Wed Nov 11 2026 00:00:00 {tz}</ical:dtend></item>
        <item><title>Festival</title><link>https://x.si/?a=1&amp;b=2</link><ical:dtstart>Wed Nov 04 2026 00:00:00 {tz}</ical:dtstart><ical:dtend>Wed Nov 11 2026 00:00:00 {tz}</ical:dtend></item>
        <item><title>Mimo</title><ical:dtstart>Tue Nov 03 2026 20:00:00 {tz}</ical:dtstart></item>
        <item><title>Jutri</title><guid>http://www.sigic.si/koledar-dogodkov.rss#https://x.si/j?sigic_id=event-2</guid><enclosure url="https://x.si/a b.jpg"/><ical:dtstart>Fri Nov 06 2026 20:00:00 {tz}</ical:dtstart></item>
        <item><title>Slab datum</title><ical:dtstart>nonsense</ical:dtstart></item>
        <item><title>Sredina</title><ical:dtstart>Fri Nov 13 2026 00:00:00 {tz}</ical:dtstart></item>
        </channel></rss>
        """);
    var second = XDocument.Parse($"""
        <rss xmlns:ical="http://www.w3.org/2002/12/cal/icaltzd#"><channel>
        <item><title>Sredina</title><ical:dtstart>Fri Nov 13 2026 00:00:00 {tz}</ical:dtstart></item>
        <item><title>Zadnji</title><guid>http://www.sigic.si/koledar-dogodkov.rss#https://x.si/z?sigic_id=event-1</guid><ical:location>Klub GROMKA</ical:location><ical:dtstart>Thu Nov 19 2026 20:00:00 {tz}</ical:dtstart></item>
        <item><title>Predaleč</title><ical:dtstart>Fri Nov 20 2026 20:00:00 {tz}</ical:dtstart></item>
        </channel></rss>
        """);
    List<DateOnly> fetched = [];
    var genres = ParseGenres(XDocument.Parse("""
        <rss><channel>
        <item><guid>https://x.si/z?sigic_id=event-1</guid><category>popularna / popular</category></item>
        <item><guid>https://x.si/j?sigic_id=event-2</guid><category>jazz</category></item>
        <item><guid>https://x.si/no-id</guid><category>jazz</category></item>
        <item><guid>https://x.si/?sigic_id=event-3</guid></item>
        </channel></rss>
        """));
    if (genres.Count != 2) throw new Exception($"genres: {genres.Count}");
    var events = await LoadEvents(today, today.AddDays(UpcomingDays), genres, when => { fetched.Add(when); return Task.FromResult(when == today ? first : second); });
    var html = Render(events, today, today);

    foreach (var expected in new[] { ">19:30</span>", "<strong>Danes &lt;b&gt;</strong>", "<small>Orto</small>", ">do 11. 11.</span>", "href=\"https://x.si/?a=1&amp;b=2\" target=\"_blank\" rel=\"noopener\"", "<h3>petek, 6. november</h3>", "Jutri", "Sredina", "Zadnji", "resize/magic/?url=https%3A%2F%2Fx.si%2Fa%2520b.jpg&amp;size=640x640" })
        if (!html.Contains(expected)) throw new Exception($"missing: {expected}\n{html}");
    foreach (var forbidden in new[] { "Mimo", "Predaleč", "Slab datum", "javascript:", "<b>", ">00:00<" })
        if (html.Contains(forbidden)) throw new Exception($"leaked: {forbidden}\n{html}");
    if (html.Split("<li>").Length - 1 != 5) throw new Exception($"expected 5 rows\n{html}");
    if (!fetched.SequenceEqual([today, new DateOnly(2026, 11, 13)])) throw new Exception($"walk: {string.Join(", ", fetched)}");

    static Event At(string title, string location, string? genre = null) =>
        new(title, title, null, location, null, genre, new DateTimeOffset(2026, 11, 6, 19, 30, 0, TimeSpan.FromHours(1)), default);
    var merged = Merge([
        At("FKK 2: Virtuozno", "Cankarjev dom, Gallusova dvorana"), At("SF, FKK: Virtuozno", "Cankarjev dom", "klasična"),
        At("Božični koncert", "Kulturni dom Medvode"), At("Božični koncert", "Kulturni dom Kamnik"),
        At("Zlati abonma: Dresdenska filharmonija", "Cankarjev dom"), At("Srebrni abonma: Mozart!", "Cankarjev dom"),
        At("Mozart!", "")]);
    if (merged.Count != 6 || merged.Single(e => e.Title.Contains("FKK")) is not { Id: "SF, FKK: Virtuozno", Genre: "klasična" }) throw new Exception($"merge\n{string.Join("\n", merged)}");

    var calls = 0;
    var none = await LoadEvents(today, today.AddDays(UpcomingDays), [], _ => { calls++; return Task.FromResult(new XDocument(new XElement("rss"))); });
    if (calls != 1 || !Render(none, today, today).Contains("Ni dogodkov.")) throw new Exception("empty feed");

    static int Rows(string page) => page.Split("<li>").Length - 1;
    var underground = Render(events, today, today, "underground");
    if (Rows(underground) != 1 || !underground.Contains("<small>Klub GROMKA &#183; popularna</small>")) throw new Exception($"scene filter\n{underground}");
    if (!underground.Contains("href=\"/?scena=underground\" aria-current=\"page\"") || html.Contains("scena=underground\" aria-current")) throw new Exception("active pill");
    if (!underground.Contains("href=\"/?scena=underground&amp;zanr=jazz\"")) throw new Exception("pills must keep the other filter");
    var jazz = Render(events, today, today, zanr: "jazz");
    if (Rows(jazz) != 1 || !jazz.Contains("Jutri") || !jazz.Contains("href=\"/?zanr=jazz\" aria-current=\"page\"")) throw new Exception($"genre filter\n{jazz}");
    var both = Render(events, today, today, "underground", "jazz");
    if (Rows(both) != 0 || !both.Contains("Ni dogodkov.")) throw new Exception($"combined filters\n{both}");
    if (Rows(Render(events, today, today, "nonsense", "nonsense")) != Rows(html)) throw new Exception("unknown filters must show everything");
    if (!both.Contains("name=\"scena\" value=\"underground\"") || !both.Contains("name=\"zanr\" value=\"jazz\"")) throw new Exception("date form must keep the filters");

    if (!html.Contains("<h2>Danes, 5. 11. 2026</h2>") || !html.Contains("<a href=\"/\" aria-current=\"page\">Danes</a>")) throw new Exception($"today\n{html}");
    if (!(html.IndexOf("<h2>V teku") < html.IndexOf("<h2>Danes") && html.IndexOf("<h2>Danes") < html.IndexOf("<h2>Napovedano"))) throw new Exception($"section order\n{html}");
    if (underground.Contains("<h2>V teku")) throw new Exception("empty V teku must be hidden");
    var later = Render(events, new DateOnly(2026, 11, 13), today);
    foreach (var expected in new[] { "<h2>petek, 13. 11. 2026</h2>", "value=\"2026-11-13\"", "<a href=\"/\">Danes</a>", "href=\"/?datum=2026-11-13&amp;scena=underground\"", "Sredina", "Zadnji", "Predaleč" })
        if (!later.Contains(expected)) throw new Exception($"jump missing: {expected}\n{later}");
    if (Rows(later) != 3 || later.Contains("Jutri")) throw new Exception($"jump rows\n{later}");

    foreach (var (input, expected) in new (string?, DateOnly)[] { (null, today), ("nonsense", today), ("9999-12-31", today), ("2026-13-01", today), ("2026-12-01", new DateOnly(2026, 12, 1)) })
        if (ParseDay(input, today) != expected) throw new Exception($"ParseDay({input})");

    var stamp = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    var (from, to) = (today.AddDays(-CalendarPastDays), today.AddDays(CalendarDays));
    var ics = Ics(events, new Scene("all", "Vse", [""]), from, to, stamp);
    if (!ics.StartsWith("BEGIN:VCALENDAR\r\n") || !ics.EndsWith("END:VCALENDAR\r\n") || ics.Replace("\r\n", "").Contains('\n')) throw new Exception($"ics framing\n{ics}");
    if (ics.Split("BEGIN:VEVENT").Length - 1 != 7) throw new Exception($"ics event count\n{ics}");
    foreach (var expected in new[] { "DTSTAMP:20261105T120000Z", "DTSTART:20261105T183000Z\r\nSUMMARY:Danes <b>", "LOCATION:Orto", "DTSTART;VALUE=DATE:20261104\r\nDTEND;VALUE=DATE:20261111", "DTSTART;VALUE=DATE:20261113\r\nSUMMARY:Sredina", "URL:https://x.si/?a=1&b=2", "CATEGORIES:popularna", "Predaleč", "Mimo" })
        if (!ics.Contains(expected)) throw new Exception($"ics missing: {expected}\n{ics}");
    if (ics.Contains("javascript:") || ics.Contains("Slab datum")) throw new Exception($"ics leaked\n{ics}");
    var undergroundIcs = Ics(events, Scenes.All[0], from, to, stamp);
    if (undergroundIcs.Split("BEGIN:VEVENT").Length - 1 != 1 || !undergroundIcs.Contains("X-WR-CALNAME:Glasba · Underground") || !undergroundIcs.Contains("SUMMARY:Zadnji")) throw new Exception($"ics scene\n{undergroundIcs}");
    if (IcsText("a,b;c\\d\ne") != "a\\,b\\;c\\\\d\\ne") throw new Exception("ics escaping");
    var longLine = "SUMMARY:" + new string('č', 60);
    var folded = IcsFold(longLine);
    if (folded.Split("\r\n").Any(l => Encoding.UTF8.GetByteCount(l) > 75) || folded.Replace("\r\n ", "") != longLine + "\r\n") throw new Exception($"ics folding\n{folded}");

    Console.WriteLine("ok");
}

record Event(string Id, string Title, string? Url, string Location, string? Image, string? Genre, DateTimeOffset Start, DateTimeOffset End);

record Scene(string Slug, string Label, string[] Venues)
{
    public bool Has(Event e) => Venues.Any(v => e.Location.Contains(v, StringComparison.OrdinalIgnoreCase));
}

static class Scenes
{
    // Substrings of the feed's location field
    static readonly string[] Metelkova = ["Metelkova", "Gromka", "Channel Zero", "Gala hala", "Menza pri koritu", "Jalla Jalla", "Tiffany", "Monokel", "Alkatraz", "Hostel Celica"];
    static readonly string[] Siska = ["Kino Šiška"];
    static readonly string[] Cvetlicarna = ["Cvetličarna"];

    public static readonly Scene[] All =
    [
        new("underground", "Underground", [.. Metelkova, .. Siska, .. Cvetlicarna, "Orto Bar", "Klub K4"]),
        new("metelkova", "Metelkova", Metelkova),
        new("siska", "Kino Šiška", Siska),
        new("cvetlicarna", "Cvetličarna", Cvetlicarna),
    ];
}
