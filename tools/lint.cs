#!/usr/bin/dotnet run
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

var save = args.Contains("--save");

string GetRepoRoot([System.Runtime.CompilerServices.CallerFilePath] string? path = null) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, ".."));
var repoRoot = GetRepoRoot();
var wikiDir = Path.Combine(repoRoot, "wiki");
var logFile = Path.Combine(wikiDir, "log.md");
var schemaFile = Path.Combine(repoRoot, "CLAUDE.md");

string ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

async Task<string> CallLlmAsync(string prompt, int maxTokens = 4096)
{
    var model = Environment.GetEnvironmentVariable("LLM_MODEL") ?? "gemma4:26b";
    using var client = new HttpClient();

    var requestData = new
    {
        model = model,
        messages = new[] { new { role = "user", content = prompt } },
        max_tokens = maxTokens,
        stream = false
    };

    var json = JsonSerializer.Serialize(requestData);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    // Ollama compatible endpoint
    var url = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434/v1/chat/completions";

    try
    {
        var response = await client.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error calling LLM: {ex.Message}");
        return "";
    }
}

List<string> AllWikiPages()
{
    if (!Directory.Exists(wikiDir)) return new List<string>();
    return Directory.GetFiles(wikiDir, "*.md", SearchOption.AllDirectories)
        .Where(p => {
            var name = Path.GetFileName(p);
            return name != "index.md" && name != "log.md" && name != "lint-report.md";
        }).ToList();
}

List<string> ExtractWikilinks(string content)
{
    var matches = Regex.Matches(content, @"\[\[([^\]]+)\]\]");
    return matches.Select(m => m.Groups[1].Value).ToList();
}

List<string> PageNameToPath(string name, List<string> allPages)
{
    var candidates = new List<string>();
    foreach (var p in allPages)
    {
        var stem = Path.GetFileNameWithoutExtension(p);
        if (stem.Equals(name, StringComparison.OrdinalIgnoreCase) || stem == name)
        {
            candidates.Add(p);
        }
    }
    return candidates;
}

List<string> FindOrphans(List<string> pages)
{
    var inbound = new Dictionary<string, int>();
    foreach (var p in pages) inbound[p] = 0;

    foreach (var p in pages)
    {
        var content = ReadFile(p);
        foreach (var link in ExtractWikilinks(content))
        {
            var resolved = PageNameToPath(link, pages);
            foreach (var r in resolved)
            {
                if (inbound.ContainsKey(r)) inbound[r]++;
            }
        }
    }
    var overviewPath = Path.Combine(wikiDir, "overview.md");
    return pages.Where(p => inbound[p] == 0 && p != overviewPath).ToList();
}

List<(string page, string link)> FindBrokenLinks(List<string> pages)
{
    var broken = new List<(string, string)>();
    foreach (var p in pages)
    {
        var content = ReadFile(p);
        foreach (var link in ExtractWikilinks(content))
        {
            if (PageNameToPath(link, pages).Count == 0)
            {
                broken.Add((p, link));
            }
        }
    }
    return broken;
}

List<string> FindMissingEntities(List<string> pages)
{
    var mentionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var existingPages = new HashSet<string>(pages.Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);

    foreach (var p in pages)
    {
        var content = ReadFile(p);
        foreach (var link in ExtractWikilinks(content))
        {
            if (!existingPages.Contains(link))
            {
                if (!mentionCounts.ContainsKey(link)) mentionCounts[link] = 0;
                mentionCounts[link]++;
            }
        }
    }
    return mentionCounts.Where(kvp => kvp.Value >= 3).Select(kvp => kvp.Key).ToList();
}

void AppendLog(string entry)
{
    var existing = ReadFile(logFile);
    File.WriteAllText(logFile, entry.Trim() + "\n\n" + existing, Encoding.UTF8);
}

var pages = AllWikiPages();
var today = DateTime.Now.ToString("yyyy-MM-dd");

if (pages.Count == 0)
{
    Console.WriteLine("Wiki is empty. Nothing to lint.");
    return;
}

Console.WriteLine($"Linting {pages.Count} wiki pages...");

var orphans = FindOrphans(pages);
var broken = FindBrokenLinks(pages);
var missingEntities = FindMissingEntities(pages);

Console.WriteLine($"  orphans: {orphans.Count}");
Console.WriteLine($"  broken links: {broken.Count}");
Console.WriteLine($"  missing entity pages: {missingEntities.Count}");

var sample = pages.Take(20).ToList();
var pagesContext = new StringBuilder();
foreach (var p in sample)
{
    var rel = Path.GetRelativePath(repoRoot, p);
    var content = ReadFile(p);
    if (content.Length > 1500) content = content.Substring(0, 1500);
    pagesContext.Append($"\n\n### {rel}\n{content}");
}

Console.WriteLine("  running semantic lint via API...");
var prompt = $@"You are linting an LLM Wiki. Review the pages below and identify:
1. Contradictions between pages (claims that conflict)
2. Stale content (summaries that newer sources have superseded)
3. Data gaps (important questions the wiki can't answer — suggest specific sources to find)
4. Concepts mentioned but lacking depth

Wiki pages (sample of {sample.Count} pages):
{pagesContext}

Return a markdown lint report with these sections:
## Contradictions
## Stale Content
## Data Gaps & Suggested Sources
## Concepts Needing More Depth

Be specific — name the exact pages and claims involved.
";

var semanticReport = await CallLlmAsync(prompt, 3000);

var reportLines = new List<string>
{
    $"# Wiki Lint Report — {today}",
    "",
    $"Scanned {pages.Count} pages.",
    "",
    "## Structural Issues",
    ""
};

if (orphans.Count > 0)
{
    reportLines.Add("### Orphan Pages (no inbound links)");
    foreach (var p in orphans) reportLines.Add($"- `{Path.GetRelativePath(repoRoot, p)}`");
    reportLines.Add("");
}

if (broken.Count > 0)
{
    reportLines.Add("### Broken Wikilinks");
    foreach (var (p, link) in broken)
        reportLines.Add($"- `{Path.GetRelativePath(repoRoot, p)}` links to `[[{link}]]` — not found");
    reportLines.Add("");
}

if (missingEntities.Count > 0)
{
    reportLines.Add("### Missing Entity Pages (mentioned 3+ times but no page)");
    foreach (var name in missingEntities) reportLines.Add($"- `[[{name}]]`");
    reportLines.Add("");
}

if (orphans.Count == 0 && broken.Count == 0 && missingEntities.Count == 0)
{
    reportLines.Add("No structural issues found.");
    reportLines.Add("");
}

reportLines.Add("---");
reportLines.Add("");
reportLines.Add(semanticReport);

var report = string.Join("\n", reportLines);
Console.WriteLine("\n" + report);

if (save && !string.IsNullOrWhiteSpace(report))
{
    var reportPath = Path.Combine(wikiDir, "lint-report.md");
    File.WriteAllText(reportPath, report, Encoding.UTF8);
    Console.WriteLine($"\nSaved: {Path.GetRelativePath(repoRoot, reportPath)}");
}

AppendLog($"## [{today}] lint | Wiki health check\n\nRan lint. See lint-report.md for details.");
